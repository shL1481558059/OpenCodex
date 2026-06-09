# OpenCodex Proxy 部署文档

## 本地准备

```bash
cp .env.example .env
mkdir -p logs
dotnet dev-certs https --trust
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet restore opencodex_proxy/OpenCodex.sln
```

编辑 `.env`：

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

启动：

```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet run --launch-profile OpenCodex.Api --project opencodex_proxy/src/Presentation/OpenCodex.Api/OpenCodex.Api.csproj
```

本地后端监听 `https://localhost:8443`。管理台前端启动：

```bash
npm --prefix frontend install
npm --prefix frontend run dev -- --host 127.0.0.1 --port 5173
```

访问 `http://127.0.0.1:5173/admin/`。前端开发服务器会把 `/admin/login`、`/admin/config` 等请求转发到后端真实接口 `/login`、`/config` 等。直接调用后端 API 时不要加 `/admin` 或 `/admin/api` 前缀。

使用 `OPENCODEX_ADMIN_USERNAME` 和 `OPENCODEX_ADMIN_PASSWORD` 调用 `/login` 登录。首次登录后建议先完成两件事：

1. 调用 `/config` 新增自己的上游渠道。渠道里的 `apikey` 是 Windhub、OpenAI 或其他上游服务的 Key。
2. 调用 `/api-keys` 创建访问 API Key。这个 Key 是客户端调用 OpenCodex Proxy 的 Bearer Key，明文只显示一次。

普通用户只能由超级管理员创建。普通用户只能看到自己的渠道、自己的访问 API Key 和自己的请求日志；超级管理员能看到全部。Web Search 模拟只允许超级管理员配置和使用。

也可以把上游 Key 写成 `${WINDHUB_UPSTREAM_API_KEY}`，再在运行服务的环境中提供该变量。它只用于渠道上游鉴权，不是客户端调用代理的访问 API Key。

生产环境必须把 `OPENCODEX_SECRET_KEY` 改成足够随机的值，不要使用示例默认值。

## 快速更新远程镜像

推荐使用本地脚本更新服务器上的容器镜像：

```bash
./scripts/update_remote_image.sh
```

脚本默认会执行以下流程：

1. 在本地使用 `docker buildx build --platform linux/amd64 --push` 构建 x86/amd64 镜像并推送到镜像仓库。管理台前端会在 Docker 构建阶段打包进镜像。
2. 通过 SSH 登录远程服务器。
3. 在远程部署目录中拉取已推送的新镜像；远程服务器不构建镜像。
4. 备份并重写远程 `docker-compose.yml`，把服务统一为 `ocxp`。
5. 移除旧容器 `opencodex-proxy` / `opencodex-proxy-8002`。
6. 执行 `docker compose up -d --no-build --force-recreate --remove-orphans` 重建目标容器。

常用环境变量覆盖：

```bash
REMOTE_USER=admin \
REMOTE_HOST=ssh.shldev.me \
SSH_KEY=/path/to/private-key.pem \
REMOTE_DEPLOY_DIR=/www/wwwroot/ocxp \
IMAGE_NAME=shl148155/opencodexp:ocxp \
SERVICE_NAME=ocxp \
./scripts/update_remote_image.sh
```

不要把真实 SSH 私钥路径、私钥内容或生产 `.env` 写入仓库文档。未设置 `SSH_KEY` 时，脚本会使用本机 SSH agent 或 SSH config。

远程部署目录固定为 `/www/wwwroot/ocxp`。该目录下必须已经存在 `.env`，运行数据继续挂载到 `/www/wwwroot/ocxp/logs`。管理台静态文件随 Docker 镜像进入容器，不再同步到宿主机目录。脚本会备份并重写 `/www/wwwroot/ocxp/docker-compose.yml`，移除旧容器 `opencodex-proxy` / `opencodex-proxy-8002`，并启动新容器 `ocxp`。

## 手动 x86/amd64 Docker 镜像

`Dockerfile` 只发布 .NET 10 API，最终镜像通过 `dotnet OpenCodex.Api.dll` 运行。

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
  --name ocxp \
  --env-file .env \
  -v "$PWD/logs:/app/logs" \
  shl148155/opencodexp:ocxp
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
base_url = "<OpenCodex Proxy 地址>/v1"
env_key = "OPENCODEX_ACCESS_API_KEY"
wire_api = "responses"
requires_openai_auth = false
EOF
```

把管理接口创建出来的访问 API Key 放入客户端环境变量，然后运行测试：

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
