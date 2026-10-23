# ADR 0009：導入 EF migration，SQLite / Postgres 各一套

## 決定

- schema 改用 EF Core migration 管理，gateway 啟動時 `MigrateAsync()` 自動套用還沒跑過的。新的 DB 照全部 migration 建，舊的原地升級，登入 cookie 與 API key 都留著，不用再刪 DB、重新 bootstrap。
- 兩個 provider 各一套（EF 的 migration 綁 provider）：`SqliteGatewayDbContext`、`PostgresGatewayDbContext` 繼承同一個 `GatewayDbContext`，migration 分別在 `Data/Migrations/Sqlite`、`Data/Migrations/Postgres`。程式其他地方照樣用 `IDbContextFactory<GatewayDbContext>`。
- 改了 model 就跑 `tools/db-migration.sh add <名稱>`，一次產生兩套，產生的檔案一起 commit。`dotnet-ef` 是 repo 的本地工具（`dotnet-tools.json`，版本跟 EF Core 一致）。
- `MigrationTests` 用 `HasPendingModelChanges()` 檢查兩套都跟上 model，忘了產生就失敗；functional test 的 SQLite 版與 Postgres 版也都實際跑過 migration。
- 取代 ADR 0008「這次不導入 migration」那一條：`EnsureCreated` 與啟動時比對 schema 雜湊（`PRAGMA user_version` / `__GatewaySchema`）都拿掉了。

## 背景

- 之前刻意不做 migration（commit `aec63a4`），因為還沒有要長期跑的部署，改 model 就重建 DB。實際用下來，重建的代價是登入 cookie 跟著消失，要從瀏覽器重新匯出、重新建 API key、重新訂閱；換 schema 的那兩次都是手動把 cookie 和 key 搬到新 DB。
- 正式環境（Postgres）快要上線，上線後就不能再靠重建。正式環境上線前導入最便宜：那時的 DB 一定是全新的，基線最單純。
- 只替 Postgres 做也可以，但本機的 SQLite 改 schema 就還是要重建，等於留著最痛的那一段。migration 是工具產生的，多一套的成本只是多跑一次指令（包在腳本裡）。

## 接上舊的 DB

導入之前用 `EnsureCreated` 建的 DB 有表卻沒有 `__EFMigrationsHistory`，直接 `Migrate` 會撞「表已經存在」，所以 gateway 啟動時遇到「有表、沒有 migration 紀錄」會停下來說明，不會亂套。Postgres 跟別的服務共用同一個資料庫時也是這個狀況，要給 gateway 專用的。

schema 跟基線 `InitialCreate` 一樣的話（例如導入當下的本機 SQLite），補一筆紀錄就接上，不用重建：

```
dotnet tool run dotnet-ef migrations script --project services/TeamsGateway.Gateway --context SqliteGatewayDbContext   # 看基線的 SQL
sqlite3 .data/gateway.db 'CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
  INSERT INTO "__EFMigrationsHistory" VALUES ('"'"'<InitialCreate 的 MigrationId>'"'"', '"'"'10.0.13'"'"');'
```

補之前先比對 schema：用基線 SQL 建一個空的 SQLite，跟舊 DB 逐張比 `sqlite_master`（排除 `__EF*`）。2026-10-23 本機的 DB 就是這樣接上的（13 個表與索引完全一致）。

## 換版的習慣

- 換版前先備份（Postgres：`pg_dump`，放進部署流程，在 `compose up` 之前跑）。
- migration 盡量只加東西：新表、可為 null 或有預設值的新欄位。新版有問題要退回舊版程式時，舊程式還能用同一個 DB。要刪欄位或改型別時分兩次上線：先讓程式不再用它，下一版再刪。
- 產生出來的 migration 要看一下：SQLite 不支援的 ALTER（改型別、刪欄位）EF 會改用「建新表、搬資料、換掉」的方式做，資料量大時要留意。
