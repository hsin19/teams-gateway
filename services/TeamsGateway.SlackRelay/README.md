# TeamsGateway.SlackRelay

把個人版 Teams 的訊息轉進 Slack，目標是日常只用 Slack 收發。它是 gateway 的**訂閱者**，只用公開的 `/v1` API + API key，不碰 Teams 非官方 API。

分兩階段：

- **Phase 1（已做）Teams → Slack**：當 `GET /v1/events`（SSE）的訂閱者，把 Teams 訊息貼進對應的 Slack 頻道。斷線用 `Last-Event-ID` 續接。
- **Phase 2（未做）Slack → Teams**：Socket Mode 收 Slack 回覆 → 對應回 Teams 對話 → `POST /v1/chats/{id}/messages`。

## 設定

appsettings 放完整 default，secret 走 env（雙底線覆蓋）或 user-secrets。

| 設定 | env | 說明 |
|---|---|---|
| `Relay:Gateway:BaseUrl` | `Relay__Gateway__BaseUrl` | gateway 位址（預設 `http://localhost:5199`） |
| `Relay:Gateway:ApiKey` | `Relay__Gateway__ApiKey` | gateway 的 API key（`ops/service.sh key <name>` 產） |
| `Relay:Slack:BotToken` | `Relay__Slack__BotToken` | Slack bot token（`xoxb-…`，需 `chat:write`） |
| `Relay:Slack:DefaultChannel` | `Relay__Slack__DefaultChannel` | 沒對應到 ChannelMap 的對話落這個頻道；不設 = 不轉 |
| `Relay:Slack:ChannelMap` | — | `Teams 對話 id → Slack 頻道` 對照表 |

**頻道策略**：有 `ChannelMap` 就只訂閱表內那些 Teams 對話、各自轉到對應頻道；要「全部進一個頻道」就只設 `DefaultChannel`、不填 map。

```jsonc
// 範例：兩個 Teams 群各自一個 Slack 頻道
"Relay": {
  "Slack": {
    "ChannelMap": {
      "19:aaa@thread.v2": "C0ABC",
      "19:bbb@thread.v2": "C0DEF"
    }
  }
}
```

## 跑

```
Relay__Gateway__ApiKey=tgw_… Relay__Slack__BotToken=xoxb-… Relay__Slack__DefaultChannel=C0ABC \
  dotnet run --project services/TeamsGateway.SlackRelay
```

沒設 BotToken 或任何頻道時，worker 會記一筆 error 然後不啟動（不會空轉）。
