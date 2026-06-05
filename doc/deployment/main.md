# 部署与运行说明

## 4.1 环境依赖

### Python 依赖

`requirements.txt` 当前包含：

- `Flask>=3.0,<4`
- `python-dotenv>=1.0,<2`

### 运行时环境变量

| 环境变量 | 默认值 | 说明 |
| --- | --- | --- |
| `OPENCODEX_HOST` | `0.0.0.0` | 服务监听地址 |
| `OPENCODEX_PORT` | `8000` | 服务端口 |
| `OPENCODEX_ADMIN_USERNAME` | `admin` | 环境变量超级管理员用户名 |
| `OPENCODEX_ADMIN_PASSWORD` | 无 | 必填，超级管理员密码 |
| `OPENCODEX_DB_PATH` | `logs/opencodex.db` | SQLite 数据库路径 |
| `OPENCODEX_LOG_PATH` | `logs/opencodex.log` | JSON 日志文件路径 |
| `OPENCODEX_LOG_LEVEL` | `INFO` | 日志等级 |
| `OPENCODEX_LOG_VIEW_LEVEL` | `BASIC` | 管理台日志展示等级 |
| `OPENCODEX_DEFAULT_TIMEOUT` | `120` | 默认上游请求超时，单位秒 |
| `OPENCODEX_SECRET_KEY` | `change-me-session-secret` | Flask Session 密钥 |

`OPENCODEX_ADMIN_PASSWORD` 必须配置，否则应用启动会失败。

## 4.2 安装部署步骤

### 本地运行

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
cp .env.example .env
.venv/bin/python -m opencodex_proxy
```

启动后访问：

- 管理台：`http://127.0.0.1:8000/admin`
- 代理基础地址：`http://127.0.0.1:8000/v1`

### Docker 构建

`Dockerfile` 使用两阶段构建：

1. `node:24-slim` 构建前端管理台。
2. `python:3.12-slim` 安装 Python 依赖并复制后端和前端构建产物。

示例：

```bash
docker buildx build --platform linux/amd64 -t shl148155/opencodexp:latest --push .
docker pull shl148155/opencodexp:latest
docker run --rm -p 8000:8000 \
  --platform linux/amd64 \
  --env-file .env \
  -v "$PWD/logs:/app/logs" \
  shl148155/opencodexp:latest
```

## 4.3 启动与运行方式

### Python 入口

`opencodex_proxy/__main__.py` 调用 `opencodex_proxy.app:main`。

应用启动流程：

1. `Settings.from_env` 读取 `.env` 和环境变量。
2. `create_app` 初始化 Flask 应用、日志、数据库、超级管理员和配置管理器。
3. `app.run` 使用配置的 host 和 port 启动。

### 当前部署服务器信息

来自项目长期规则：

| 项 | 值 |
| --- | --- |
| SSH | `admin@ssh.shldev.me:22` |
| 本地私钥 | `/Users/w/.ssh/LightsailDefaultKey-ap-northeast-2.pem` |
| 服务器部署目录 | `/www/wwwroot/opencodex-proxy` |
| 容器名 | `opencodex-proxy` |
| 镜像名 | `opencodex-proxy:test` |
| 服务端口 | `127.0.0.1:8000 -> 8000` |

### 远程镜像更新脚本

脚本：`scripts/update_remote_image.sh`

默认行为：

1. 在本地执行 Docker buildx 构建并推送镜像。
2. SSH 到远程服务器。
3. 拉取镜像。
4. 更新 compose 文件中的服务镜像。
5. 重建指定容器。

可用环境变量覆盖：

```bash
SSH_KEY=/path/to/key IMAGE_NAME=opencodex-proxy:test ./scripts/update_remote_image.sh
```

## 4.4 常见问题与解决方案

### 启动时报 `OPENCODEX_ADMIN_PASSWORD is required`

原因：未配置超级管理员密码。

处理：在 `.env` 或环境变量中设置 `OPENCODEX_ADMIN_PASSWORD`。

### 管理台能登录，但代理接口返回 401

原因：管理台登录 Session 和代理访问 API Key 是两套认证。代理接口必须使用管理台创建的访问 API Key。

处理：在管理台创建 API Key，然后请求中添加：

```http
Authorization: Bearer ocx_...
```

### 普通用户请求返回 `no enabled channels configured`

原因：普通用户没有自己的启用渠道。

处理：为该用户创建并启用渠道，或使用该用户自己的配置导入渠道。

### 上游返回 400 或 500

可能原因：

- 渠道类型与上游真实协议不一致。
- 上游不支持某些参数。
- 模型名没有正确映射到上游模型。
- 工具调用续轮缺少 reasoning/thinking 上下文。

处理：

- 检查渠道 `type`、`baseurl`、`models`。
- 使用 compat 的 `drop_params`、`rename_params`、`unsupported_params`。
- 对需要 thinking 的渠道开启 `fallback_thinking_on_tool_use`。
- 在管理台查看日志详情中的上游请求体和上游响应体。

### Web Search 没有触发

可能原因：

- 当前用户不是超级管理员。
- Web Search 配置未启用。
- 请求入口不是 `/v1/responses`。
- 上游渠道不是 `chat` 或 `messages`。
- 请求没有声明 `{"type": "web_search"}` 工具。

## 推荐测试命令

```bash
.venv/bin/python -m unittest discover -s tests
```

