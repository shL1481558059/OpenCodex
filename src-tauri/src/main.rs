#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use serde::{Deserialize, Serialize};
use std::{
    fs,
    net::TcpStream,
    path::PathBuf,
    sync::Mutex,
    thread,
    time::{Duration, Instant},
};
use tauri::{AppHandle, Manager, State, WebviewUrl, WebviewWindowBuilder};
use tauri_plugin_shell::{
    process::{CommandChild, CommandEvent},
    ShellExt,
};

const LOCALHOST_MODE: &str = "localhost";
const LAN_MODE: &str = "lan";
const LOCALHOST_BIND_HOST: &str = "127.0.0.1";
const LAN_BIND_HOST: &str = "0.0.0.0";
const DEFAULT_PORT: u16 = 18080;

#[derive(Default)]
struct BackendState {
    child: Mutex<Option<CommandChild>>,
}

#[derive(Clone, Deserialize, Serialize)]
struct DesktopSettings {
    access_mode: String,
    bind_host: String,
    port: u16,
}

#[derive(Serialize)]
struct RestartBackendResponse {
    admin_url: String,
}

impl Default for DesktopSettings {
    fn default() -> Self {
        Self {
            access_mode: LOCALHOST_MODE.to_string(),
            bind_host: LOCALHOST_BIND_HOST.to_string(),
            port: DEFAULT_PORT,
        }
    }
}

