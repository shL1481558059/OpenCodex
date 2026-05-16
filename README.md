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
cp config.example.json config.json
.venv/bin/python -m opencodex_proxy
```

`.env` 管系统配置和 admin 密码，`config.json` 管渠道与路由。

## `.env`

```env
OPENCODEX_HOST=0.0.0.0
OPENCODEX_PORT=8000
OPENCODEX_ADMIN_PASSWORD=change-me
OPENCODEX_CONFIG_PATH=config.json
OPENCODEX_LOG_PATH=logs/opencodex.log
OPENCODEX_LOG_LEVEL=INFO
OPENCODEX_LOG_VIEW_LEVEL=BASIC
OPENCODEX_DEFAULT_TIMEOUT=120
OPENCODEX_SECRET_KEY=change-me-session-secret
```

日志展示等级：

- `BASIC`：时间、等级、路径、入口协议、模型、渠道、状态码、耗时、错误摘要。
- `DEBUG`：增加路由命中、模型改写、兼容规则、脱敏参数、上游错误摘要。
- `TRACE`：增加脱敏后的请求和响应正文片段，仅建议本地临时排障。

所有日志展示都会脱敏 `Authorization`、`api_key`、`apikey`、`x-api-key`、cookie 和密码。

## Docker

```bash
docker buildx build --platform linux/amd64 -t opencodex-proxy:windhub-amd64 --load .
docker run --rm -p 8000:8000 \
  --platform linux/amd64 \
  --env-file .env \
  -v "$PWD/config.json:/app/config/config.json" \
  -v "$PWD/logs:/app/logs" \
  opencodex-proxy:windhub-amd64
```

完整部署步骤见 [DEPLOYMENT.md](/Users/w/shL/work/shl/OpenCodex/DEPLOYMENT.md)。

## Codex CLI

Codex CLI 会通过 `/v1/responses` 使用 `stream=true` 和工具调用。代理会合成 Responses SSE，并按渠道转换到上游协议。

临时隔离测试示例：

```toml
model = "mimo-v2.5-pro"
model_provider = "opencodex-local"
approval_policy = "never"
sandbox_mode = "workspace-write"

[model_providers.opencodex-local]
name = "OpenCodex Local Proxy"
base_url = "http://127.0.0.1:8000/v1"
env_key = "WINDHUB_API_KEY"
wire_api = "responses"
requires_openai_auth = false
```

Windhub 的 `mimo-v2.5-pro` 已在示例配置中统一路由到 `messages` 渠道。实测其 `chat` 渠道文本请求可用，但工具结果续轮会因上游 `reasoning_content` 校验返回 400 或偶发 500；`messages` 渠道在保留 thinking block 后可完成工具调用闭环。

## 测试

```bash
.venv/bin/python -m unittest discover -s tests
```
