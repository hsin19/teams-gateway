# ADR 0004：先跑在本機（不容器化、不對外）

> 已被 ADR 0007 取代：本機只當開發環境、用 Aspire 跑，不再用 launchd 常駐（`ops/` 已移除）。

## 決定

gateway 先以 macOS LaunchAgent 跑在本機 `localhost`，**不**裝 Docker、**不**開對外網域 / tunnel。客戶端與 skills 同一台機器，走 `127.0.0.1` 連。

## 背景

原本考慮容器化 + Cloudflare Tunnel 對外。但盤點後：

- 目前要接 gateway 的（客戶端、skills）都在同一台 Mac，沒有對外連線需求。
- 只為了掛著而在本機開 Docker，反而更吃資源（跟一開始「別吃幾百 MB」的初衷相反）。

所以先用最輕的方式：發佈後用 launchd 跑 DLL（約 150–170MB RSS）。

## 之後要對外時

Docker 化 + Cloudflare Tunnel（或搬雲）再做。屆時：gateway 已經是純 HTTP 服務、資料都在 `.data/`（掛 volume 即可）、認證有 API key / admin token，容器化不需要改 code。那條路留著，不急。

## 操作

`ops/service.sh install | bootstrap <cookies.json> | key <name> | restart | status | logs | uninstall`。
資料（cookie / token / SQLite）在 repo `.data/`；admin token 在 `.env`（都 gitignored）。
