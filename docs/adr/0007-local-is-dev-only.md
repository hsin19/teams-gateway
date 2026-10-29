# ADR 0007：本機只當開發環境，用 Aspire 跑

## 決定

- 本機沒有「正式環境」，只有開發：`aspire run` 一次起 gateway + slack-relay + dashboard，要用才開、改完重啟。不再用 launchd 常駐。
- 正式環境之後另找一台機器跑 container（`docker-compose.yml`、`DEPLOY.md`），上線時在那台重新 bootstrap、Slack 頻道重新整理，不沿用本機的資料。（後來 compose 與部署說明移到部署端，repo 只由 CI 出 image，見根目錄 README〈部署〉。）
- Slack 頻道分開：本機只轉進 Slack 測試頻道，正式環境不綁測試頻道。（ADR 0012 之後：relay 讀頻道裡的 metadata 只認自己的 bot 貼的，本機改用另一個 Slack app 的話，正式環境也可以把測試用的 Teams 對話轉進同一個測試頻道，兩邊互不干擾。本機要開發話功能時一定要用另一個 app：同一個 app 的 Socket Mode 連線會互搶事件。）正式環境上線前，本機暫時沿用現有的 4 條 Routes（其中測試用的 Teams 對話 → Slack 測試頻道）；上線後本機的 Routes 全部改指測試頻道，其他正式頻道只由正式環境發。
- 取代 ADR 0004。

## 背景

0004 用 launchd 把 gateway 掛在本機當「正式」在用，結果開發多出一套平行的跑法：

- gateway 跑 publish 出來的版本，改了程式要 `ops/service.sh restart` 重新 publish；relay 又另外 `dotnet run`。
- 兩套設定互相對不上：admin token 在 `.env`、relay 的 secret 在另一個 `.env`；launchd 聽 5199、`dotnet run` / Aspire 聽 5243；`DataDir` 一個指 repo 根目錄、一個預設落在專案目錄。用 `aspire run` 會連到一顆空的 DB。
- log 是沒有時間戳的文字檔，查事情要回頭翻 DB 時間欄位。

最終要部署到另一台，本機常駐只是過渡；繼續補它（自動重啟、log 輪替）是在做會丟掉的東西。現階段要的是開發穩、好重現，不是訊息不漏——停掉、睡眠期間沒收到的，gateway 下次起來會由回補 / 輪詢補上，也就夠了。

## 開發的跑法

- AppHost（`tools/TeamsGateway.AppHost`）把設定接好：
  - gateway 固定 `localhost:5199`（`launchSettings.json`），relay、client、MCP、E2E 的預設都指這裡。
  - `Gateway__DataDir` 指 repo 根目錄的 `.data/`，不會因為 content root 不同而分家。
  - relay 的 gateway 位址從 gateway 的 endpoint 帶進去，等 gateway `/health` 過了才起。
- 本機的 secret 與設定放 `tools/TeamsGateway.AppHost/appsettings.Local.json`（gitignored），key 直接用服務吃的扁平環境變數名稱（`Gateway__AdminToken`、`Relay__Slack__BotToken`…），AppHost 原樣轉成 env 帶給對應的服務。看 `AppHost.cs` 就知道正式環境要設哪些環境變數、各給誰用，不多一層參數名稱對照。沒設的就不帶，由服務自己處理缺值，第一次 bootstrap 前還沒有 API key 也起得來。
- log / trace / metrics 看 dashboard（structured log 有時間）；dashboard 不留歷史，要查更早的事還是看 DB 時間欄位。
