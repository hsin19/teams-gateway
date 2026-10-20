# teams-gateway

給 CLI 與 app 用的個人版 Microsoft Teams（teams.live.com）包裝。個人版沒有 bot API、webhook 或官方 API，
teams-gateway 用一個專用帳號登入，對外提供：

- HTTP API：列群組、讀訊息、發話
- 事件串流：群組有新訊息就推給訂閱者（SSE / webhook），之後可以接 Elasticsearch

目標是之後掛在一台機器（或雲端）上，其他人要做 Teams 機器人只要打 HTTP。

## 狀態

- `spike/`：拋棄式的 Python 腳本，摸清非官方 API（登入、token、讀寫、即時事件）。結論在 [`spike/FINDINGS.md`](spike/FINDINGS.md)：**執行期完全不需要瀏覽器**。
- `src/`：正式版（.NET 10 + Aspire + EF Core/SQLite），開發中。

## 技術選型

.NET 10 + Aspire + ASP.NET Core Minimal API + EF Core（SQLite）。選型與為什麼不用 Native AOT 見 [`docs/adr/0001-tech-stack.md`](docs/adr/0001-tech-stack.md)。

```
src/
  TeamsGateway.AppHost          Aspire 編排（只在 dev，不部署）
  TeamsGateway.ServiceDefaults  Aspire 共用：OTel / health / resilience
  TeamsGateway.Service          gateway 本體（HTTP API + 背景工作）
```

## 開發

```
dotnet build                 # 建置
aspire run                   # AppHost：起 dashboard + gateway
dotnet run --project src/TeamsGateway.Service   # 只跑 gateway
```

## Bootstrap：綁定 Teams 帳號

執行期不需要瀏覽器，但第一次要把專用帳號的登入 cookie 匯進來（之後 gateway 自己換 token）。
cookie 用 `spike/http_refresh.py export` 從已登入的瀏覽器 profile 匯出。

`/admin` 端點要帶 `X-Admin-Token`（設定 `Gateway:AdminToken`，可用 env `Gateway__AdminToken`）：

```
curl -X PUT  -H "X-Admin-Token: $TOKEN" --data-binary @login_cookies.json  $BASE/admin/login-cookies
curl -X POST -H "X-Admin-Token: $TOKEN"                                     $BASE/admin/auth/refresh   # 換一次 token 驗證
curl        -H "X-Admin-Token: $TOKEN"                                     $BASE/admin/auth           # 看狀態
```

cookie 與 token 以 DataProtection 加密後存進 SQLite（`SecureValues` 表），key 在 `.data/keys/`。

建一把給訂閱者的 API key：

```
curl -X POST -H "X-Admin-Token: $TOKEN" -d '{"name":"client"}' $BASE/admin/api-keys   # 回的 apiKey 只出現這一次
```

## 對外 API（給 skills / 客戶端接）

帶 `Authorization: Bearer tgw_...`：

| 端點 | 說明 |
|---|---|
| `GET /v1/chats` | 列對話 |
| `GET /v1/chats/{id}/messages?limit=&cursor=` | 讀訊息（`cursor` 用上一頁回的 `nextCursor` 往更舊翻） |
| `POST /v1/chats/{id}/messages` `{"text":"..."}` | 發話 |
| `GET /v1/events?chats=<id>,<id>` | **SSE** 即時新訊息串流（像 Slack Socket Mode）；`chats` 省略 = 全部 |

即時推播由背景的 Trouter 連線收進來，寫進 `MessageEvents` 後 publish 給 SSE。自己發的訊息也會推回來（`from` 是自己）。

```
curl -N -H "Authorization: Bearer $KEY" "$BASE/v1/events?chats=19:...@thread.v2"
```
