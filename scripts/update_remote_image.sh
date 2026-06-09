#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-ssh.shldev.me}"
REMOTE_PORT="${REMOTE_PORT:-22}"
SSH_KEY="${SSH_KEY:-}"
REMOTE_DEPLOY_DIR="${REMOTE_DEPLOY_DIR:-/www/wwwroot/ocxp}"
IMAGE_NAME="${IMAGE_NAME:-shl148155/opencodexp:ocxp}"
SERVICE_NAME="${SERVICE_NAME:-ocxp}"
OLD_SERVICE_NAMES="${OLD_SERVICE_NAMES:-opencodex-proxy opencodex-proxy-8002}"
HOST_BIND="${HOST_BIND:-127.0.0.1}"
HOST_PORT="${HOST_PORT:-8002}"
CONTAINER_PORT="${CONTAINER_PORT:-8080}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
CONTAINER_TIMEZONE="${CONTAINER_TIMEZONE:-Asia/Shanghai}"

SSH_TARGET="${REMOTE_USER}@${REMOTE_HOST}"
SSH_OPTS=(
  -p "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)
if [ -n "$SSH_KEY" ]; then
  SSH_OPTS=(-i "$SSH_KEY" "${SSH_OPTS[@]}")
fi

echo "Building and pushing $IMAGE_NAME for $DOCKER_PLATFORM from $ROOT_DIR"
(
  cd "$ROOT_DIR"
  docker buildx build --progress=plain --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" --push .
)

echo "Pulling and deploying $IMAGE_NAME on $SSH_TARGET"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "REMOTE_DEPLOY_DIR='$REMOTE_DEPLOY_DIR' IMAGE_NAME='$IMAGE_NAME' SERVICE_NAME='$SERVICE_NAME' OLD_SERVICE_NAMES='$OLD_SERVICE_NAMES' HOST_BIND='$HOST_BIND' HOST_PORT='$HOST_PORT' CONTAINER_PORT='$CONTAINER_PORT' DOCKER_PLATFORM='$DOCKER_PLATFORM' CONTAINER_TIMEZONE='$CONTAINER_TIMEZONE' bash -s" <<'REMOTE_SCRIPT'
set -euo pipefail

docker pull "$IMAGE_NAME"
mkdir -p "$REMOTE_DEPLOY_DIR/logs"
cd "$REMOTE_DEPLOY_DIR"

if [ ! -f .env ]; then
  echo "Remote .env not found under $REMOTE_DEPLOY_DIR; create it from .env.example before deploying." >&2
  exit 1
fi

if grep -q '^TZ=' .env; then
  sed -i "s|^TZ=.*|TZ=$CONTAINER_TIMEZONE|" .env
else
  printf '\nTZ=%s\n' "$CONTAINER_TIMEZONE" >> .env
fi

if [ -f docker-compose.yml ]; then
  cp docker-compose.yml "docker-compose.yml.bak-$(date +%Y%m%d%H%M%S)"
fi

cat > docker-compose.yml <<COMPOSE
version: "3.8"
services:
  $SERVICE_NAME:
    image: $IMAGE_NAME
    platform: $DOCKER_PLATFORM
    container_name: $SERVICE_NAME
    env_file:
      - ./.env
    ports:
      - "$HOST_BIND:$HOST_PORT:$CONTAINER_PORT"
    volumes:
      - ./logs:/app/logs
    healthcheck:
      test: ["CMD-SHELL", "true"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 20s
    restart: unless-stopped
COMPOSE

for old_service in $OLD_SERVICE_NAMES; do
  if [ "$old_service" != "$SERVICE_NAME" ] && docker ps -a --format '{{.Names}}' | grep -Fxq "$old_service"; then
    docker rm -f "$old_service"
  fi
done

if docker compose version >/dev/null 2>&1; then
  docker compose up -d --no-build --force-recreate --remove-orphans "$SERVICE_NAME"
else
  docker-compose up -d --no-build --force-recreate --remove-orphans "$SERVICE_NAME"
fi

docker ps --filter "name=$SERVICE_NAME" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
REMOTE_SCRIPT

echo "Remote image pull deploy finished."
