# OpenCodex Proxy

轻量协议中转服务，当前迁移运行时为 .NET 10 / ASP.NET Core，提供：

- `/v1/responses`
- `/v1/chat/completions`
- `/v1/messages`
- 管理接口：`/session`、`/login`、`/logout`、`/config`、`/api-keys`、`/users`、`/logs`、`/stats`、`/web-search` 等

运行时依赖尽量少：ASP.NET Core、SQLite 和内置 HTTP 客户端。旧 Python 后端已移除；行为追溯以 Git 历史和 `.NET` 测试为准。

`/v1/responses` 支持 Codex CLI 使用的 `stream=true`：代理会对上游发起非流式请求，并合成 Responses SSE 事件返回。

## 本地运行

```bash
cp .env.example .env
mkdir -p logs
dotnet dev-certs https --trust
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet run --launch-profile OpenCodex.Api --project opencodex_proxy/src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj
```

后端本地监听 `https://localhost:8443`。`.env` 管系统配置和超级管理员账号密码；.NET 运行时会读取当前目录 `.env`，环境变量仍优先。渠道配置、普通用户和访问 API Key 通过管理接口写入 SQLite。

管理台前端：

```bash
npm --prefix frontend install
npm --prefix frontend run dev -- --host 127.0.0.1 --port 5173
```

访问 `http://127.0.0.1:5173/admin/`。前端开发服务器会把浏览器里的 `/admin/login`、`/admin/config` 等请求转发到后端真实接口 `/login`、`/config` 等；直接调用后端 API 时不要加 `/admin` 或 `/admin/api` 前缀。

## `.env`

```env
OPENCODEX_ADMIN_USERNAME=admin
OPENCODEX_ADMIN_PASSWORD=change-me
OPENCODEX_DB_PATH=logs/opencodex.db
OPENCODEX_LOG_PATH=logs/opencodex.log
OPENCODEX_LOG_LEVEL=INFO
OPENCODEX_LOG_VIEW_LEVEL=BASIC
OPENCODEX_DEFAULT_TIMEOUT=120
OPENCODEX_SECRET_KEY=change-me-session-secret
TZ=Asia/Shanghai
```

`OPENCODEX_ADMIN_USERNAME` 和 `OPENCODEX_ADMIN_PASSWORD` 是环境变量超级管理员。超级管理员不能通过管理接口降级或删除，密码以环境变量为准；普通用户只能由超级管理员创建、停用和重置密码。

`OPENCODEX_SECRET_KEY` 用于会话相关安全配置，生产环境不要使用示例默认值。

## 用户与访问 API Key

先调用 `/login` 登录，或通过管理台登录。登录后：

- 超级管理员可以查看和管理所有用户、所有渠道、所有访问 API Key 元数据和所有请求日志。
- 普通用户只能查看和管理自己的渠道、自己的访问 API Key 和自己的请求日志。
- Web Search 模拟只对超级管理员开放：普通用户不能配置，普通用户的 `/v1/responses` 请求即使声明 `web_search` 也不会触发本地 Tavily 模拟。

调用 `/v1/responses`、`/v1/chat/completions`、`/v1/messages` 必须携带管理接口创建的访问 API Key：

```http
Authorization: Bearer ocx_...
```

访问 API Key 是 OpenCodex Proxy 的调用凭证，不是上游模型服务的 Key。它只在创建成功时显示一次明文，数据库只保存哈希；停用或删除后立即不能继续调用。后端会按这个 Key 识别用户，并且只在该用户自己的渠道中路由请求。普通用户没有启用渠道时会返回 `no enabled channels configured`，不会回退使用超级管理员渠道。

上游模型服务 Key 仍然放在渠道配置的 `apikey` 或自定义 headers 中。代理不会把客户端传入的访问 API Key 透传给上游；需要上游认证时使用 `auth_mode=config` 并在渠道里配置上游 Key。

日志展示等级：

- `BASIC`：时间、等级、路径、入口协议、模型、渠道、状态码、耗时、错误摘要。
- `DEBUG`：增加上游模型、兼容规则、脱敏参数、上游错误摘要。
- `TRACE`：增加脱敏后的请求和响应正文片段，仅建议本地临时排障。

所有日志展示都会脱敏 `Authorization`、`api_key`、`apikey`、`x-api-key`、cookie 和密码。

## Docker

```bash
docker buildx build --platform linux/amd64 -t shl148155/opencodexp:ocxp --push .
docker pull shl148155/opencodexp:ocxp
docker run --rm \
  --platform linux/amd64 \
  --env-file .env \
  -v "$PWD/logs:/app/logs" \
  shl148155/opencodexp:ocxp
```

完整部署步骤见 [DEPLOYMENT.md](DEPLOYMENT.md)。

## Codex CLI

Codex CLI 会通过 `/v1/responses` 使用 `stream=true` 和工具调用。代理会合成 Responses SSE，并按渠道服务类型自动转换到上游协议；入口协议和渠道服务类型不一致时不需要额外配置。

临时隔离测试示例：

```toml
model = "mimo-v2.5-pro"
model_provider = "opencodex-local"
approval_policy = "never"
sandbox_mode = "workspace-write"

[model_providers.opencodex-local]
name = "OpenCodex Local Proxy"
base_url = "<OpenCodex Proxy 地址>/v1"
env_key = "OPENCODEX_ACCESS_API_KEY"
wire_api = "responses"
requires_openai_auth = false
```

其中 `OPENCODEX_ACCESS_API_KEY` 是通过 `/api-keys` 或管理台创建的访问 Key。Windhub、OpenAI 或其他上游服务的 Key 应配置在对应渠道的 `apikey` 字段中，不再作为客户端调用代理的环境变量。

Windhub 的 `mimo-v2.5-pro` 建议通过管理接口把服务类型配置为 `messages`，并放在渠道列表首位。上游协议严格由渠道的服务类型决定：配置为 `chat` 就走 `/v1/chat/completions`，配置为 `responses` 就走 `/v1/responses`，配置为 `messages` 就走 `/v1/messages`。实测其 `chat` 渠道文本请求可用，但工具结果续轮会因上游 `reasoning_content` 校验返回 400 或偶发 500；`messages` 渠道在保留 thinking block 后可完成工具调用闭环。

## 测试

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test opencodex_proxy/OpenCodex.sln
```
