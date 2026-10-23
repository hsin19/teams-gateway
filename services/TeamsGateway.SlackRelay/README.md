# TeamsGateway.SlackRelay

把個人版 Teams 的訊息轉進 Slack，目標是日常只用 Slack 收發。它是 gateway 的**訂閱者**，只用公開的 `/v1` API + API key，不碰 Teams 非官方 API。

分兩階段：

- **Phase 1（已做）Teams → Slack**：當 `GET /v1/events`（SSE）的訂閱者，把 Teams 訊息貼進對應的 Slack 頻道。斷線用 `Last-Event-ID`（事件序號）續接；relay 自己重啟時序號沒了，改從 Slack 上最後一則轉過去的 Teams 訊息之後續接（`afterMessage`），停機期間的不會漏，重送的照版本去重。60 秒沒收到任何資料（gateway 每 20 秒有心跳）就當連線半死、重連。
- **Phase 2（未做）Slack → Teams**：Socket Mode 收 Slack 回覆 → 對應回 Teams 對話 → `POST /v1/chats/{id}/messages`。

## Slack app

bot 的 app 定義在 [`slack-app-manifest.yaml`](slack-app-manifest.yaml)。到 api.slack.com/apps → Create New App → From an app manifest 貼它建立，Install 後把 **Bot User OAuth Token**（`xoxb-…`）設成 `Relay__Slack__BotToken`。各 scope 的用途寫在 manifest 註解；改了 scope 要重新 Install 才生效。

表情：Teams 有人按 / 收回表情時，在那則 Slack 訊息的 thread 留一句「*誰* reacted 👀」/「removed」，bot 也在原訊息按上同一個 Slack 表情（最後一個人收回時拿掉），頻道裡一眼看得到有人按。表情代碼對照見 `TeamsEmoji`，對不到 Slack 表情的只留言（顯示原始代碼）。需要 `reactions:write`（manifest 已列，舊的 app 要加上並重新 Install）。

訊息裡的內嵌圖片：relay 經 gateway 的 `/v1/media` 抓下來，上傳成 Slack 檔案（不分享到頻道），再用 image block 貼在同一則訊息裡；抓不到或上傳失敗時文字照送，末尾註記幾張圖未能轉送。

## 設定

appsettings 放完整 default，secret 走 env（雙底線覆蓋）或 user-secrets。

| 設定 | env | 說明 |
|---|---|---|
| `Relay:Gateway:BaseUrl` | `Relay__Gateway__BaseUrl` | gateway 位址（預設 `http://localhost:5199`） |
| `Relay:Gateway:ApiKey` | `Relay__Gateway__ApiKey` | gateway 的 API key（`ops/service.sh key <name>` 產） |
| `Relay:Slack:BotToken` | `Relay__Slack__BotToken` | Slack bot token（`xoxb-…`，scope 見 manifest） |
| `Relay:Slack:DefaultChannel` | `Relay__Slack__DefaultChannel` | 沒對應到 ChannelMap 的對話落這個頻道；不設 = 不轉 |
| `Relay:Slack:Routes` | — | `Teams 對話 → Slack 頻道` 路由（list） |

**頻道策略**：有 `Routes` 就只訂閱表內那些 Teams 對話、各自轉到對應頻道；要「全部進一個頻道」就只設 `DefaultChannel`、不填 Routes。

Routes 刻意用 **list** 而非 dict：Teams 對話 id 含 `:`，當 config dict 的 key 會被 .NET config 的階層分隔符拆掉。非 secret 但屬部署專屬，放 gitignored 的 `appsettings.Local.json`（會載入、不進 repo）。

```jsonc
// appsettings.Local.json：兩個 Teams 群各自一個 Slack 頻道
"Relay": {
  "Slack": {
    "Routes": [
      { "Chat": "19:aaa@thread.v2", "Channel": "C0ABC" },
      { "Chat": "19:bbb@thread.v2", "Channel": "C0DEF" }
    ]
  }
}
```

## 跑

```
Relay__Gateway__ApiKey=tgw_… Relay__Slack__BotToken=xoxb-… Relay__Slack__DefaultChannel=C0ABC \
  dotnet run --project services/TeamsGateway.SlackRelay
```

沒設 BotToken 或任何頻道時，worker 會記一筆 error 然後不啟動（不會空轉）。
