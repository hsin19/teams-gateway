# teams-gateway

把個人版 Microsoft Teams（teams.live.com）包成給 CLI、app、其他服務用的介面。個人版沒有 bot API / webhook / 官方 API，
這個 monorepo 用一個專用帳號登入，對外提供 REST + 即時事件串流，並在其上長出轉送、client 等元件。
目標是掛在一台機器（或雲端）上，其他人要做 Teams 機器人只要打 HTTP。

## 元件

| 路徑 | 是什麼 | 狀態 |
|---|---|---|
| `services/TeamsGateway.Gateway` | Teams → HTTP/SSE gateway（列群組/讀/發/事件串流） | ✅ 可用 |
| `services/slack-relay` | 訂閱 gateway、把訊息轉進 Slack（只看 Slack 就能接收與回覆） | 規劃中 |
| `clients/python` | Python client（程式 / 常駐訂閱用，含 SSE） | ✅ |
| `clients/mcp` | agent 接法：gateway 原生 MCP server（`/mcp`） | ✅ |
| `shared/TeamsGateway.Contracts` | 對外 wire DTO，服務與 .NET client 共用 | ✅ |
| `shared/TeamsGateway.ServiceDefaults` | Aspire 共用（OTel / health / resilience） | ✅ |
| `apphost/TeamsGateway.AppHost` | Aspire 編排（只在 dev，不部署） | ✅ |
| `spike/` | 逆向非官方 API 的拋棄式 Python 腳本；結論在 [`spike/FINDINGS.md`](spike/FINDINGS.md) | — |

技術選型、結構、monorepo 的理由見 [`docs/adr/`](docs/adr/)（0001 選型、0002 服務內部結構、0003 monorepo）。

## 開始

```
dotnet build        # 建整個 solution
aspire run          # 起 Aspire dashboard + gateway
```

gateway 的 bootstrap（綁定 Teams 帳號）與 API 見 [`services/TeamsGateway.Gateway/README.md`](services/TeamsGateway.Gateway/README.md)。
