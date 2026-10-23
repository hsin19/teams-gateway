# ADR 0002：專案內部結構（參考 eShop）

## 決定

借 [dotnet/eShop](https://github.com/dotnet/eShop) 單一服務（如 `Catalog.API`）的內部切法，但不照它拆多服務——teams-gateway 是一個服務。

| eShop（Catalog.API） | teams-gateway | 內容 |
|---|---|---|
| `Apis/` | `Apis/` | Minimal API 端點模組，每個 `Map<Feature>(this IEndpointRouteBuilder)` |
| `Services/` | `Teams/` | 對 Teams 的 protocol client（auth / chatsvc / trouter） |
| `Infrastructure/` | `Data/` | EF Core DbContext、schema 建立（部署前不做 migration；後來導入了，見 ADR 0009）、health check |
| `Model/` | `Model/` | DTO（對外契約）；EF 實體留在 `Data/`（實作時沒開這個資料夾：對外 DTO 放 `packages/TeamsGateway.Contracts`，admin 專用的在 `Apis/AdminDtos.cs`） |
| `IntegrationEvents/` | `Events/` | 內部事件（收到新訊息）+ 分派到 sink |
| `Extensions/`（`AddApplicationServices`） | `Extensions.cs` | 一個聚合器把所有服務註冊起來，`Program.cs` 只呼叫它 |
| `CatalogOptions.cs` | `GatewayOptions.cs` | options pattern |

## 與 eShop 的差異（刻意）

- **事件匯流排**：eShop 用 RabbitMQ 做跨服務 integration event；我們是單一行程，用 `System.Threading.Channels` 的行程內 pub/sub 把「收到新訊息」分派給 sink（SSE / webhook / 之後的 ES）。要拆多服務或多實例時再換成真的 broker。（事件出入口抽象化、傳輸 infra 可替換的修正見 ADR 0006。）
- **API 版本**：eShop 用 `Asp.Versioning` + `NewVersionedApi`；我們先用 `/v1` route group，省一個相依，之後要多版本再換。
- **資料庫**：見 ADR 0001，SQLite + EF，單一實例。

## 對外介面（給 skills / 客戶端接）

- `/v1/*`：REST，帶 API key（`Authorization: Bearer tgw_...`）。列對話、讀訊息、發話。
- `/v1/events`：SSE 事件串流，訂閱者用 `chats=` 篩選、`Last-Event-ID` 補收。
- `/admin/*`：管理（匯入 cookie、建 API key、建 webhook 訂閱），帶 `X-Admin-Token`。（實作時 webhook 改由 API key 持有者在 `/v1/webhooks` 自己建、只看得到自己的。）
