# teams-gateway

把個人版 Microsoft Teams（teams.live.com）包成給 CLI、app、其他服務用的介面。個人版沒有 bot API / webhook / 官方 API，
這個 monorepo 用一個專用帳號登入，對外提供 REST + 即時事件串流，並在其上長出轉送、client 等元件。
目標是掛在一台機器（或雲端）上，其他人要做 Teams 機器人只要打 HTTP。

## 元件

**服務**（會部署、對外提供能力）

| 路徑 | 是什麼 | 狀態 |
|---|---|---|
| `services/TeamsGateway.Gateway` | Teams → HTTP/SSE gateway（列群組/讀/發/事件串流） | ✅ 可用 |
| `services/TeamsGateway.SlackRelay` | 訂閱 gateway、把訊息轉進 Slack（只看 Slack 就能接收與回覆） | Phase 1 ✅（Teams→Slack） |

**Client**（接 gateway 的）

| 路徑 | 是什麼 | 狀態 |
|---|---|---|
| `clients/python` | Python client（程式 / 常駐訂閱用，含 SSE） | ✅ |
| `clients/mcp` | agent 接法：gateway 原生 MCP server（`/mcp`） | ✅ |

**共用 / 編排**（不是服務，不單獨部署）

| 路徑 | 是什麼 | 狀態 |
|---|---|---|
| `packages/TeamsGateway.Contracts` | 對外 wire DTO，服務與 .NET client 共用 | ✅ |
| `packages/TeamsGateway.ServiceDefaults` | Aspire 共用（OTel / health / resilience） | ✅ |
| `tools/TeamsGateway.AppHost` | Aspire 編排：本機開發一次起 gateway + relay（不部署） | ✅ |
| `spike/` | 逆向非官方 API 的拋棄式 Python 腳本；結論在 [`spike/FINDINGS.md`](spike/FINDINGS.md) | — |

技術選型、結構等決策的理由見 [`docs/adr/`](docs/adr/)（0001 選型、0002 服務內部結構、0003 monorepo、0004 先跑在本機、0005 agent 介面用 MCP、0006 事件出入口抽象化、0007 本機只當開發環境）。

## 開始

本機只當開發環境（ADR 0007），正式部署見 [`DEPLOY.md`](DEPLOY.md)。全域 `aspire` CLI 要跟 `global.json` 的 `Aspire.AppHost.Sdk` 同版（`dotnet tool update -g Aspire.Cli`）。

```
dotnet build        # 建整個 solution
aspire run          # 起 dashboard + gateway（localhost:5199）+ slack-relay；aspire start 則在背景跑
```

本機的 secret 與設定放 `tools/TeamsGateway.AppHost/appsettings.Local.json`（gitignored），key 是服務吃的環境變數名稱，AppHost 原樣帶給對應的服務（哪個給誰見 `AppHost.cs`）：

```jsonc
{
  "Gateway__AdminToken": "…",            // 自己產，例如 openssl rand -hex 24
  "Relay__Gateway__ApiKey": "tgw_…",      // gateway bootstrap 後 POST /admin/api-keys 建
  "Relay__Slack__BotToken": "xoxb-…",
  "Relay__Slack__Routes__0__Chat": "19:…@thread.v2",   // Teams 對話 → Slack 頻道；list 攤平成 __<i>__
  "Relay__Slack__Routes__0__Channel": "C…"            // 本機只指 Slack 測試頻道（ADR 0007）
}
```

gateway 的 bootstrap（綁定 Teams 帳號）與 API 見 [`services/TeamsGateway.Gateway/README.md`](services/TeamsGateway.Gateway/README.md)。
