#!/bin/bash
# 把 gateway 裝成 macOS LaunchAgent：登入後背景跑在 localhost，掛了自動重啟，不用開著終端機、不用 Docker。
# Mac 睡眠時會跟著暫停。資料（cookie / token / SQLite）在 repo 的 .data/。
#
# 用法：ops/service.sh <指令>
#   install           publish + 啟動（首次會產生 .env 的 admin token；重跑 = 重新 publish 並載入）
#   bootstrap <file>  匯入登入 cookie（spike/http_refresh.py export 的 JSON）並建一把 API key，印出來
#   key <name>        再建一把 API key
#   restart | status | logs | uninstall
set -euo pipefail

LABEL="local.teams-gateway"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
PLIST="$HOME/Library/LaunchAgents/${LABEL}.plist"
LOG="${REPO}/.state/gateway.log"
DOMAIN="gui/$(id -u)"
URL="http://127.0.0.1:5199"

ensure_env() {
  if [ ! -f "${REPO}/.env" ] || ! grep -q Gateway__AdminToken "${REPO}/.env"; then
    local token
    token="$(openssl rand -hex 24)"
    printf 'Gateway__AdminToken=%s\nASPNETCORE_URLS=%s\n' "$token" "${URL}" >> "${REPO}/.env"
    echo "已產生 admin token 寫入 .env"
  fi
}

admin_token() { grep Gateway__AdminToken "${REPO}/.env" | cut -d= -f2; }

publish() {
  echo "publish 中…"
  ( cd "${REPO}" && dotnet publish services/TeamsGateway.Gateway -c Release -o .publish --nologo -v q )
}

write_plist() {
  mkdir -p "$(dirname "$PLIST")" "$(dirname "${LOG}")"
  cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>${LABEL}</string>
  <key>ProgramArguments</key>
  <array><string>/bin/bash</string><string>${REPO}/ops/launchd/run.sh</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>10</integer>
  <key>StandardOutPath</key><string>${LOG}</string>
  <key>StandardErrorPath</key><string>${LOG}</string>
</dict>
</plist>
EOF
}

wait_ready() {
  for _ in $(seq 1 60); do curl -sf "${URL}/" >/dev/null 2>&1 && return 0; sleep 0.5; done
  return 1
}

case "${1:-}" in
  install)
    ensure_env
    publish
    write_plist
    launchctl bootout "${DOMAIN}/${LABEL}" 2>/dev/null && sleep 2 || true
    launchctl bootstrap "$DOMAIN" "$PLIST"
    wait_ready && echo "已啟動 ${LABEL}，${URL}，log：${LOG}" || echo "啟動逾時，看 log：${LOG}"
    ;;
  bootstrap)
    [ -f "${2:-}" ] || { echo "用法：$0 bootstrap <login_cookies.json>" >&2; exit 1; }
    curl -s -X PUT -H "X-Admin-Token: $(admin_token)" --data-binary @"$2" "${URL}/admin/login-cookies"; echo
    curl -s -X POST -H "X-Admin-Token: $(admin_token)" -H "Content-Type: application/json" -d '{"name":"'"${3:-default}"'"}' "${URL}/admin/api-keys"; echo
    ;;
  key)
    curl -s -X POST -H "X-Admin-Token: $(admin_token)" -H "Content-Type: application/json" -d '{"name":"'"${2:-default}"'"}' "${URL}/admin/api-keys"; echo
    ;;
  restart)
    publish
    launchctl kickstart -k "${DOMAIN}/${LABEL}"
    echo "已重啟 ${LABEL}"
    ;;
  status)
    launchctl print "${DOMAIN}/${LABEL}" 2>/dev/null | grep -E $'^\t(state|pid|runs|last exit code) =' || echo "${LABEL} 沒有在跑"
    ;;
  logs)
    tail -n 50 -F "${LOG}"
    ;;
  uninstall)
    launchctl bootout "${DOMAIN}/${LABEL}" 2>/dev/null || true
    rm -f "$PLIST"
    echo "已移除 ${LABEL}（.data 與 .env 保留）"
    ;;
  *)
    echo "用法：$0 install | bootstrap <file> [keyname] | key <name> | restart | status | logs | uninstall" >&2
    exit 1
    ;;
esac
