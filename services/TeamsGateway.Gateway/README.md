# TeamsGateway.Gateway

個人版 Microsoft Teams（teams.live.com）的 HTTP/SSE gateway。個人版沒有 bot API、webhook 或官方 API，
gateway 用一個專用帳號登入，對外提供 REST（列群組/讀/發）與即時事件串流（SSE）。

技術選型與結構見 `docs/adr/0001`、`0002`。執行期不需要瀏覽器（spike 證明，見 `spike/FINDINGS.md`）。

## 開發

本機只當開發環境（`docs/adr/0007`），用 AppHost 跑，聽 `localhost:5199`：

```
aspire start                          # 背景起 dashboard + gateway + slack-relay（aspire run 則掛在前景）
aspire logs gateway -t -f             # 看 log（帶時間）
aspire resource gateway rebuild       # 改了程式後重新編譯再起（restart 不會重新編譯）
aspire stop
```

AppHost 把 `Gateway:DataDir` 指到 repo 根目錄的 `.data/`（cookie / DataProtection key / SQLite，gitignored），admin token 從 `tools/TeamsGateway.AppHost/appsettings.Local.json` 的 `Gateway__AdminToken` 帶進來。單獨 `dotnet run --project services/TeamsGateway.Gateway` 也能跑，但這兩個要自己用 env 給，不然資料會落在專案目錄底下的 `.data/`。

`Microsoft.*` / `System.*` / `Polly` 只記 warning 以上（`appsettings.json` 的 `Logging:LogLevel`）；要看 HTTP / EF 細節時，暫時把 `appsettings.Development.json` 的等級調成 `Information` 再重啟。

DB schema：正式部署前不做 migration，啟動時照 EF model 直接建（`EnsureCreated`）。改了 model 之後啟動會擋下來並提示——把 `.data/gateway.db` 刪掉重啟就會重建，裡面的登入 cookie 與 API key 會一起清掉，要重新 bootstrap（見下）、relay 的 `Relay__Gateway__ApiKey` 也要換新的。

Mac 睡眠時 gateway 跟著暫停，醒來後輪詢 / 回補會把缺口補上並照樣發出，只是晚到。關掉「閒置自動睡眠」擋不住闔上螢幕的睡眠（`pmset -g log` 會看到 `Clamshell Sleep`）；要闔著也繼續跑，用 `sudo pmset -a disablesleep 1`（用電池時也不睡，帶出門前改回 `0`）。

## Bootstrap：綁定 Teams 帳號

第一次要把專用帳號的登入 cookie 匯進來（之後 gateway 自己換 token）。cookie 用 `spike/http_refresh.py export` 從已登入的瀏覽器 profile 匯出。

`/admin` 端點帶 `X-Admin-Token`（設定 `Gateway:AdminToken`，可用 env `Gateway__AdminToken`）：

```
curl -X PUT  -H "X-Admin-Token: $TOKEN" --data-binary @login_cookies.json  $BASE/admin/login-cookies
curl -X POST -H "X-Admin-Token: $TOKEN"                                     $BASE/admin/auth/refresh   # 換一次 token 驗證
curl -X POST -H "X-Admin-Token: $TOKEN" -H "Content-Type: application/json" -d '{"name":"client"}' $BASE/admin/api-keys       # 建 API key，回的 apiKey 只出現一次
```

cookie 與 token 以 DataProtection 加密後存進 SQLite（`SecureValues` 表），key 在 `.data/keys/`。

## 訂閱哪些對話（範圍）

gateway **只追 / 存訂閱中的對話**（Trouter 會推帳號的所有對話，但只有訂閱的會入庫、才會進 SSE / webhook）。
訂閱時會同步回補一段歷史（預設 7 天，`Gateway:BackfillDays`，只存不發）；重啟 / 睡眠後也會補停機期間的缺口並發出——給之後接 Elasticsearch 歷史搜尋留料。缺口從每個對話的水位線（`SyncedThrough`，最後一次完整同步到的時間）算起，只有回補 / 輪詢這種整段抓的會推進它；重新訂閱時歸零，沒追的那段不會被當成漏接發出。

**編輯 / 刪除**：訊息 `version` 在編輯時遞增。同 id、version 更大而且看得到的內容（文字 / 附件 / 刪除標記）有變 = 被編輯 / 刪除，gateway 會更新 DB 的內容與 `Edited` / `Deleted` 標記並重發事件。覆蓋前先把舊版本存進 `MessageRevisions` 表（`MessageEvents` 永遠是最新版，`MessageRevisions` 是編輯歷史），所以改過的訊息也看得到被改掉前的內容，給之後 ES 歷史搜尋與稽核留料。推播不一定帶每個編輯，所以還有「慢更新」：每 `Gateway:ReconcileMinutes`（預設 15）分重掃訂閱對話近 `ReconcileWindowHours`（預設 24）小時的訊息，把漏接的編輯 / 刪除補上。

**表情**：有人按 / 收回表情時 Teams 也會讓 `version` 往上跳，但內容沒變，不當編輯（不留 revision、不重發 `message`），改比對前後兩版發 `reaction` 事件。晚到的訊息（例如睡眠後才被輪詢 / 回補抓到）第一次看到時就已經掛著的表情，會接在那則 `message` 之後一起發出，不會因為沒有前一版可比而漏掉。

