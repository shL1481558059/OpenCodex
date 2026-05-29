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
    --exclude='node_modules' \
    --exclude='frontend/node_modules' \
    --exclude='opencodex_proxy/static/admin' \
    --exclude='AGENTS.md' \
    --exclude='config.json' \
    --exclude='logs' \
    --exclude='*.tar' \
    --exclude='*.tar.gz' \
    --exclude='*.zip' \
    -czf "$ARCHIVE" \
    Dockerfile package.json requirements.txt .env.example frontend opencodex_proxy tests
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
updated_compose_file=""
for compose_file in docker-compose.yml docker-compose.yaml compose.yml compose.yaml; do
  if [ ! -f "$compose_file" ]; then
    continue
  fi
  tmp_file="$compose_file.tmp.$$"
  if awk -v service="$SERVICE_NAME" -v image="$IMAGE_NAME" '
    function leading_spaces(line) {
      match(line, /^[[:space:]]*/)
      return RLENGTH
    }
    BEGIN {
      in_services = 0
      in_service = 0
      services_indent = -1
      service_indent = -1
      service_seen = 0
      image_replaced = 0
    }
    {
      line = $0
      indent = leading_spaces(line)
      if (line ~ /^[[:space:]]*services:[[:space:]]*$/) {
        in_services = 1
        services_indent = indent
        print line
        next
      }
      if (in_services && indent <= services_indent && line !~ /^[[:space:]]*($|#)/) {
        in_services = 0
        in_service = 0
      }
      if (in_services) {
        service_pattern = "^[[:space:]]*" service ":[[:space:]]*$"
        if (line ~ service_pattern) {
          in_service = 1
          service_indent = indent
          service_seen = 1
          print line
          next
        }
        if (in_service && indent <= service_indent && line !~ /^[[:space:]]*($|#)/) {
          in_service = 0
        }
        if (in_service && line ~ /^[[:space:]]*image:[[:space:]]*/) {
          print substr(line, 1, indent) "image: " image
          image_replaced = 1
          next
        }
      }
      print line
    }
    END {
      if (!service_seen) {
        exit 2
      }
      if (!image_replaced) {
        exit 3
      }
    }
  ' "$compose_file" > "$tmp_file"; then
    if ! cmp -s "$compose_file" "$tmp_file"; then
      cp "$compose_file" "$compose_file.bak-$(date +%Y%m%d%H%M%S)"
      mv "$tmp_file" "$compose_file"
      echo "Updated $SERVICE_NAME image in $compose_file to $IMAGE_NAME"
    else
      rm -f "$tmp_file"
      echo "$SERVICE_NAME image in $compose_file is already $IMAGE_NAME"
    fi
    updated_compose_file="$compose_file"
    break
  else
    status=$?
    rm -f "$tmp_file"
    if [ "$status" -eq 2 ]; then
      continue
    fi
    echo "Failed to update image for service $SERVICE_NAME in $compose_file" >&2
    exit "$status"
  fi
done
if [ -z "$updated_compose_file" ]; then
  echo "No compose file contains service $SERVICE_NAME under $REMOTE_DEPLOY_DIR" >&2
  exit 1
fi

if docker compose version >/dev/null 2>&1; then
  docker compose up -d --force-recreate "$SERVICE_NAME"
else
  docker-compose up -d --force-recreate "$SERVICE_NAME"
fi

docker ps --filter "name=$SERVICE_NAME" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
REMOTE_SCRIPT

echo "Remote image update finished."
