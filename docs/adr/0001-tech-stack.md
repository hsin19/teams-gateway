# ADR 0001：技術選型

## 決定

.NET 10 + Aspire + ASP.NET Core（Minimal API）+ EF Core（SQLite），走一般 JIT CoreCLR，**不用 Native AOT**。

## 背景與取捨

| 需求 | 選擇 | 原因 |
|---|---|---|
| DB，之後好轉正式 | EF Core + SQLite | provider 可抽換；DbContext 註冊集中一處，之後換 Postgres / SQL Server 只改一行，並接 Aspire 第一方 EF 整合 |
| 專案框架、好上雲 | .NET Aspire | ServiceDefaults（OTel / health / resilience）+ dashboard；AppHost 只在 dev，正式只部 gateway container，方便日後容器化部署流程（後來 relay 也以 container 部署，見 `DEPLOY.md`） |
| 省記憶體 | workstation GC（非 server、非並行），不是 Native AOT | 見下 |

### 為什麼不用 Native AOT

原本為了省記憶體想用 Native AOT，後來放棄：

1. **EF Core 的 AOT 到 EF 10 仍是 experimental、官方標明不可上 production**，動態查詢會在執行期直接丟 `Query wasn't precompiled`。Aspire 本身也不 AOT-friendly。
2. **省記憶體的動機已經消失**：原本怕吃幾百 MB 的元兇是登入 Teams 要開的 Chromium。spike（見 `spike/FINDINGS.md`）已確認**執行期完全不需要瀏覽器**——換 token、讀、發、收推播全是 HTTP / WebSocket，瀏覽器只在第一次登入拿 cookie 時用一次。

所以改用 JIT，並以 workstation GC（`ServerGarbageCollection=false`）把常駐記憶體壓到數十 MB 等級，足以滿足原始訴求，又不必犧牲 EF Core 與 Aspire。

## 後果

- 開發：`aspire run`（AppHost）起 dashboard + gateway；或單獨 `dotnet run` 跑 gateway。
- 之後轉正式 DB：改 DbContext 的 `UseSqlite` 為對應 provider，AppHost 加資料庫資源，client 換成 `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` 之類的第一方整合。
- AppHost 透傳的 `MessagePack` 有已知漏洞（NuGet audit NU1902/NU1903），因只在 dev 不部署，降為警告不擋建置（`Directory.Build.props`）。
