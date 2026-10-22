# TeamsGateway.Gateway

個人版 Microsoft Teams（teams.live.com）的 HTTP/SSE gateway。個人版沒有 bot API、webhook 或官方 API，
gateway 用一個專用帳號登入，對外提供 REST（列群組/讀/發）與即時事件串流（SSE）。

技術選型與結構見 `docs/adr/0001`、`0002`。執行期不需要瀏覽器（spike 證明，見 `spike/FINDINGS.md`）。

## 開發

```
aspire run                                        # AppHost：dashboard + gateway
dotnet run --project services/TeamsGateway.Gateway   # 只跑 gateway
```

migration：`dotnet dotnet-ef migrations add <Name> --project services/TeamsGateway.Gateway --output-dir Data/Migrations`

## 本機常駐（launchd）

掛在本機 `localhost:5199`，不用 Docker（見 `docs/adr/0004`）：

```
ops/service.sh install                                   # 發佈 + launchd 啟動（首次產生 .env 的 admin token）
ops/service.sh bootstrap spike/.local/login_cookies.json client   # 匯入 cookie + 建一把 API key
ops/service.sh key <name>                                # 再建 API key（明文只出現一次）
ops/service.sh status | logs | restart | uninstall
```

資料（cookie / token / SQLite）在 repo `.data/`、admin token 在 `.env`（都 gitignored）。約 150–170MB RSS。

## Bootstrap：綁定 Teams 帳號

第一次要把專用帳號的登入 cookie 匯進來（之後 gateway 自己換 token）。cookie 用 `spike/http_refresh.py export` 從已登入的瀏覽器 profile 匯出。

`/admin` 端點帶 `X-Admin-Token`（設定 `Gateway:AdminToken`，可用 env `Gateway__AdminToken`）：

```
curl -X PUT  -H "X-Admin-Token: $TOKEN" --data-binary @login_cookies.json  $BASE/admin/login-cookies
curl -X POST -H "X-Admin-Token: $TOKEN"                                     $BASE/admin/auth/refresh   # 換一次 token 驗證
curl -X POST -H "X-Admin-Token: $TOKEN" -d '{"name":"client"}'              $BASE/admin/api-keys       # 建 API key，回的 apiKey 只出現一次
```

cookie 與 token 以 DataProtection 加密後存進 SQLite（`SecureValues` 表），key 在 `.data/keys/`。

## 訂閱哪些對話（範圍）

gateway **只追 / 存訂閱中的對話**（Trouter 會推帳號的所有對話，但只有訂閱的會入庫、才會進 SSE / webhook）。
訂閱時會同步回補一段歷史（預設 7 天，`Gateway:BackfillDays`）；重啟時也會補停機期間的缺口——給之後接 Elasticsearch 歷史搜尋留料。

**編輯 / 刪除**：訊息 `version` 在編輯時遞增。同 id 但 version 更大 = 被編輯 / 刪除，gateway 會更新 DB 的內容與 `Edited` / `Deleted` 標記並重發事件。覆蓋前先把舊版本存進 `MessageRevisions` 表（`MessageEvents` 永遠是最新版，`MessageRevisions` 是編輯歷史），所以改過的訊息也看得到被改掉前的內容，給之後 ES 歷史搜尋與稽核留料。推播不一定帶每個編輯，所以還有「慢更新」：每 `Gateway:ReconcileMinutes`（預設 15）分重掃訂閱對話近 `ReconcileWindowHours`（預設 24）小時的訊息，把漏接的編輯 / 刪除補上。

**輪詢保險**：Trouter 推播是主路徑，但另外每 `Gateway:PollSeconds`（預設 20）秒抓一次各訂閱對話最近 15 分鐘的訊息，把漏推的新訊息照時間順序補發（最近 10 秒內的先留給推播，避免搶先誤判）。凡是靠輪詢 / 慢更新 / 啟動回補才抓到的新訊息都會記 warning（`Trouter 漏接，…補到`），可以拿來觀察推播可靠度。

**Metrics**（meter `TeamsGateway.Gateway`，隨 ServiceDefaults 的 OpenTelemetry 匯出；設了 `OTEL_EXPORTER_OTLP_ENDPOINT` 才會送出去）：

| 指標 | 說明 |
|---|---|
| `teams_gateway.messages.delivered` | 發給訂閱者的訊息數，tag `source`（trouter / poll / reconcile / backfill）、`change`（new / edit）、`sender`（self / others） |
| `teams_gateway.push.latency` | Trouter 推到的新訊息從發送到收到的秒數，tag `sender` |

推播健不健康看 `sender=others` 的 `source` 分布：正常幾乎都是 `trouter`，`poll` 變多就是推播壞了（自己帳號的訊息不管怎樣都會推來，不能拿來判斷）。沒有後端時直接看行程內的值（launchd 跑的行程名稱是 `dotnet`，用 PID 指定；counter 顯示的是每秒增量）：

```
dnx dotnet-counters monitor -p $(pgrep -f TeamsGateway.Gateway.dll) --counters TeamsGateway.Gateway
```

```
curl -X PUT    -H "X-Admin-Token: $T" -d '{"chat":"General Chat"}' $BASE/admin/subscriptions  # chat 可給 id 或群名
curl           -H "X-Admin-Token: $T"                                        $BASE/admin/subscriptions  # 列訂閱
curl -X DELETE -H "X-Admin-Token: $T"  $BASE/admin/subscriptions/<id>                                    # 取消（保留已存歷史）
```

## 對外 API（給 skills / client / slack-relay 接）

帶 `Authorization: Bearer tgw_...`：

| 端點 | 說明 |
|---|---|
| `GET /v1/chats` | 列對話 |
| `GET /v1/chats/{id}/messages?limit=&cursor=` | 讀訊息（`cursor` 用上一頁回的 `nextCursor` 往更舊翻） |
| `POST /v1/chats/{id}/messages` `{"text":"..."}` | 發話 |
| `GET /v1/messages/{id}/revisions` | 一則訊息的編輯歷史：`current`（目前版本）+ `revisions`（被取代的舊版本，由舊到新，含 `content` / `superseded_at`） |
| `GET /v1/media?url=<圖片網址>` | 代抓訊息內嵌圖片（`<img src>` 指向 `*.asm.skype.com`，要 Teams 登入才拿得到）；只接受這個網域 |
| `GET /v1/events?chats=<id>,<id>` | **SSE** 即時新訊息串流（像 Slack Socket Mode）；`chats` 省略 = 全部。SSE `id` 是事件序號（不是訊息 id，編輯會沿用訊息 id）；重連帶 `Last-Event-ID`（上次收到的 `id`）會先補回斷線期間發過的事件（保留 `Gateway:EventRetentionDays`，預設 7 天） |
| `POST /mcp` | **MCP** server（Streamable HTTP）：`list_chats` / `read_messages` / `send_message`，給 agent 用。接法見 `clients/mcp/` |
| `POST/GET/DELETE /v1/webhooks` | 登記 / 列 / 刪 webhook；有符合的新訊息時 gateway POST 過去，帶 `X-TeamsGateway-Signature: sha256=<hmac>` |

即時推播由背景的 Trouter 連線收進來，寫進 `MessageEvents` 後 publish 給 SSE。自己發的訊息也會推回來（`from` 是自己）。

```
curl -N -H "Authorization: Bearer $KEY" "$BASE/v1/events?chats=19:...@thread.v2"
```
