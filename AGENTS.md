# AGENTS.md

給在這個 repo 工作的 coding agent 的規則與慣例。專案介紹與元件見 [`README.md`](README.md)，設計決策見 [`docs/adr/`](docs/adr/)。

## 專案速覽

個人版 Teams（teams.live.com）→ HTTP / SSE gateway，加上把訊息轉進 Slack 的 relay。.NET 10 + Aspire 的 monorepo（ADR 0003）。

| 路徑 | 內容 |
|---|---|
| `services/` | 會部署的服務：`TeamsGateway.Gateway`、`TeamsGateway.SlackRelay` |
| `packages/` | 共用：`Contracts`（對外 wire DTO）、`ServiceDefaults`（OTel / health / resilience） |
| `clients/` | Python client、MCP 接法 |
| `tools/` | Aspire AppHost（只在 dev） |
| `tests/` | UnitTests / FunctionalTests / E2E |
| `spike/` | 逆向非官方 API 的拋棄式 Python 腳本，結論在 `spike/FINDINGS.md` |
| `docs/adr/` | 設計決策 |

## 建置與測試

```
dotnet build
dotnet test --project tests/TeamsGateway.Gateway.UnitTests
dotnet test --project tests/TeamsGateway.Gateway.FunctionalTests
GATEWAY_TEST_DB=postgres dotnet test --project tests/TeamsGateway.Gateway.FunctionalTests   # 同一組改跑 Postgres（Aspire 起 container，要有 Docker）
dotnet test --project tests/TeamsGateway.SlackRelay.UnitTests
dotnet test --project tests/TeamsGateway.Gateway.UnitTests -- --filter-method '*Reaction*'   # 只跑部分
```

- 測試 runner 是 Microsoft.Testing.Platform：指定專案要用 `--project`，篩選參數放在 `--` 後面。
- `TreatWarningsAsErrors` 開著，warning 會擋建置。
- E2E 打的是真帳號、會真的在 Teams 發訊息；沒設 `GATEWAY_API_KEY` 會整組 skip。要跑先問。
- 改了行為就補測試；修 bug 時先確認新測試在修正前會失敗。

## 程式慣例

- 照周圍的寫法：primary constructor、`sealed`、DTO 用 `record`；JSON 走 source generator（`*JsonContext`），新的 wire 型別要註冊進去。
- 註解、XML doc、log 訊息用繁體中文，寫「為什麼」，不重複程式碼已經講清楚的事。
- 測試名稱是英文、底線分隔的句子（例如 `Upsert_inserts_then_dedupes_same_version`），上面用中文註解說明情境；`CancellationToken` 用 `TestContext.Current.CancellationToken`，DB 用 `TestDb`。
- DB 可切換 SQLite / Postgres（`Gateway:Database:Provider`，ADR 0008），本機預設 SQLite。動到 DB 的程式兩邊都要能跑：改完跑一次 Postgres 版的 functional test；寫 `PublishedEvents` 只能經過 `MessageIngestor` 的寫入鎖（序號要等於 commit 順序）。
- DB 沒有 migration（`EnsureCreated`）：改了 EF model，既有的 `.data/gateway.db` 要刪掉重建。裡面有登入 cookie 和 API key，**刪之前一定要先問**。
- 事件相關的程式守 ADR 0006 的契約：所有事件種類同一個串流、順序只保證在同一個對話內、去重與游標分對話記。

## 文件規則

- **改了檔案，要檢查它每一層上層目錄的 `README.md`（存在的話）需不需要跟著更新，一路到 repo 根目錄。** 例如改了 `packages/TeamsGateway.ServiceDefaults/Extensions.cs`，要看：
  - `packages/TeamsGateway.ServiceDefaults/README.md`
  - `packages/README.md`
  - `README.md`
- 設計上的決定寫成 ADR：`docs/adr/NNNN-<slug>.md`，格式照既有的。新的決定修正或推翻舊 ADR 時，不改寫舊 ADR 的內容，在舊的那份加一句指向新的；新增 ADR 時一併更新根 `README.md` 的 ADR 清單。
- 文件用繁體中文；程式碼識別字、路徑、設定鍵用反引號。

## Commit

- Conventional Commits，英文：`type(scope): summary`。type 常用 `feat` / `fix` / `refactor` / `chore`；scope 是元件名（`gateway`、`slack-relay`），跨元件的可以省略。
- body 說明為什麼改、影響到什麼，約 72 字元換行。

## 機密與本機狀態

下面都不在版控裡：不要加進去，也不要把內容印出來。

| 路徑 | 內容 |
|---|---|
| `tools/TeamsGateway.AppHost/appsettings.Local.json` | 本機開發的 secret 與設定（admin token、relay 的 API key、Slack bot token、Routes），key 是服務吃的環境變數名稱。要確認有沒有設只看鍵名（`jq keys`） |
| `.data/` | SQLite（訊息、API key、加密過的登入 cookie）與解密用的 DataProtection key |
| `services/TeamsGateway.SlackRelay/appsettings.Local.json` | 部署那台用的 Teams 對話 → Slack 頻道路由。本機不放：relay 最後載入它，會蓋掉 AppHost 帶的 env |
| `spike/.local/` | 抓到的流量、token、瀏覽器 profile |

## 本機運作與除錯

本機只當開發環境（ADR 0007），沒有常駐服務。

- `aspire start` 在背景起 gateway（`localhost:5199`）+ slack-relay + dashboard，`aspire stop` 停；`aspire ps` 看有沒有在跑。改了程式要 `aspire resource <gateway|slack-relay> rebuild`（`restart` 不會重新編譯）才會生效；改了 `AppHost.cs` 或它的 `appsettings.Local.json` 要 `aspire stop` 再 `aspire start`。
- repo 根目錄的 `aspire.config.json` 指定 AppHost：functional test 專案也是 Aspire SDK（Postgres 版測試要起 container），不指定的話 CLI 會要你二選一，非互動模式直接失敗。
- 全域 `aspire` CLI 要跟 `global.json` 的 `Aspire.AppHost.Sdk` 同一個 minor（現在 13.6）。CLI 比較舊時會經 `dnx` 轉給新版跑、外層連不上 backchannel，`aspire start` 剛好 120 秒後整組被砍（log 有 `Timed out waiting 120s for AppHost to start`）；`dotnet tool update -g Aspire.Cli` 升上去。
- log：`aspire logs <resource> -t`（帶時間）、`aspire otel logs <resource>`（structured）。`Microsoft.*` / `System.*` / `Polly` 只記 warning 以上。dashboard 不留歷史，要查更早「什麼時候發生」看 DB 的時間欄位（`MessageEvents.ComposeTime` / `ReceivedAt`、`PublishedEvents.PublishedAt`，Unix 毫秒），用唯讀模式開：`sqlite3 'file:.data/gateway.db?mode=ro'`。
- 訊息晚到先查 Mac 有沒有睡眠（`pmset -g log`）：睡眠時 gateway 跟著暫停，醒來後由輪詢補上並記 `Trouter 漏接` warning，不一定是推播壞了。