**輪詢保險**：Trouter 推播是主路徑，但另外每 `Gateway:PollSeconds`（預設 20）秒抓一次各訂閱對話最近 15 分鐘的訊息，把漏推的新訊息照時間順序補發（最近 10 秒內的先留給推播，避免搶先誤判）。水位線落後超過 15 分鐘（停機、Mac 睡眠）時，輪詢改成從水位線整段回補。凡是靠輪詢 / 慢更新 / 啟動回補才抓到的新訊息都會記 warning（`Trouter 漏接，…補到`），可以拿來觀察推播可靠度。Mac 睡眠期間發的訊息醒來後也是輪詢補到、記同一個 warning，不代表推播壞了——對照 `pmset -g log` 的睡眠時間判斷。

**Metrics**（meter `TeamsGateway.Gateway`，隨 ServiceDefaults 的 OpenTelemetry 匯出；設了 `OTEL_EXPORTER_OTLP_ENDPOINT` 才會送出去）：

| 指標 | 說明 |
|---|---|
| `teams_gateway.messages.delivered` | 發給訂閱者的訊息數，tag `source`（trouter / poll / reconcile / backfill）、`change`（new / edit）、`sender`（self / others） |
| `teams_gateway.push.latency` | Trouter 推到的新訊息從發送到收到的秒數，tag `sender` |

推播健不健康看 `sender=others` 的 `source` 分布：正常幾乎都是 `trouter`，`poll` 變多（扣掉睡眠醒來補的）就是推播壞了（自己帳號的訊息不管怎樣都會推來，不能拿來判斷）。開發時 dashboard 的 Metrics 頁就看得到。沒有後端時直接看行程內的值（`dnx dotnet-counters ps` 找 PID；counter 顯示的是每秒增量）：

```
dnx dotnet-counters monitor -p <pid> --counters TeamsGateway.Gateway
```

```
curl -X PUT    -H "X-Admin-Token: $T" -H "Content-Type: application/json" -d '{"chat":"General Chat"}' $BASE/admin/subscriptions  # chat 可給 id 或群名
curl           -H "X-Admin-Token: $T"                                        $BASE/admin/subscriptions  # 列訂閱
curl -X DELETE -H "X-Admin-Token: $T"  $BASE/admin/subscriptions/<id>                                    # 取消（保留已存歷史）
```

## 對外 API（給 skills / client / slack-relay 接）

API 文件在 `/scalar`（OpenAPI 文件 `/openapi/v1.json`），根目錄 `/` 會轉過去。

帶 `Authorization: Bearer tgw_...`：

| 端點 | 說明 |
|---|---|
| `GET /v1/chats` | 列對話 |
| `GET /v1/chats/{id}/messages?limit=&cursor=` | 讀訊息（`cursor` 用上一頁回的 `nextCursor` 往更舊翻） |
| `POST /v1/chats/{id}/messages` `{"text":"..."}` | 發話 |
| `GET /v1/messages/{id}/revisions` | 一則訊息的編輯歷史：`current`（目前版本）+ `revisions`（被取代的舊版本，由舊到新，含 `content` / `superseded_at`） |
| `GET /v1/media?url=<圖片網址>` | 代抓訊息內嵌圖片（`<img src>` 指向 `*.asm.skype.com`，要 Teams 登入才拿得到）；只接受這個網域 |
| `GET /v1/events?chats=<id>,<id>` | **SSE** 即時事件串流（像 Slack Socket Mode）；`chats` 省略 = 全部。事件兩種：`event: message`（新訊息 / 編輯 / 刪除，`MessageDto`）、`event: reaction`（有人按 / 收回表情，`ReactionDto`：誰、哪個表情、還剩幾個人）。SSE `id` 是事件序號（不是訊息 id，編輯會沿用訊息 id）；重連帶 `Last-Event-ID`（上次收到的 `id`）會先補回斷線期間發過的事件（保留 `Gateway:EventRetentionDays`，預設 7 天）；訂閱者重啟、沒留序號時可帶 `afterMessage=<Teams 訊息 id>`（最後處理的那則），從它第一次發出之後補（之後的編輯也會重送，照 `version` 去重）。閒置時每 `Gateway:SseHeartbeatSeconds`（預設 20）秒送 `: ping` 心跳，訂閱者可據此判斷連線半死；API key 被撤銷時會斷線 |
| `POST /mcp` | **MCP** server（Streamable HTTP）：`list_chats` / `read_messages` / `send_message`，給 agent 用。接法見 `clients/mcp/` |
| `POST/GET/DELETE /v1/webhooks` | 登記 / 列 / 刪 webhook；有符合的新訊息時 gateway POST 過去，帶 `X-TeamsGateway-Signature: sha256=<hmac>`。`X-TeamsGateway-Event` 是 `message` 或 `reaction`（body 分別是 `MessageDto` / `ReactionDto`）。目標只能是 http(s)，預設不能打 loopback / 私有網段（訂閱者在同機 / 同網段 / 同 docker 網路時設 `Gateway:WebhookAllowPrivateNetworks=true`），link-local（雲端 metadata）一律不行；不跟 redirect |

即時推播由背景的 Trouter 連線收進來，寫進 `MessageEvents` 後 publish 給 SSE。自己發的訊息也會推回來（`from` 是自己）。

```
curl -N -H "Authorization: Bearer $KEY" "$BASE/v1/events?chats=19:...@thread.v2"
```
