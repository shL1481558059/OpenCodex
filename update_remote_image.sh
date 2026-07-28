#!/usr/bin/env bash
set -euo pipefail

# 数据库类型: sqlite 或 postgres
DB_TYPE="${DB_TYPE:-postgres}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-your-remote-host.example.com}"
REMOTE_PORT="${REMOTE_PORT:-22}"
SSH_KEY="${SSH_KEY:-}"
REMOTE_DEPLOY_DIR="${REMOTE_DEPLOY_DIR:-/www/wwwroot/ocxp}"
IMAGE_NAME="${IMAGE_NAME:-shl148155/opencodexp:ocxp}"
SERVICE_NAME="${SERVICE_NAME:-ocxp}"
OLD_SERVICE_NAMES="${OLD_SERVICE_NAMES:-opencodex-proxy opencodex-proxy-8002}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
POSTGRES_CONTAINER_NAME="${POSTGRES_CONTAINER_NAME:-ocxp-postgres}"
APP_PORT_MAPPING="${APP_PORT_MAPPING:-127.0.0.1:8002:8080}"
NETWORK_NAME="${NETWORK_NAME:-ocxp-network}"
# 构建模式: remote（在服务器上构建，默认）或 local（本地 buildx 推送到镜像仓库）
BUILD_MODE="${BUILD_MODE:-remote}"
# 服务器端构建目录（远程构建时存放同步的源码）
REMOTE_BUILD_DIR="${REMOTE_BUILD_DIR:-/www/wwwroot/ocxp-build}"
# rsync 排除项
RSYNC_EXCLUDES=(
  --exclude '.git'
  --exclude 'node_modules'
  --exclude '.venv'
  --exclude '__pycache__'
  --exclude '.env'
  --exclude 'config.json'
  --exclude 'logs'
  --exclude '*.tar'
  --exclude '*.tar.gz'
  --exclude '*.zip'
  --exclude '.DS_Store'
  --exclude '.idea'
  --exclude '.vscode'
  --exclude 'frontend/node_modules'
  --exclude 'frontend/dist'
  --exclude 'src-tauri/target'
  --exclude 'src-tauri/resources'
  --exclude 'src-tauri/binaries/publish'
  --exclude 'src-tauri/binaries/opencodex-api-*'
  --exclude 'opencodex_proxy/**/bin/'
  --exclude 'opencodex_proxy/**/obj/'
  --exclude '**/TestResults/'
  --exclude '**/*.trx'
  --exclude '.tmp'
  --exclude '.playwright-cli'
)

# 根据数据库类型选择 docker-compose 文件
case "$DB_TYPE" in
  sqlite)
    COMPOSE_FILE="docker-compose-sqlite.yml"
    ;;
  postgres|postgresql|pgsql)
    COMPOSE_FILE="docker-compose-pgsql.yml"
    ;;
  *)
    echo "Error: DB_TYPE must be 'sqlite' or 'postgres' (got: $DB_TYPE)" >&2
    echo "Usage: DB_TYPE=postgres $0" >&2
    exit 1
    ;;
esac

SSH_TARGET="${REMOTE_USER}@${REMOTE_HOST}"

