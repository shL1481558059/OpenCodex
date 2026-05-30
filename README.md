# OpenCodex Proxy

轻量 Python 协议中转服务，用 Flask 提供：

- `/v1/responses`
- `/v1/chat/completions`
- `/v1/messages`
- `/admin` 渠道配置与日志页面

运行时依赖尽量少：`Flask` 和 `python-dotenv`。

`/v1/responses` 支持 Codex CLI 使用的 `stream=true`：代理会对上游发起非流式请求，并合成 Responses SSE 事件返回。

## 本地运行

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
cp .env.example .env
.venv/bin/python -m opencodex_proxy
```

`.env` 管系统配置和超级管理员账号密码，渠道配置、普通用户和访问 API Key 通过 `/admin` 写入 SQLite。

## `.env`

```env
OPENCODEX_HOST=0.0.0.0
OPENCODEX_PORT=8000
OPENCODEX_ADMIN_USERNAME=admin
OPENCODEX_ADMIN_PASSWORD=change-me
OPENCODEX_DB_PATH=logs/opencodex.db
OPENCODEX_LOG_PATH=logs/opencodex.log
OPENCODEX_LOG_LEVEL=INFO
OPENCODEX_LOG_VIEW_LEVEL=BASIC
OPENCODEX_DEFAULT_TIMEOUT=120
OPENCODEX_SECRET_KEY=change-me-session-secret
```

`OPENCODEX_ADMIN_USERNAME` 和 `OPENCODEX_ADMIN_PASSWORD` 是环境变量超级管理员。超级管理员不能在管理台降级或删除，密码以环境变量为准；普通用户只能由超级管理员创建、停用和重置密码。

## 用户与访问 API Key

管理台登录地址是 `/admin`。登录后：

- 超级管理员可以查看和管理所有用户、所有渠道、所有访问 API Key 元数据和所有请求日志。
- 普通用户只能查看和管理自己的渠道、自己的访问 API Key 和自己的请求日志。
- Web Search 模拟只对超级管理员开放：普通用户不能配置，普通用户的 `/v1/responses` 请求即使声明 `web_search` 也不会触发本地 Tavily 模拟。

调用 `/v1/responses`、`/v1/chat/completions`、`/v1/messages` 必须携带管理台创建的访问 API Key：

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
docker buildx build --platform linux/amd64 -t shl148155/opencodexp:latest --push .
docker pull shl148155/opencodexp:latest
docker run --rm -p 8000:8000 \
  --platform linux/amd64 \
  --env-file .env \
  -v "$PWD/logs:/app/logs" \
  shl148155/opencodexp:latest
```

完整部署步骤见 [DEPLOYMENT.md](/Users/w/shL/work/shl/OpenCodex/DEPLOYMENT.md)。

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
base_url = "http://127.0.0.1:8000/v1"
env_key = "OPENCODEX_ACCESS_API_KEY"
wire_api = "responses"
requires_openai_auth = false
```

其中 `OPENCODEX_ACCESS_API_KEY` 是在管理台“API Key 管理”里创建的访问 Key。Windhub、OpenAI 或其他上游服务的 Key 应配置在对应渠道的 `apikey` 字段中，不再作为客户端调用代理的环境变量。

Windhub 的 `mimo-v2.5-pro` 建议在管理台直接把服务类型配置为 `messages`，并放在渠道列表首位。上游协议严格由渠道的服务类型决定：配置为 `chat` 就走 `/v1/chat/completions`，配置为 `responses` 就走 `/v1/responses`，配置为 `messages` 就走 `/v1/messages`。实测其 `chat` 渠道文本请求可用，但工具结果续轮会因上游 `reasoning_content` 校验返回 400 或偶发 500；`messages` 渠道在保留 thinking block 后可完成工具调用闭环。

## 测试

```bash
.venv/bin/python -m unittest discover -s tests
```
