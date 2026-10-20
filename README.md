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
