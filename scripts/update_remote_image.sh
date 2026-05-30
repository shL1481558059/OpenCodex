#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-ssh.shldev.me}"
REMOTE_PORT="${REMOTE_PORT:-22}"
SSH_KEY="${SSH_KEY:-/Users/w/.ssh/LightsailDefaultKey-ap-northeast-2.pem}"
REMOTE_DEPLOY_DIR="${REMOTE_DEPLOY_DIR:-/www/wwwroot/opencodex-proxy}"
IMAGE_NAME="${IMAGE_NAME:-shl148155/opencodexp:latest}"
SERVICE_NAME="${SERVICE_NAME:-opencodex-proxy}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"

SSH_TARGET="${REMOTE_USER}@${REMOTE_HOST}"
SSH_OPTS=(
  -i "$SSH_KEY"
  -p "$REMOTE_PORT"
  -o StrictHostKeyChecking=accept-new
)

echo "Building and pushing $IMAGE_NAME for $DOCKER_PLATFORM from $ROOT_DIR"
(
  cd "$ROOT_DIR"
  docker buildx build --progress=plain --platform "$DOCKER_PLATFORM" -t "$IMAGE_NAME" --push .
)

echo "Pulling and deploying $IMAGE_NAME on $SSH_TARGET"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" \
  "REMOTE_DEPLOY_DIR='$REMOTE_DEPLOY_DIR' IMAGE_NAME='$IMAGE_NAME' SERVICE_NAME='$SERVICE_NAME' bash -s" <<'REMOTE_SCRIPT'
set -euo pipefail

docker pull "$IMAGE_NAME"
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
  docker compose up -d --no-build --force-recreate "$SERVICE_NAME"
else
  docker-compose up -d --no-build --force-recreate "$SERVICE_NAME"
fi

docker ps --filter "name=$SERVICE_NAME" --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}'
REMOTE_SCRIPT

echo "Remote image pull deploy finished."
