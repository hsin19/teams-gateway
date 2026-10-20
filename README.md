# teams-gateway

給 CLI 與 app 用的個人版 Microsoft Teams（teams.live.com）包裝。個人版沒有 bot API、webhook 或官方 API，
teams-gateway 用一個專用帳號登入，對外提供：

- HTTP API：列群組、讀訊息、發話
- 事件串流：群組有新訊息就推給訂閱者（SSE / webhook），之後可以接 Elasticsearch

目標是之後掛在一台機器（或雲端）上，其他人要做 Teams 機器人只要打 HTTP。

## 狀態

- `spike/`：拋棄式的 Python 腳本，摸清非官方 API（登入、token、讀寫、即時事件）。結論在 [`spike/FINDINGS.md`](spike/FINDINGS.md)。
- 正式版預計用 C#（.NET 10、Native AOT）實作，平常只跑一個小行程；瀏覽器只在登入時才開。