#[tauri::command]
fn restart_backend(
    app: AppHandle,
    state: State<'_, BackendState>,
) -> Result<RestartBackendResponse, String> {
    stop_backend(state.inner());
    let settings = start_backend(&app, state.inner())?;
    Ok(RestartBackendResponse {
        admin_url: admin_url(settings.port),
    })
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .manage(BackendState::default())
        .invoke_handler(tauri::generate_handler![restart_backend])
        .setup(|app| {
            let handle = app.handle().clone();
            let state = app.state::<BackendState>();
            let settings = start_backend(&handle, state.inner())?;
            open_admin_window(&handle, settings.port)?;
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("failed to run OpenCodex desktop");
}

fn start_backend(app: &AppHandle, state: &BackendState) -> Result<DesktopSettings, String> {
    let settings = load_or_create_settings(app)?;
    let data_dir = app
        .path()
        .app_data_dir()
        .map_err(|error| error.to_string())?;
    let logs_dir = data_dir.join("logs");
    let keys_dir = data_dir.join("keys");
    let ocr_dir = data_dir.join("ocr-cache");
    fs::create_dir_all(&logs_dir).map_err(|error| error.to_string())?;
    fs::create_dir_all(&keys_dir).map_err(|error| error.to_string())?;
    fs::create_dir_all(&ocr_dir).map_err(|error| error.to_string())?;

    let settings_path = settings_path(app)?;
    let content_root = content_root(app)?;
    let urls = format!("http://{}:{}", settings.bind_host, settings.port);
    let db_connection = format!("Data Source={}", logs_dir.join("opencodex.db").display());
    let log_path = logs_dir.join("opencodex.log").display().to_string();
    let keys_path = keys_dir.display().to_string();
    let ocr_path = ocr_dir.display().to_string();

    let command = app
        .shell()
        .sidecar("opencodex-api")
        .map_err(|error| error.to_string())?
        .env("ASPNETCORE_ENVIRONMENT", "Production")
        .env("ASPNETCORE_URLS", urls)
        .env("OPENCODEX_CONTENT_ROOT", content_root.display().to_string())
        .env("OPENCODEX_DISABLE_DOTENV", "true")
        .env("OPENCODEX_DESKTOP_SETTINGS_PATH", settings_path.display().to_string())
        .env("OPENCODEX_DESKTOP_BIND_HOST", settings.bind_host.clone())
        .env("OPENCODEX_DESKTOP_PORT", settings.port.to_string())
        .env("OPENCODEX_DB_PROVIDER", "sqlite")
        .env("OPENCODEX_DB_CONNECTION_STRING", db_connection)
        .env("OPENCODEX_LOG_PATH", log_path)
        .env("OPENCODEX_DATA_PROTECTION_KEYS_PATH", keys_path)
        .env("OPENCODEX_OCR_CACHE_DIR", ocr_path);

    let (mut rx, child) = command.spawn().map_err(|error| error.to_string())?;
    tauri::async_runtime::spawn(async move {
        while let Some(event) = rx.recv().await {
            match event {
                CommandEvent::Stdout(line) => {
                    println!("[opencodex-api] {}", String::from_utf8_lossy(&line));
                }
                CommandEvent::Stderr(line) => {
                    eprintln!("[opencodex-api] {}", String::from_utf8_lossy(&line));
                }
                CommandEvent::Terminated(payload) => {
                    println!("[opencodex-api] terminated: {:?}", payload);
                }
                _ => {}
            }
        }
    });

    *state.child.lock().map_err(|error| error.to_string())? = Some(child);
    wait_for_backend(settings.port)?;
    Ok(settings)
}

fn stop_backend(state: &BackendState) {
    if let Ok(mut guard) = state.child.lock() {
        if let Some(mut child) = guard.take() {
            let _ = child.kill();
        }
    }
}

fn open_admin_window(app: &AppHandle, port: u16) -> Result<(), String> {
    let url = tauri::Url::parse(&admin_url(port)).map_err(|error| error.to_string())?;
    WebviewWindowBuilder::new(app, "main", WebviewUrl::External(url))
        .title("OpenCodex")
        .inner_size(1280.0, 820.0)
        .min_inner_size(960.0, 640.0)
        .build()
        .map_err(|error| error.to_string())?;
    Ok(())
}

fn wait_for_backend(port: u16) -> Result<(), String> {
    let started_at = Instant::now();
    while started_at.elapsed() < Duration::from_secs(15) {
        if TcpStream::connect((LOCALHOST_BIND_HOST, port)).is_ok() {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(250));
    }

    Err(format!("OpenCodex backend did not start on port {}", port))
}

fn load_or_create_settings(app: &AppHandle) -> Result<DesktopSettings, String> {
    let path = settings_path(app)?;
    if path.exists() {
        if let Ok(content) = fs::read_to_string(&path) {
            if let Ok(settings) = serde_json::from_str::<DesktopSettings>(&content) {
                let normalized = normalize_settings(settings);
                write_settings(&path, &normalized)?;
                return Ok(normalized);
            }
        }
    }

    let settings = DesktopSettings::default();
    write_settings(&path, &settings)?;
    Ok(settings)
}

fn write_settings(path: &PathBuf, settings: &DesktopSettings) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let content = serde_json::to_string_pretty(settings).map_err(|error| error.to_string())?;
    fs::write(path, content).map_err(|error| error.to_string())
}

fn settings_path(app: &AppHandle) -> Result<PathBuf, String> {
    let config_dir = app
        .path()
        .app_config_dir()
        .map_err(|error| error.to_string())?;
    Ok(config_dir.join("desktop-settings.json"))
}

fn content_root(app: &AppHandle) -> Result<PathBuf, String> {
    if let Ok(resource_dir) = app.path().resource_dir() {
        return Ok(resource_dir);
    }

    Ok(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("resources"))
}

fn normalize_settings(settings: DesktopSettings) -> DesktopSettings {
    let access_mode = if settings.access_mode == LAN_MODE || settings.bind_host == LAN_BIND_HOST {
        LAN_MODE
    } else {
        LOCALHOST_MODE
    };
    let port = if (1024..=65535).contains(&settings.port) {
        settings.port
    } else {
        DEFAULT_PORT
    };

    DesktopSettings {
        access_mode: access_mode.to_string(),
        bind_host: if access_mode == LAN_MODE {
            LAN_BIND_HOST.to_string()
        } else {
            LOCALHOST_BIND_HOST.to_string()
        },
        port,
    }
}

fn admin_url(port: u16) -> String {
    format!("http://{}:{}/admin/", LOCALHOST_BIND_HOST, port)
}
