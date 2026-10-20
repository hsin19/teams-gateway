#!/bin/bash
# launchd 啟動 gateway 的入口。launchd 不載入 shell 設定，dotnet 的路徑要自己補。
set -euo pipefail

export PATH="$HOME/.dotnet:$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"
export DOTNET_ROOT="$HOME/.dotnet"

cd "$(dirname "$0")/../.."

# .env：Gateway__AdminToken 等（service.sh install 時產生）
if [ -f .env ]; then
  set -a
  . ./.env
  set +a
fi
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5199}"
export Gateway__DataDir="$(pwd)/.data"

exec dotnet .publish/TeamsGateway.Gateway.dll
