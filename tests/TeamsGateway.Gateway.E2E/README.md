# TeamsGateway.Gateway.E2E

對**實際在跑的 gateway** 打真帳號的 live canary，驗我們跟 Teams 的溝通沒被它改版打爛。黑箱，只走 HTTP，不碰 gateway 內部——所以同一份測試能打本機或線上部署。

## 跑

靠環境變數配置；沒設 `GATEWAY_API_KEY` 就整組 skip（一般 `dotnet test` 不會誤跑）。

| 環境變數 | 預設 | 說明 |
|---|---|---|
| `GATEWAY_API_KEY` | （無）| gateway 的 API key（`Bearer`）。**沒設 = skip** |
| `GATEWAY_BASE_URL` | `http://localhost:5199` | 打哪台 gateway。本機先用預設，CI 指到部署目標 |
| `TEAMS_E2E_CHAT` | 自測空群 | round-trip 發話的對話；預設只有自己的群，不吵到真群。要先在 gateway 上訂閱（`PUT /admin/subscriptions`），不然收不到推播 |

```
GATEWAY_API_KEY=tgw_xxx dotnet test --project tests/TeamsGateway.Gateway.E2E/TeamsGateway.Gateway.E2E.csproj
```

## 驗什麼

- **列對話** — 成功就隱含證明 skypetoken 換發（打得到 chatsvc）、讀路徑、回傳形狀還能反序列化進 `TeamsGateway.Contracts`（Teams 改 schema 會在這爆）。
- **推播 + 發話 → 讀回** — 先連上 `/v1/events`（SSE），發一則帶唯一標記的訊息到自測群，10 秒內要從 SSE 收到它，再從 Teams 讀回同一則。輪詢會把 10 秒內的新訊息留給 Trouter，所以這段時間內收到就是 Trouter 推的；Trouter 壞了、只剩輪詢在補，這裡會失敗。

- **發話的選填欄位**（ADR 0012）— 先發一則 `html` 加自訂 16 碼 `clientMessageId`，再用 `replyToId` 發一則引用它的回覆：回應的 `messageId` 就是推回來那則的 id，`clientMessageId` 原樣推回、讀回，HTML 標籤還在，引用回覆的 `qtdMsgs` 被 Teams 驗成 `Valid`（真的當成回覆）。每次多發兩則到自測群。
- **改、刪自己發的**（ADR 0013）— 發一則再 `PUT` 改掉：推回來是同一個 id 的編輯、`clientMessageId` 還在；引用回覆改的時候再帶 `replyToId`，引用還在、仍是 `Valid`；兩則都 `DELETE`，推回來是刪除；刪掉的再改是 409、沒有的是 404。每次多發兩則到自測群，最後都刪掉。

新功能的測試第一行呼叫 `SkipUnlessVersionAtLeastAsync("<版本>")`：目標 gateway 的 `/version` 比它舊就 skip，功能合併了、部署端還沒換版時每天的 canary 不會紅。本機沒帶版本（`1.0.0+…`）一律會跑；讀不到版本也照跑。出版的版本號比門檻小的話，記得把門檻改成實際出版的那一版。

形狀斷言共用 `packages/TeamsGateway.Contracts` 的 wire DTO：契約只有一份 source of truth，Teams 真的變了就一定被我們看到。

## 排程

`.github/workflows/canary.yml` 每天打一次部署端，設定與 secret 見 [workflow 說明](../../.github/workflows/README.md#canary)。canary 打的是**我們自己的 gateway**，runner 上不放 Teams cookie（cookie 只待在 gateway 主機）。失敗才告警。
