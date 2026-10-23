#!/bin/bash
# 改了 EF model 之後產生 / 拿掉 migration：SQLite、Postgres 各一套，一次處理兩個（docs/adr/0009）。
# 產生出來的檔案要一起 commit；gateway 啟動時會自動套用還沒跑過的。
#
# 用法：tools/db-migration.sh <指令>
#   add <名稱>   兩個 provider 各加一個 migration（名稱用 PascalCase，例如 AddWebhookLastError）
#   remove       兩個 provider 各拿掉最後一個（只在還沒 commit、還沒套用到任何 DB 之前用）
set -euo pipefail

cd "$(dirname "$0")/.."
dotnet tool restore >/dev/null

PROJECT=services/TeamsGateway.Gateway

case "${1:-}" in
  add)
    [ -n "${2:-}" ] || { echo "用法：$0 add <名稱>" >&2; exit 1; }
    for provider in Sqlite Postgres; do
      # namespace 由 output-dir 推（TeamsGateway.Gateway.Data.Migrations.<provider>）；另外給 --namespace 的話 snapshot 會跑到別的資料夾
      dotnet ef migrations add "$2" --project "$PROJECT" --context "${provider}GatewayDbContext" --output-dir "Data/Migrations/${provider}"
    done
    ;;
  remove)
    for provider in Sqlite Postgres; do
      dotnet ef migrations remove --project "$PROJECT" --context "${provider}GatewayDbContext"
    done
    ;;
  *)
    echo "用法：$0 add <名稱> | remove" >&2
    exit 1
    ;;
esac
