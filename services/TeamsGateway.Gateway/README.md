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

## 對外 API（給 skills / client / slack-relay 接）

帶 `Authorization: Bearer tgw_...`：

| 端點 | 說明 |
|---|---|
| `GET /v1/chats` | 列對話 |
| `GET /v1/chats/{id}/messages?limit=&cursor=` | 讀訊息（`cursor` 用上一頁回的 `nextCursor` 往更舊翻） |
| `POST /v1/chats/{id}/messages` `{"text":"..."}` | 發話 |
| `GET /v1/events?chats=<id>,<id>` | **SSE** 即時新訊息串流（像 Slack Socket Mode）；`chats` 省略 = 全部 |
| `POST /mcp` | **MCP** server（Streamable HTTP）：`list_chats` / `read_messages` / `send_message`，給 agent 用。接法見 `clients/mcp/` |
| `POST/GET/DELETE /v1/webhooks` | 登記 / 列 / 刪 webhook；有符合的新訊息時 gateway POST 過去，帶 `X-TeamsGateway-Signature: sha256=<hmac>` |

即時推播由背景的 Trouter 連線收進來，寫進 `MessageEvents` 後 publish 給 SSE。自己發的訊息也會推回來（`from` 是自己）。

```
curl -N -H "Authorization: Bearer $KEY" "$BASE/v1/events?chats=19:...@thread.v2"
```
