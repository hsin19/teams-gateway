# slack-relay（規劃中）

訂閱 teams-gateway 的 `GET /v1/events`（SSE），把個人版 Teams 的訊息轉貼進對應的 Slack 頻道；
Slack 那邊的回覆再經 gateway 的 `POST /v1/chats/{id}/messages` 轉回 Teams。

目標：日常只用 Slack 收發，不用再開 Teams。

- 它是 gateway 的**訂閱者**，只用公開的 `/v1` API + API key，不碰 Teams 非官方 API。
- 對照 `docs/adr/0003-monorepo.md`。
- 預計一樣是 .NET 服務，共用 `shared/TeamsGateway.Contracts` 與 `shared/TeamsGateway.ServiceDefaults`。
