#!/usr/bin/env bash
set -euo pipefail

# 切换 ocxp.shldev.me 的 nginx upstream 后端
# 用法:
#   ./switch_backend.sh 8002        # 仅用原有 ocxp
#   ./switch_backend.sh 8003        # 仅用 dev
#   ./switch_backend.sh both        # 负载均衡(默认轮询)

REMOTE_USER="${REMOTE_USER:-admin}"
REMOTE_HOST="${REMOTE_HOST:-ssh.shldev.me}"
REMOTE_PORT="${REMOTE_PORT:-22}"
SSH_KEY="${SSH_KEY:-}"

SSH_OPTS=(-p "$REMOTE_PORT" -o StrictHostKeyChecking=accept-new)
if [ -n "$SSH_KEY" ]; then
  SSH_OPTS=(-i "$SSH_KEY" "${SSH_OPTS[@]}")
fi
SSH_TARGET="${REMOTE_USER}@${REMOTE_HOST}"

MODE="${1:-both}"

case "$MODE" in
  8002)
    SERVERS="server 127.0.0.1:8002;"
    LABEL="仅 8002 (原有 ocxp)"
    ;;
  8003)
    SERVERS="server 127.0.0.1:8003;"
    LABEL="仅 8003 (dev)"
    ;;
  both)
    SERVERS="server 127.0.0.1:8002;
    server 127.0.0.1:8003;"
    LABEL="负载均衡 8002 + 8003"
    ;;
  *)
    echo "用法: $0 [8002|8003|both]"
    echo "  8002  仅用原有 ocxp"
    echo "  8003  仅用 dev"
    echo "  both  负载均衡(默认)"
    exit 1
    ;;
esac

echo "切换到: $LABEL"

ssh "${SSH_OPTS[@]}" "$SSH_TARGET" "sudo tee /etc/nginx/conf.d/ocxp.shldev.me.conf > /dev/null" <<EOF
upstream ocxp_backend {
    $SERVERS
}

server {
    listen 80;
    listen [::]:80;
    server_name ocxp.shldev.me;

    client_max_body_size 100m;
    proxy_connect_timeout 480s;
    proxy_send_timeout 480s;
    proxy_read_timeout 480s;
    send_timeout 480s;

    location / {
        proxy_pass http://ocxp_backend;
        proxy_set_header Host \$http_host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection \$connection_upgrade;
        proxy_buffering off;
        proxy_request_buffering off;
    }
}
EOF

ssh "${SSH_OPTS[@]}" "$SSH_TARGET" "sudo nginx -t 2>&1 && sudo nginx -s reload 2>&1 && echo 'done'"

echo
echo "当前后端:"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" "grep -A5 'upstream ocxp_backend' /etc/nginx/conf.d/ocxp.shldev.me.conf | grep server"
echo
echo "验证:"
ssh "${SSH_OPTS[@]}" "$SSH_TARGET" "curl -s http://127.0.0.1:80 -H 'Host: ocxp.shldev.me'"
echo
