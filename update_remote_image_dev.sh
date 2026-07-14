#!/usr/bin/env bash
set -euo pipefail

# dev 版镜像更新脚本：部署到 ocxp-dev 容器（PostgreSQL + Redis），不影响原有 ocxp
DB_TYPE="${DB_TYPE:-postgres}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-your-remote-host.example.com}"
REMOTE_PORT="${REMOTE_PORT:-22}"
SSH_KEY="${SSH_KEY:-}"
REMOTE_DEPLOY_DIR="${REMOTE_DEPLOY_DIR:-/www/wwwroot/ocxp-dev}"
IMAGE_NAME="${IMAGE_NAME:-shl148155/opencodexp:ocxp}"
SERVICE_NAME="${SERVICE_NAME:-ocxp-dev}"
OLD_SERVICE_NAMES="${OLD_SERVICE_NAMES:-ocxp-dev-old}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
POSTGRES_CONTAINER_NAME="${POSTGRES_CONTAINER_NAME:-ocxp-postgres-dev}"
REDIS_CONTAINER_NAME="${REDIS_CONTAINER_NAME:-ocxp-redis-dev}"
APP_PORT_MAPPING="${APP_PORT_MAPPING:-127.0.0.1:8003:8080}"
NETWORK_NAME="${NETWORK_NAME:-ocxp-dev-network}"

case "$DB_TYPE" in
  postgres|postgresql|pgsql)
    COMPOSE_FILE="docker-compose-pgsql.yml"
    ;;
  *)
    echo "Error: dev 版仅支持 postgres (got: $DB_TYPE)" >&2
    exit 1
    ;;
esac

SSH_TARGET="${REMOTE_USER}@${REMOTE_HOST}"

SSH_OPTS=(
  -p "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)
if [ -n "$SSH_KEY" ]; then
  SSH_OPTS=(-i "$SSH_KEY" "${SSH_OPTS[@]}")
fi

SCP_OPTS=(
  -P "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)
if [ -n "$SSH_KEY" ]; then
  SCP_OPTS=(-i "$SSH_KEY" "${SCP_OPTS[@]}")
fi

echo "=== Configuration (dev) ==="
echo "Database type: $DB_TYPE"
echo "Compose file: $COMPOSE_FILE"
echo "Image: $IMAGE_NAME"
echo "Platform: $DOCKER_PLATFORM"
echo "Remote: $SSH_TARGET:$REMOTE_DEPLOY_DIR"
echo "Service name: $SERVICE_NAME"
echo "Postgres container: $POSTGRES_CONTAINER_NAME"
echo "Redis container: $REDIS_CONTAINER_NAME"
echo "Port mapping: $APP_PORT_MAPPING"
echo "Network: $NETWORK_NAME"
echo "============================"
echo

echo "Building and pushing $IMAGE_NAME for $DOCKER_PLATFORM"
(
  cd "$ROOT_DIR"
  docker buildx build --progress=plain --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" .
  docker push "$IMAGE_NAME"
)
echo

echo "Uploading $COMPOSE_FILE to remote as docker-compose.yml"
scp "${SCP_OPTS[@]}" "$ROOT_DIR/$COMPOSE_FILE" "$SSH_TARGET:$REMOTE_DEPLOY_DIR/docker-compose.yml"
echo

echo "Pulling and deploying on $SSH_TARGET"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "REMOTE_DEPLOY_DIR='$REMOTE_DEPLOY_DIR' IMAGE_NAME='$IMAGE_NAME' SERVICE_NAME='$SERVICE_NAME' OLD_SERVICE_NAMES='$OLD_SERVICE_NAMES' POSTGRES_CONTAINER_NAME='$POSTGRES_CONTAINER_NAME' REDIS_CONTAINER_NAME='$REDIS_CONTAINER_NAME' APP_PORT_MAPPING='$APP_PORT_MAPPING' NETWORK_NAME='$NETWORK_NAME' bash -s" <<'REMOTE_SCRIPT'
set -euo pipefail

docker pull "$IMAGE_NAME"
mkdir -p "$REMOTE_DEPLOY_DIR/logs" "$REMOTE_DEPLOY_DIR/redis-data"
cd "$REMOTE_DEPLOY_DIR"

if [ ! -f .env ]; then
  echo "Remote .env not found under $REMOTE_DEPLOY_DIR; create it from .env.example before deploying." >&2
  exit 1
fi

# 用容器名后缀 -dev 覆盖 compose 变量，确保不影响原有 ocxp
export SERVICE_NAME="$SERVICE_NAME"
export POSTGRES_CONTAINER_NAME="$POSTGRES_CONTAINER_NAME"
export REDIS_CONTAINER_NAME="$REDIS_CONTAINER_NAME"
export APP_PORT_MAPPING="$APP_PORT_MAPPING"
export NETWORK_NAME="$NETWORK_NAME"

# 启动服务（优先 docker-compose v1，兼容服务器环境）
if command -v docker-compose >/dev/null 2>&1; then
  docker-compose up -d --no-build --force-recreate
else
  docker compose up -d --no-build --force-recreate
fi

echo
echo "=== Running Containers (dev) ==="
docker ps --filter "name=ocxp-dev" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
REMOTE_SCRIPT

echo
echo "=== Deployment Complete (dev) ==="
echo "App:       http://127.0.0.1:8003"
echo "Postgres:  $POSTGRES_CONTAINER_NAME (internal 5432)"
echo "Redis:     $REDIS_CONTAINER_NAME (internal 6379)"
echo "Data:      $REMOTE_DEPLOY_DIR/{postgres-data,redis-data,logs}"
echo "================================="
