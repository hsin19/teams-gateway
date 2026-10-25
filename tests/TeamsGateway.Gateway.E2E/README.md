# TeamsGateway.Gateway.E2E

對**實際在跑的 gateway** 打真帳號的 live canary，驗我們跟 Teams 的溝通沒被它改版打爛。黑箱，只走 HTTP，不碰 gateway 內部——所以同一份測試能打本機或線上部署。

## 跑

靠環境變數配置；沒設 `GATEWAY_API_KEY` 就整組 skip（一般 `dotnet test` 不會誤跑）。

| 環境變數 | 預設 | 說明 |
|---|---|---|
| `GATEWAY_API_KEY` | （無）| gateway 的 API key（`Bearer`）。**沒設 = skip** |
| `GATEWAY_BASE_URL` | `http://localhost:5199` | 打哪台 gateway。本機先用預設，CI 指到部署目標 |
| `TEAMS_E2E_CHAT` | 自測空群 | round-trip 發話的對話；預設只有自己的群，不吵到真群 |

```
GATEWAY_API_KEY=tgw_xxx dotnet test --project tests/TeamsGateway.Gateway.E2E/TeamsGateway.Gateway.E2E.csproj
```

## 驗什麼

- **列對話** — 成功就隱含證明 skypetoken 換發（打得到 chatsvc）、讀路徑、回傳形狀還能反序列化進 `TeamsGateway.Contracts`（Teams 改 schema 會在這爆）。
- **發話 → 讀回** — 發一則帶唯一標記的訊息到自測群，再從 Teams 讀回同一則。

形狀斷言共用 `packages/TeamsGateway.Contracts` 的 wire DTO：契約只有一份 source of truth，Teams 真的變了就一定被我們看到。

## 排程

`.github/workflows/canary.yml` 每天打一次部署端，設定與 secret 見 [workflow 說明](../../.github/workflows/README.md#canary)。canary 打的是**我們自己的 gateway**，runner 上不放 Teams cookie（cookie 只待在 gateway 主機）。失敗才告警。
