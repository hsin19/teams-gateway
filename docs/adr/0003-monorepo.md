# ADR 0003：Monorepo 結構

## 決定

一個 repo 放整個「個人版 Teams 整合」平台的所有產物：gateway 服務、未來的 slack-relay 服務、以及 client（CLI / Python）。

```
services/     可部署的服務
  TeamsGateway.Gateway      Teams → HTTP/SSE gateway（已實作）
  slack-relay/              訂閱 gateway、把訊息轉進 Slack（規劃中）
shared/       跨專案共用
  TeamsGateway.Contracts    對外 wire DTO（gateway 與 .NET relay 共用）
  TeamsGateway.ServiceDefaults  Aspire 共用（OTel / health / resilience）
apphost/      Aspire 編排（只在 dev）
  TeamsGateway.AppHost
clients/      給別人接的 client
  python/                   Python client（規劃中）
  cli/                      CLI（規劃中）
spike/        逆向用的拋棄式 Python 腳本
docs/
```

## 為什麼

- **一個帳號、一組非官方 API，散在多個 repo 難維護**：gateway、relay、client 對同一組契約，放一起改契約一次到位。
- **契約共用**：`TeamsGateway.Contracts` 讓 gateway 和未來的 .NET slack-relay 共用同一份 DTO；Python client 另外對照同一份契約。
- **各自可獨立 build / 部署**：`services/*` 各自出 container，`clients/*` 各自發佈；`apphost` 只在本機編排，不部署。

## slack-relay 的定位（規劃）

一個獨立服務，用 API key 連 gateway 的 `GET /v1/events`（SSE），把 Teams 訊息轉貼進對應的 Slack 頻道，必要時把 Slack 的回覆經 gateway 的 `POST /v1/chats/{id}/messages` 轉回 Teams。目標：日常只看 Slack 就能處理 Teams 的事。

它是 gateway 的**訂閱者**，不碰 Teams 非官方 API，所以跟 gateway 完全解耦——gateway 換底層實作，relay 不受影響。