# SSH 选项
SSH_OPTS=(
  -p "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)
if [ -n "$SSH_KEY" ]; then
  SSH_OPTS=(-i "$SSH_KEY" "${SSH_OPTS[@]}")
fi

# SCP 选项（注意端口参数用大写 -P）
SCP_OPTS=(
  -P "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)
if [ -n "$SSH_KEY" ]; then
  SCP_OPTS=(-i "$SSH_KEY" "${SCP_OPTS[@]}")
fi

echo "=== Configuration ==="
echo "Database type: $DB_TYPE"
echo "Compose file: $COMPOSE_FILE"
echo "Image: $IMAGE_NAME"
echo "Platform: $DOCKER_PLATFORM"
echo "Remote: $SSH_TARGET:$REMOTE_DEPLOY_DIR"
echo "Service name: $SERVICE_NAME"
echo "Postgres container: $POSTGRES_CONTAINER_NAME"
echo "Port mapping: $APP_PORT_MAPPING"
echo "Network: $NETWORK_NAME"
echo "Build mode: $BUILD_MODE"
echo "===================="
echo

if [ "$BUILD_MODE" = "local" ]; then
  # 本地构建并推送到镜像仓库（需要 docker buildx 支持）
  echo "Building and pushing $IMAGE_NAME for $DOCKER_PLATFORM locally"
  (
    cd "$ROOT_DIR"
    docker buildx build --progress=plain --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" --push .
  )
  echo
else
  # 在服务器上构建镜像（服务器原生 amd64，无需跨架构，也不依赖本地 buildx）
  echo "Syncing source to $SSH_TARGET:$REMOTE_BUILD_DIR"
  ssh "${SSH_OPTS[@]}" "$SSH_TARGET" "mkdir -p '$REMOTE_BUILD_DIR'"
  rsync -az --delete "${RSYNC_EXCLUDES[@]}" \
    -e "ssh ${SSH_OPTS[*]}" \
    "$ROOT_DIR/" "$SSH_TARGET:$REMOTE_BUILD_DIR/"
  echo

  echo "Building $IMAGE_NAME on remote"
  ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
    "REMOTE_BUILD_DIR='$REMOTE_BUILD_DIR' IMAGE_NAME='$IMAGE_NAME' DOCKER_PLATFORM='$DOCKER_PLATFORM' bash -s" <<'BUILD_SCRIPT'
set -euo pipefail
cd "$REMOTE_BUILD_DIR"
docker build --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" .
BUILD_SCRIPT
  echo
fi

echo "Uploading $COMPOSE_FILE to remote as docker-compose.yml"
scp "${SCP_OPTS[@]}" "$ROOT_DIR/$COMPOSE_FILE" "$SSH_TARGET:$REMOTE_DEPLOY_DIR/docker-compose.yml"
echo

if [ "$BUILD_MODE" = "local" ]; then
  echo "Pulling and deploying on $SSH_TARGET"
else
  echo "Deploying on $SSH_TARGET (image already built on remote)"
fi
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "REMOTE_DEPLOY_DIR='$REMOTE_DEPLOY_DIR' IMAGE_NAME='$IMAGE_NAME' SERVICE_NAME='$SERVICE_NAME' OLD_SERVICE_NAMES='$OLD_SERVICE_NAMES' DB_TYPE='$DB_TYPE' POSTGRES_CONTAINER_NAME='$POSTGRES_CONTAINER_NAME' APP_PORT_MAPPING='$APP_PORT_MAPPING' NETWORK_NAME='$NETWORK_NAME' BUILD_MODE='$BUILD_MODE' bash -s" <<'REMOTE_SCRIPT'
set -euo pipefail

# 本地构建模式需要从仓库拉取镜像；远程构建模式镜像已在服务器上
if [ "$BUILD_MODE" = "local" ]; then
  docker pull "$IMAGE_NAME"
fi
mkdir -p "$REMOTE_DEPLOY_DIR/logs"
cd "$REMOTE_DEPLOY_DIR"

if [ ! -f .env ]; then
  echo "Remote .env not found under $REMOTE_DEPLOY_DIR; create it from .env.example before deploying." >&2
  exit 1
fi

echo "Using database type: $DB_TYPE"

# 停止并移除旧容器
for old_service in $OLD_SERVICE_NAMES; do
  if [ "$old_service" != "$SERVICE_NAME" ] && docker ps -a --format '{{.Names}}' | grep -Fxq "$old_service"; then
    echo "Removing old container: $old_service"
    docker rm -f "$old_service"
  fi
done

# 启动服务
if docker compose version >/dev/null 2>&1; then
  docker compose up -d --no-build --force-recreate --remove-orphans
else
  docker-compose up -d --no-build --force-recreate --remove-orphans
fi

echo
echo "=== Running Containers ==="
docker ps --filter "name=$SERVICE_NAME" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'

# 如果是 PostgreSQL 部署，也显示 PostgreSQL 容器
if [ "$DB_TYPE" = "postgres" ] || [ "$DB_TYPE" = "postgresql" ] || [ "$DB_TYPE" = "pgsql" ]; then
  docker ps --filter "name=$POSTGRES_CONTAINER_NAME" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
fi
REMOTE_SCRIPT

echo
echo "=== Deployment Complete ==="
if [ "$DB_TYPE" = "postgres" ] || [ "$DB_TYPE" = "postgresql" ] || [ "$DB_TYPE" = "pgsql" ]; then
  echo "PostgreSQL container should be running alongside the app."
  echo "Database: opencodex, User: admin"
  echo "Data persisted in: $REMOTE_DEPLOY_DIR/postgres-data"
else
  echo "SQLite database: $REMOTE_DEPLOY_DIR/logs/opencodex.db"
fi
echo "==========================="
