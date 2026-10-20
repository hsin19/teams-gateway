# Spike 發現

個人版 Teams（teams.live.com）沒有官方 API，以下都是從網頁版的流量與既有 teams skill 摸出來的非官方介面，隨時可能變。
腳本都用 PEP 723 單檔，`cd spike && uv run <script>.py`；抓到的流量與 token 只存在 `spike/.local/`（gitignored）。

## 登入狀態

- teams skill 的 Playwright profile（`~/.claude/teams/pw-profile`）複製一份，用 Chrome for Testing 156 加 `--use-mock-keychain` headless 開，直接是登入狀態。
  Playwright 在 macOS 用 mock keychain 加密 cookie，所以一定要帶這個參數才解得開。
- 複製時略過 Cache / Code Cache / Service Worker 的 CacheStorage 等快取，原本 1.2 GB 的 profile 剩不到 100 MB。

## Token

兩層 token：

| token | 怎麼來 | 壽命 | 用途 |
|---|---|---|---|
| MSA access / refresh token | MSAL（SPA）對 `login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/oauth2/v2.0/token` 用 `grant_type=refresh_token` | access 8h；refresh token 只剩當初登入起算的 ~24h（`refresh_token_expires_in` 每次遞減，不會因續期延長） | 換 skypetoken、其他服務 |
| skypetoken（JWT） | 待確認（網頁快取還有效時不會重換） | 8 小時（`exp - iat = 28800`） | chatsvc 的 `x-skypetoken` / `authentication` header |

- client_id `4b3e8f46-56d3-427f-b1e2-d239b2ea6bca`，redirect_uri `https://teams.live.com/v2/authv2`（SPA，要帶 Origin）。
- SPA refresh token 有 24 小時的硬上限，純 HTTP 續期最多撐一天；之後要靠 login.live.com 的登入 cookie 做 `prompt=none` 的靜默授權，這是下一步要驗的。

## 讀取

- 列對話：`GET /api/chatsvc/consumer/v1/users/ME/conversations?view=msnp24Equivalent|supportsMessageProperties|supportsExtendedHistory&pageSize=500`
  **只帶 token 時只回一對一對話**，要加網頁 client 的識別 header（`x-ms-client-type: cdlworker`、`ms-ic3-product: tfl` 等，見 `teams_api.headers`）才會回群組。
- 成員：`GET /api/chatsvc/consumer/v1/threads/{id}?view=msnp24Equivalent` → `members[]`。列對話時的 `threadProperties` 沒有成員數。
- 訊息：`GET .../users/ME/conversations/{id}/messages?view=...&pageSize=200&startTime=0`，翻頁用回傳的 `_metadata.backwardLink`。

## 發話（不需要瀏覽器）

- 建群組：`POST /api/chatsvc/consumer/v1/threads`，body `{"members":[{"id":"8:<skypeid>","role":"Admin"}],"properties":{"threadType":"chat","topic":"..."}}`
  → 201，新 thread id 在 `Location` header。
- 發訊息：`POST .../users/ME/conversations/{id}/messages`，body `{"content","messagetype":"Text","contenttype":"text","clientmessageid","imdisplayname"}` → 201。
  teams skill 現在發話走瀏覽器 DOM，改走 REST 就不需要 Chromium。
- 測試用群組 `teams-gateway test`（只有自己）的 id 存在 `spike/.local/state.json`；`48:notes` 在個人版不存在（404）。

## 即時事件（Trouter 推播，不需要瀏覽器）

`trouter_client.py` 只用 skypetoken 就能收到推播，發話後約 0.4 秒到。

1. **連線**：`wss://go.trouter.skype.com/v4/c?tc=<client 資訊 JSON>&timeout=40&epid=<自產 UUID>&cor_id=<UUID>&con_num=<ms>_0`
   協定是舊版 socket.io（0.9）的文字 frame：
   - `1::` 連上 → 送 `5:1+::{"name":"user.authenticate","args":[{"headers":{"X-Skypetoken":"..."}}]}`
   - 收 `5:N::{"name":"trouter.connected","args":[{"surl":"https://pub-...trouter.skype.com:3443/v4/f/<id>/", ...}]}`
   - 收 `trouter.message_loss` 要回 `trouter.processed_message_loss`（同 args）
   - 每 30～40 秒送 `5:N+::{"name":"ping"}`，收 `6:::N+["pong"]`
2. **註冊推播**：`POST https://edge.skype.com/registrar/prod/v2/registrations`（`X-Skypetoken`）
   `{"clientDescription":{"appId":"TeamsCDLWebWorker","templateKey":"TeamsCDLWebWorker_2.7","productContext":"TFL","platform":"chrome",...},"registrationId":"<epid>","transports":{"TROUTER":[{"path":"<surl>","ttl":3600}]}}` → 202。
   `ttl` 3600 秒，要每小時重新註冊。
3. **收事件**：`3:::{"id":N,"method":"POST","url":"/v4/f/<id>/messaging","headers":{...,"trouter-request":"..."},"body":"<JSON 字串>"}`
   body：`{"type":"EventMessage","resourceType":"NewMessage","resource":{"id","clientmessageid","content","messagetype","from","imdisplayname","to","threadtopic","composetime","conversationLink",...}}`
4. **回 ack**：`3:::{"id":N,"status":200,"headers":{"trouter-request":"<原樣帶回>","trouter-client":"{\"cd\":1}"},"body":""}`。

注意：
- 網頁版的訊息功能跑在 Web Worker 裡（`TeamsCDLWebWorker`），用 CDP 錄流量要 `Target.setAutoAttach` 連 Worker 才看得到 chatsvc 與這條 Trouter。
  主頁面另一條 `go.trouter.teams.microsoft.com`（`SkypeSpacesWeb`）收不到訊息推播。
- 自己發的訊息也會推回來，訂閱端要能分辨（`from` 是自己）。
- 推播漏掉時（斷線、`message_loss`）要用 chatsvc 的訊息 API 補，訊息 id 是遞增的時間戳，可以拿來當游標。
