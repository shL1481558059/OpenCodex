# OpenCodex Proxy 部署文档

## 结论

Windhub 的 `mimo-v2.5-pro` 在本项目中统一走 `/v1/messages` 上游。

原因：本地实测 Windhub `/v1/messages` 可以完成工具调用续轮；`/v1/chat/completions` 文本请求可用，但工具结果续轮会触发 `reasoning_content` 兼容错误或偶发 500。

## 本地准备

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
cp .env.example .env
mkdir -p logs
```

编辑 `.env`：

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

启动：

```bash
.venv/bin/python -m opencodex_proxy
```

管理后台：

```text
http://127.0.0.1:8000/admin
```

使用 `OPENCODEX_ADMIN_USERNAME` 和 `OPENCODEX_ADMIN_PASSWORD` 登录。首次登录后建议先完成两件事：

1. 在“渠道配置”中新增自己的上游渠道。渠道里的 `apikey` 是 Windhub、OpenAI 或其他上游服务的 Key。
2. 在“API Key 管理”中创建访问 API Key。这个 Key 是客户端调用 OpenCodex Proxy 的 Bearer Key，明文只显示一次。

普通用户只能由超级管理员创建。普通用户只能看到自己的渠道、自己的访问 API Key 和自己的请求日志；超级管理员能看到全部。Web Search 模拟只允许超级管理员配置和使用。

## Windhub MiMo 配置

在管理台新增 `windhub-mimo-messages` 渠道，并将它放在渠道列表首位。

核心配置：

```json
{
  "id": "windhub-mimo-messages",
  "type": "messages",
  "baseurl": "https://windhub.cc",
  "apikey": "sk-your-windhub-upstream-key",
  "auth_mode": "config"
}
```

也可以把上游 Key 写成 `${WINDHUB_UPSTREAM_API_KEY}`，再在运行服务的环境中提供该变量。它只用于渠道上游鉴权，不是客户端调用代理的访问 API Key。

## x86/amd64 Docker 镜像

本地构建并推送 linux/amd64 镜像：

```bash
docker buildx build --platform linux/amd64 -t shl148155/opencodexp:latest --push .
```

服务器只拉取镜像，不在服务器构建：

```bash
docker pull shl148155/opencodexp:latest
```

运行容器：

```bash
mkdir -p logs

docker run --rm \
  --platform linux/amd64 \
  --name opencodex-proxy \
  -p 8000:8000 \
  --env-file .env \
  -v "$PWD/logs:/app/logs" \
  shl148155/opencodexp:latest
```

如果 `.env` 里使用容器路径，请保持：

```env
OPENCODEX_DB_PATH=/app/logs/opencodex.db
OPENCODEX_LOG_PATH=/app/logs/opencodex.log
```

## Codex CLI 隔离测试

不要修改用户现有 Codex CLI 配置。创建临时目录：

```bash
mkdir -p /tmp/opencodex-codex-home
cat >/tmp/opencodex-codex-home/config.toml <<'EOF'
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
EOF
```

把管理台创建出来的访问 API Key 放入客户端环境变量，然后运行测试：

```bash
CODEX_HOME=/tmp/opencodex-codex-home \
OPENCODEX_ACCESS_API_KEY="ocx_..." \
codex exec "使用工具创建一个名为 opencodex_tool_test.txt 的文件，内容为 OK，然后确认文件存在。"
```

测试结束后删除临时目录即可：

```bash
rm -rf /tmp/opencodex-codex-home
```

## 日志展示等级

- `BASIC`：时间、等级、路径、入口协议、模型、渠道、状态码、耗时、错误摘要。
- `DEBUG`：增加上游模型、兼容规则、脱敏参数、上游错误摘要。
- `TRACE`：增加脱敏后的请求和响应正文片段，仅建议本地临时排障。

所有等级都会脱敏 `Authorization`、`api_key`、`apikey`、`x-api-key`、cookie 和密码。
