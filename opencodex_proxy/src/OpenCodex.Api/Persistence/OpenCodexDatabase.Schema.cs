namespace OpenCodex.Api.Persistence;

public static partial class OpenCodexDatabase
{
    private const string RequestLogsSchema = """
        CREATE TABLE IF NOT EXISTS request_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            request_id TEXT,
            created_at REAL,
            method TEXT,
            path TEXT,
            client_ip TEXT,
            model TEXT,
            upstream_model TEXT,
            channel_id TEXT,
            is_stream INTEGER DEFAULT 0,
            ttft_ms INTEGER,
            duration_ms INTEGER,
            status_code INTEGER,
            input_tokens INTEGER,
            cached_tokens INTEGER,
            output_tokens INTEGER,
            cost REAL,
            owner_username TEXT NOT NULL DEFAULT 'admin',
            api_key_id INTEGER,
            error TEXT
        );
        """;

    private const string RequestLogsIndexesSchema = """
        CREATE INDEX IF NOT EXISTS idx_request_logs_owner_id ON request_logs(owner_username, id);
        CREATE INDEX IF NOT EXISTS idx_request_logs_created_at ON request_logs(created_at);
        CREATE INDEX IF NOT EXISTS idx_request_logs_model ON request_logs(model);
        CREATE INDEX IF NOT EXISTS idx_request_logs_upstream_model ON request_logs(upstream_model);
        CREATE INDEX IF NOT EXISTS idx_request_logs_channel_id ON request_logs(channel_id);
        CREATE INDEX IF NOT EXISTS idx_request_logs_path ON request_logs(path);
        CREATE INDEX IF NOT EXISTS idx_request_logs_status_code ON request_logs(status_code);
        CREATE INDEX IF NOT EXISTS idx_request_logs_api_key_id ON request_logs(api_key_id);
        """;

    private const string RequestLogDetailsSchema = """
        CREATE TABLE IF NOT EXISTS request_log_details (
            log_id INTEGER PRIMARY KEY,
            request_headers TEXT,
            request_body TEXT,
            upstream_request_body TEXT,
            upstream_response_body TEXT,
            response_body TEXT,
            web_search_json TEXT
        );
        """;

    private const string ChannelsSchema = """
        CREATE TABLE IF NOT EXISTS channels (
            owner_username TEXT NOT NULL DEFAULT 'admin',
            id TEXT NOT NULL,
            position INTEGER NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            type TEXT NOT NULL,
            baseurl TEXT NOT NULL,
            apikey TEXT NOT NULL DEFAULT '',
            auth_mode TEXT NOT NULL DEFAULT 'config',
            headers_json TEXT NOT NULL DEFAULT '{}',
            timeout_seconds INTEGER NOT NULL,
            retry_count INTEGER NOT NULL DEFAULT 3,
            compat_json TEXT NOT NULL DEFAULT '{}',
            models_json TEXT NOT NULL DEFAULT '[]',
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL,
            PRIMARY KEY (owner_username, id)
        );
        """;

    private const string UsersSchema = """
        CREATE TABLE IF NOT EXISTS users (
            username TEXT PRIMARY KEY,
            password_hash TEXT NOT NULL,
            role TEXT NOT NULL DEFAULT 'user',
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS access_api_keys (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            owner_username TEXT NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            key_hash TEXT NOT NULL UNIQUE,
            key_plaintext TEXT,
            key_prefix TEXT NOT NULL,
            key_suffix TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL,
            last_used_at REAL,
            FOREIGN KEY (owner_username) REFERENCES users(username)
        );

        CREATE INDEX IF NOT EXISTS idx_access_api_keys_owner ON access_api_keys(owner_username, id);
        """;

    private const string WebSearchSchema = """
        CREATE TABLE IF NOT EXISTS web_search_settings (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            enabled INTEGER NOT NULL DEFAULT 0,
            key_usage_limit INTEGER NOT NULL DEFAULT 1000,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tavily_keys (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            position INTEGER NOT NULL,
            provider TEXT NOT NULL DEFAULT 'tavily',
            api_key TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            usage_count INTEGER NOT NULL DEFAULT 0,
            usage_limit INTEGER NOT NULL DEFAULT 1000,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_tavily_keys_position ON tavily_keys(position);
        """;

    private static readonly string Schema = string.Join(
        Environment.NewLine,
        RequestLogsSchema,
        RequestLogDetailsSchema,
        ChannelsSchema,
        UsersSchema,
        WebSearchSchema);
}
