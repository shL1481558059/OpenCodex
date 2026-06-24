#!/usr/bin/env bash
set -euo pipefail

# 数据库类型: sqlite 或 postgres
DB_TYPE="${DB_TYPE:-sqlite}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-ssh.shldev.me}"
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
echo "===================="
echo

echo "Building and pushing $IMAGE_NAME for $DOCKER_PLATFORM"
(
  cd "$ROOT_DIR"
  docker buildx build --progress=plain --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" --push .
)
echo

echo "Uploading $COMPOSE_FILE to remote as docker-compose.yml"
scp "${SCP_OPTS[@]}" "$ROOT_DIR/$COMPOSE_FILE" "$SSH_TARGET:$REMOTE_DEPLOY_DIR/docker-compose.yml"
echo

echo "Pulling and deploying on $SSH_TARGET"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "REMOTE_DEPLOY_DIR='$REMOTE_DEPLOY_DIR' IMAGE_NAME='$IMAGE_NAME' SERVICE_NAME='$SERVICE_NAME' OLD_SERVICE_NAMES='$OLD_SERVICE_NAMES' DB_TYPE='$DB_TYPE' POSTGRES_CONTAINER_NAME='$POSTGRES_CONTAINER_NAME' APP_PORT_MAPPING='$APP_PORT_MAPPING' NETWORK_NAME='$NETWORK_NAME' bash -s" <<'REMOTE_SCRIPT'
set -euo pipefail

docker pull "$IMAGE_NAME"
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
