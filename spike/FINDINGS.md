# Spike 發現

個人版 Teams（teams.live.com）沒有官方 API，以下都是從網頁版的流量與既有 teams skill 摸出來的非官方介面，隨時可能變。
腳本都用 PEP 723 單檔，`cd spike && uv run <script>.py`；抓到的流量與 token 只存在 `spike/.local/`（gitignored）。

## 登入狀態

- teams skill 的 Playwright profile（`~/.claude/teams/pw-profile`）複製一份，用 Chrome for Testing 156 加 `--use-mock-keychain` headless 開，直接是登入狀態。
  Playwright 在 macOS 用 mock keychain 加密 cookie，所以一定要帶這個參數才解得開。
- 複製時略過 Cache / Code Cache / Service Worker 的 CacheStorage 等快取，原本 1.2 GB 的 profile 剩不到 100 MB。

## 結論

**執行期完全不需要瀏覽器。** 瀏覽器只在第一次登入時用來拿 login.live.com 的登入 cookie；之後換 token、讀、發、收推播全部是 HTTP / WebSocket。

| 能力 | 做法 | 驗證腳本 |
|---|---|---|
| 換 skypetoken | 拿登入 cookie 做靜默授權（純 HTTP） | `http_refresh.py` |
| 讀對話 / 訊息 | chatsvc REST | `chats.py`、`members.py` |
| 發話、建群組 | chatsvc REST | `send.py` |
| 即時事件 | Trouter WebSocket + registrar | `trouter_client.py` |

## Token（純 HTTP，`http_refresh.py`）

只要有 login.live.com 的登入 cookie，就能照網頁版 MSAL 的流程自己換：

1. `GET https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize`
   `client_id=4b3e8f46-56d3-427f-b1e2-d239b2ea6bca`、`redirect_uri=https://teams.live.com/v2/authv2`、`response_type=code`、`response_mode=fragment`、
   `prompt=none`、PKCE（S256）、`scope=https://mtsvc.fl.teams.microsoft.com/teams.mt.readwrite openid profile offline_access`
   → 302 到 `login.live.com/oauth20_authorize.srf` → 302 到 `https://teams.live.com/v2/authv2#code=...`（不需要跑 JS）
2. `POST .../consumers/oauth2/v2.0/token`（`grant_type=authorization_code` + `code_verifier`，帶 `Origin: https://teams.live.com`）→ access + refresh token
3. 同一個 token 端點用 `grant_type=refresh_token` 換 `https://auth.fl.teams.microsoft.com/teams.auth.readwrite`
4. `POST https://teams.live.com/api/auth/v2.0/authz/consumer`，`Authorization: Bearer <第 3 步的 access token>`、
   `ms-teams-authz-type: ExplicitLogin`、`x-ms-client-type: web`、`claimschallengecapable: true`，body 空
   → `{"skypeToken":{"skypetoken","expiresIn":28799,"skypeid",...},"regionGtms":{...},...}`

壽命：

| 東西 | 壽命 | 備註 |
|---|---|---|
| skypetoken | 8 小時 | 到期前重跑上面四步就好 |
| SPA refresh token | 從登入起最多 24 小時，續期不會延長 | 所以不靠它，每次都從靜默授權開始 |
| login.live.com 登入 cookie | `__Host-MSAAUTHP` 約一年（這次看到 2027-11-15 到期） | 靜默授權時回來的 Set-Cookie 要存回去，讓它持續更新 |

登入 cookie 只要 `login.live.com` / `login.microsoftonline.com` 這兩個網域的；匯出方式見 `http_refresh.py export`
（開一次 profile 用 CDP `Storage.getCookies` 拿）。

網頁版另外也會用 MSAL 快取的 refresh token 直接換各服務的 token（`/9188040d-.../oauth2/v2.0/token`），但那顆 token 有 24 小時上限，不適合長期跑。

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
