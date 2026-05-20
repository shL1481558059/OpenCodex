#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-ssh.shldev.me}"
REMOTE_PORT="${REMOTE_PORT:-22}"
SSH_KEY="${SSH_KEY:-/Users/w/.ssh/LightsailDefaultKey-ap-northeast-2.pem}"
REMOTE_DEPLOY_DIR="${REMOTE_DEPLOY_DIR:-/www/wwwroot/opencodex-proxy}"
REMOTE_SRC_DIR="${REMOTE_SRC_DIR:-/tmp/opencodex-proxy-src}"
REMOTE_ARCHIVE="${REMOTE_ARCHIVE:-/tmp/opencodex-proxy-src.tar.gz}"
IMAGE_NAME="${IMAGE_NAME:-opencodex-proxy:test}"
SERVICE_NAME="${SERVICE_NAME:-opencodex-proxy}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"

ARCHIVE="$(mktemp -t opencodex-proxy-src.XXXXXX.tar.gz)"
trap 'rm -f "$ARCHIVE"' EXIT

SSH_TARGET="${REMOTE_USER}@${REMOTE_HOST}"
SSH_OPTS=(
  -i "$SSH_KEY"
  -p "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)
echo "Packing source from $ROOT_DIR"
(
  cd "$ROOT_DIR"
  COPYFILE_DISABLE=1 LC_ALL=C LANG=C tar \
    --exclude='.git' \
    --exclude='.venv' \
    --exclude='.env' \
    --exclude='.DS_Store' \
    --exclude='AGENTS.md' \
    --exclude='config.json' \
    --exclude='logs' \
    --exclude='*.tar' \
    --exclude='*.tar.gz' \
    --exclude='*.zip' \
    -czf "$ARCHIVE" \
    Dockerfile requirements.txt .env.example opencodex_proxy tests
)

echo "Uploading archive to $SSH_TARGET:$REMOTE_ARCHIVE"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "mkdir -p \"$(dirname "$REMOTE_ARCHIVE")\" && cat > \"$REMOTE_ARCHIVE\"" \
  < "$ARCHIVE"

echo "Building $IMAGE_NAME on $SSH_TARGET"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "REMOTE_ARCHIVE='$REMOTE_ARCHIVE' REMOTE_SRC_DIR='$REMOTE_SRC_DIR' REMOTE_DEPLOY_DIR='$REMOTE_DEPLOY_DIR' IMAGE_NAME='$IMAGE_NAME' SERVICE_NAME='$SERVICE_NAME' DOCKER_PLATFORM='$DOCKER_PLATFORM' bash -s" <<'REMOTE_SCRIPT'
set -euo pipefail

rm -rf "$REMOTE_SRC_DIR"
mkdir -p "$REMOTE_SRC_DIR"
tar -xzf "$REMOTE_ARCHIVE" -C "$REMOTE_SRC_DIR"

cd "$REMOTE_SRC_DIR"
docker build --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" .

cd "$REMOTE_DEPLOY_DIR"
for compose_file in docker-compose.yml docker-compose.yaml compose.yml compose.yaml; do
  if [ -f "$compose_file" ] && grep -q 'opencodex-proxy:windhub-amd64' "$compose_file"; then
    cp "$compose_file" "$compose_file.bak-$(date +%Y%m%d%H%M%S)"
    sed -i "s|opencodex-proxy:windhub-amd64|$IMAGE_NAME|g" "$compose_file"
  fi
done

if docker compose version >/dev/null 2>&1; then
  docker compose up -d --force-recreate "$SERVICE_NAME"
else
  docker-compose up -d --force-recreate "$SERVICE_NAME"
fi

docker ps --filter "name=$SERVICE_NAME" --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
REMOTE_SCRIPT

echo "Remote image update finished."
