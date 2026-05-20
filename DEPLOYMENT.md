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
OPENCODEX_ADMIN_PASSWORD=change-me
OPENCODEX_DB_PATH=logs/opencodex.db
OPENCODEX_LOG_PATH=logs/opencodex.log
OPENCODEX_LOG_LEVEL=INFO
OPENCODEX_LOG_VIEW_LEVEL=BASIC
OPENCODEX_DEFAULT_TIMEOUT=120
OPENCODEX_SECRET_KEY=change-me-session-secret
WINDHUB_API_KEY=sk-your-key
```

启动：

```bash
.venv/bin/python -m opencodex_proxy
```

管理后台：

```text
http://127.0.0.1:8000/admin
```

## Windhub MiMo 配置

在管理台新增 `windhub-mimo-messages` 渠道，并将它放在渠道列表首位。

核心配置：

```json
{
  "id": "windhub-mimo-messages",
  "type": "messages",
  "baseurl": "https://windhub.cc",
  "apikey": "${WINDHUB_API_KEY}",
  "auth_mode": "config"
}
```

## x86/amd64 Docker 镜像

构建 linux/amd64 镜像：

```bash
docker buildx build --platform linux/amd64 -t opencodex-proxy:test --load .
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
  opencodex-proxy:test
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
env_key = "WINDHUB_API_KEY"
wire_api = "responses"
requires_openai_auth = false
EOF
```

运行测试时只给该进程指定临时 `CODEX_HOME`：

```bash
CODEX_HOME=/tmp/opencodex-codex-home \
WINDHUB_API_KEY="$WINDHUB_API_KEY" \
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
