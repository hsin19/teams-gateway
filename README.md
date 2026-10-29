# teams-gateway

把個人版 Microsoft Teams（teams.live.com）包成給 CLI、app、其他服務用的介面。個人版沒有 bot API / webhook / 官方 API，
這個 monorepo 用一個專用帳號登入，對外提供 REST + 即時事件串流，並在其上長出轉送、client 等元件。
目標是掛在一台機器（或雲端）上，其他人要做 Teams 機器人只要打 HTTP。

## 元件

**服務**（會部署、對外提供能力）

| 路徑 | 是什麼 | 狀態 |
|---|---|---|
| `services/TeamsGateway.Gateway` | Teams → HTTP/SSE gateway（列群組/讀/發/事件串流） | ✅ 可用 |
| `services/TeamsGateway.SlackRelay` | 訂閱 gateway、把訊息轉進 Slack（只看 Slack 就能接收與回覆） | Phase 1 ✅（Teams→Slack）、Phase 2 ✅（Slack→Teams） |

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

技術選型、結構等決策的理由見 [`docs/adr/`](docs/adr/)（0001 選型、0002 服務內部結構、0003 monorepo、0004 先跑在本機、0005 agent 介面用 MCP、0006 事件出入口抽象化、0007 本機只當開發環境、0008 資料庫可切換 SQLite / Postgres、0009 EF migration、0010 image 由 SDK 出、0011 認證與授權分開、接上 Cloudflare Access、0012 Slack → Teams 發話）。

## 開始

本機只當開發環境（ADR 0007），正式部署見下方〈部署〉。全域 `aspire` CLI 要跟 `global.json` 的 `Aspire.AppHost.Sdk` 同版（`dotnet tool update -g Aspire.Cli`）。

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
  "Relay__Slack__Routes__0__Channel": "C…",           // 本機只指 Slack 測試頻道（ADR 0007）
  "Gateway__Database__Provider": "Postgres"           // 選填：改用 Postgres container（要有 Docker），沒設 = SQLite（ADR 0008）
}
```

gateway 的 bootstrap（綁定 Teams 帳號）與 API 見 [`services/TeamsGateway.Gateway/README.md`](services/TeamsGateway.Gateway/README.md)。

## 部署

兩個服務各一個 image，打 `v*` tag 由 CI 出版（[`.github/workflows/`](.github/workflows/README.md)）：`ghcr.io/hsin19/teams-gateway`、`ghcr.io/hsin19/teams-slack-relay`，tag 是版本號（`1.0.0`、`1.0`）與 `sha-<短 sha>`，同一個 tag 含 `linux/amd64` 與 `linux/arm64`。image 由 .NET SDK 直接出，沒有 Dockerfile（ADR 0010）。compose 等部署設定放在部署端，這個 repo 不帶。

| | gateway | relay |
|---|---|---|
| 副本 | 1：一條 Trouter 連線、單一寫入者 | 1：多副本會重複貼 Slack |
| 執行方式 | JIT（base image `aspnet` chiseled） | Native AOT（base image `runtime-deps` chiseled，image 小、記憶體少） |
| 埠 | 8080（HTTP） | 8080（HTTP，只有 `/health`、`/alive`、`/version`） |
| 持久化 | `/data` volume：DataProtection key，用 SQLite 時還有 `gateway.db`。要讓 uid 1654 寫得進去（bind mount 的話先 `chown 1654:1654`） | 無：狀態在 Slack 訊息的 metadata |
| 健康檢查 | `/health`：含 DB | `/health`：設定齊全、連著 gateway 的事件串流才 Healthy；`/alive`：process 有回應 |
| 版本 | `/version`（純文字，例如 `1.2.3+33be044`），OpenAPI 的 `info.version` 也是它 | `/version` |
| 必要設定 | `Gateway__AdminToken`（或改用 Cloudflare Access 的身分，見 gateway README）；用 Postgres 時加 `Gateway__Database__Provider=Postgres`、`ConnectionStrings__gateway`（gateway 專用的資料庫） | `Relay__Gateway__BaseUrl`、`Relay__Gateway__ApiKey`、`Relay__Slack__BotToken`；Routes 掛檔到 `/app/appsettings.Local.json`（uid 1654 要讀得到） |

- 版本是 build 時壓進去的：release 帶 tag 的版本號，後面接 7 碼 commit（Source Link 從 git 拿）；本機 build 是 `1.0.0+<commit>`。OTel 的 `service.version` 也帶它，log / trace 分得出是哪一版。
- image 以非 root（uid 1654）執行，而且沒有 shell，`docker exec` 進不去；查問題看 log、OTel 與掛出來的 `/data`。
- 開 init（compose 的 `init: true`）：不開的話 `dotnet` 是 container 的 PID 1，背景執行緒上的未處理例外會讓 process 卡在 100% CPU 不結束，restart policy 也不會動。啟動失敗（例如 `/data` 寫不進去、DB 連不上）由 `Program.cs` 接住，有沒有 init 都會以 exit code 1 結束（ADR 0010）。
- schema 啟動時自動套用 migration（ADR 0009）。換版前先備份 `/data`，用 Postgres 時再加 `pg_dump`；tag 用版本號、不要用 `latest`，換版、退版才有依據。
- 第一次上線的 bootstrap（匯入 cookie、訂閱對話、給 relay 建 API key）、對外要開哪些路徑等安全注意，見 gateway README。
- 設了 `OTEL_EXPORTER_OTLP_ENDPOINT` 就會送 OpenTelemetry（log / trace / metrics）。
