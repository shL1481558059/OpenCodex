#!/usr/bin/env bash
set -Eeuo pipefail

fail() {
  echo "Error: $*" >&2
  exit 1
}

require_value() {
  local name="$1"
  if [ -z "${!name:-}" ]; then
    fail "$name is required"
  fi
}

cleanup() {
  if [ -n "${DOCKER_CONFIG_DIR:-}" ]; then
    rm -rf "$DOCKER_CONFIG_DIR"
  fi
}

for name in \
  IMAGE_NAME \
  IMAGE_TAG \
  SERVICE_NAME \
  POSTGRES_CONTAINER_NAME \
  REDIS_CONTAINER_NAME \
  APP_PORT_MAPPING \
  NETWORK_NAME; do
  require_value "$name"
done

DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$DEPLOY_DIR/docker-compose.yml"
COMPOSE_SERVICE="${COMPOSE_SERVICE:-ocxp}"
REGISTRY_HOST="${REGISTRY_HOST:-ghcr.io}"
REGISTRY_AUTH_ENABLED="${REGISTRY_AUTH_ENABLED:-0}"
REGISTRY_USERNAME="${REGISTRY_USERNAME:-}"
HEALTH_TIMEOUT_SECONDS="${HEALTH_TIMEOUT_SECONDS:-180}"
HEALTH_POLL_INTERVAL_SECONDS="${HEALTH_POLL_INTERVAL_SECONDS:-5}"
DOCKER_LOG_MAX_SIZE="${DOCKER_LOG_MAX_SIZE:-50m}"
DOCKER_LOG_MAX_FILE="${DOCKER_LOG_MAX_FILE:-5}"
IMAGE_REF="${IMAGE_NAME}:${IMAGE_TAG}"

case "$REGISTRY_AUTH_ENABLED" in
  0|1) ;;
  *) fail "REGISTRY_AUTH_ENABLED must be 0 or 1" ;;
esac

case "$HEALTH_TIMEOUT_SECONDS" in
  *[!0-9]*|"") fail "HEALTH_TIMEOUT_SECONDS must be numeric" ;;
esac
[ "$HEALTH_TIMEOUT_SECONDS" -gt 0 ] || fail "HEALTH_TIMEOUT_SECONDS must be greater than zero"

case "$HEALTH_POLL_INTERVAL_SECONDS" in
  *[!0-9]*|"") fail "HEALTH_POLL_INTERVAL_SECONDS must be numeric" ;;
esac
[ "$HEALTH_POLL_INTERVAL_SECONDS" -gt 0 ] || fail "HEALTH_POLL_INTERVAL_SECONDS must be greater than zero"

[ -f "$COMPOSE_FILE" ] || fail "Compose file not found: $COMPOSE_FILE"
[ -f "$DEPLOY_DIR/.env" ] || fail "Remote .env not found: $DEPLOY_DIR/.env"

command -v docker >/dev/null 2>&1 || fail "docker is not installed"

if docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  fail "docker compose is not available"
fi

for dependency in "$POSTGRES_CONTAINER_NAME" "$REDIS_CONTAINER_NAME"; do
  dependency_state="$(docker inspect --format '{{.State.Status}}' "$dependency" 2>/dev/null || true)"
  [ "$dependency_state" = "running" ] || fail "Dependency container is not running: $dependency"
done

trap cleanup EXIT

if [ "$REGISTRY_AUTH_ENABLED" = "1" ]; then
  [ -n "$REGISTRY_USERNAME" ] || fail "REGISTRY_USERNAME is required when registry authentication is enabled"
  IFS= read -r REGISTRY_TOKEN
  [ -n "$REGISTRY_TOKEN" ] || fail "Registry token was not provided"

  DOCKER_CONFIG_DIR="$(mktemp -d)"
  chmod 700 "$DOCKER_CONFIG_DIR"
  export DOCKER_CONFIG="$DOCKER_CONFIG_DIR"
  printf '%s' "$REGISTRY_TOKEN" | docker login "$REGISTRY_HOST" --username "$REGISTRY_USERNAME" --password-stdin
  unset REGISTRY_TOKEN
fi

echo "Pulling image: $IMAGE_REF"
docker pull "$IMAGE_REF"

previous_image="$(docker inspect --format '{{.Config.Image}}' "$SERVICE_NAME" 2>/dev/null || true)"
if [ -n "$previous_image" ]; then
  printf '%s\n' "$previous_image" > "$DEPLOY_DIR/.previous-image"
  echo "Previous image: $previous_image"
else
  echo "Previous image: none"
fi

export IMAGE_NAME="$IMAGE_REF"
export SERVICE_NAME
export POSTGRES_CONTAINER_NAME
export REDIS_CONTAINER_NAME
export APP_PORT_MAPPING
export NETWORK_NAME
export DOCKER_LOG_MAX_SIZE
export DOCKER_LOG_MAX_FILE

cd "$DEPLOY_DIR"

echo "Recreating application container: $SERVICE_NAME"
"${COMPOSE[@]}" up \
  -d \
  --no-build \
  --no-deps \
  --force-recreate \
  "$COMPOSE_SERVICE"

wait_for_health() {
  local deadline=$((SECONDS + HEALTH_TIMEOUT_SECONDS))
  local status

  while [ "$SECONDS" -lt "$deadline" ]; do
    if docker inspect "$SERVICE_NAME" >/dev/null 2>&1; then
      status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$SERVICE_NAME")"
    else
      status="missing"
    fi

    case "$status" in
      healthy)
        return 0
        ;;
      unhealthy|exited|dead|missing)
        echo "Container health check failed with status: $status" >&2
        docker logs --tail 120 "$SERVICE_NAME" >&2 || true
        return 1
        ;;
      starting|created|running|restarting)
        echo "Waiting for $SERVICE_NAME health: $status"
        ;;
      *)
        echo "Unexpected container status: $status" >&2
        docker logs --tail 120 "$SERVICE_NAME" >&2 || true
        return 1
        ;;
    esac

    sleep "$HEALTH_POLL_INTERVAL_SECONDS"
  done

  echo "Container health check timed out after ${HEALTH_TIMEOUT_SECONDS}s" >&2
  docker logs --tail 120 "$SERVICE_NAME" >&2 || true
  return 1
}

wait_for_health

echo "Deployment complete: $IMAGE_REF"
echo "Application container: $SERVICE_NAME"
