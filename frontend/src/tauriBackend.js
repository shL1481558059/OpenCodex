export function isTauriRuntime() {
  return Boolean(window.__TAURI_INTERNALS__ || window.__TAURI__);
}

export async function restartBackend() {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke("restart_backend");
}
