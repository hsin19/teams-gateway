# ADR 0008：資料庫可在 SQLite / PostgreSQL 間切換

## 決定

- gateway 的 DB 用設定切換：`Gateway:Database:Provider` = `Sqlite`（預設）或 `Postgres`。Postgres 要給 `ConnectionStrings:gateway`，而且要一個 gateway 專用的資料庫。
- 本機開發預設 SQLite（`.data/gateway.db`），不用 Docker。AppHost 的 `appsettings.Local.json` 設 `Gateway__Database__Provider=Postgres` 時，改起一個 Postgres container（named volume），連線字串由 Aspire 帶進去。
- 正式環境打算用 Postgres：部署環境本來就有 Postgres，備份與監控可以跟其他服務共用一套。
- functional test 預設跑 SQLite；`GATEWAY_TEST_DB=postgres` 時照 eShop 的做法，用 Aspire 在測試裡起一個 Postgres container 跑同一組測試。單元測試維持 in-memory SQLite。
- 這次不導入 migration：兩個 provider 都沿用 `EnsureCreated` + 啟動時比對 schema 雜湊。SQLite 記在 `PRAGMA user_version`，Postgres 記在一張不在 EF model 裡的 `__GatewaySchema` 表。（後來導入了 migration，兩個 provider 各一套，見 ADR 0009。）
- 修正 ADR 0001 的「EF Core + SQLite」：provider 從寫死變成可切換。

## 背景

- 只看這個專案，SQLite 就夠：單一寫入者、一天幾百則訊息、一個檔案就是全部狀態。
- 換 Postgres 的理由在環境：部署環境本來就有 Postgres，共用的話只需要一套備份與監控；之後的歷史搜尋也有機會靠 `pg_trgm` 做中文子字串搜尋，不必另外架 Elasticsearch。
- 開發不想因此背 Docker，所以兩個都留、用設定切。程式本來就只在 `DataExtensions` 一處註冊 DbContext，切換的成本低。

## 兩個 provider 要守的事

- **事件序號 = commit 順序**：ADR 0006 的讀法是「讀游標之後的事件」，要求序號照 commit 順序遞增。SQLite 一次只有一個寫入者，天生成立；Postgres 的序號在 INSERT 時就分配，兩筆交易同時跑時，序號小的可能比較晚 commit，讀的人就會跳過它。現在靠 `MessageIngestor` 的寫入鎖讓整個行程只有一個寫入點，所以成立；寫 `PublishedEvents` 的地方不能繞過它，也不能跑多個 gateway 實例（本來就是單實例，見根目錄 README〈部署〉）。
- **時間一律 UTC**：Npgsql 只收 offset 0 的 `DateTimeOffset`，寫入前由 `UtcConverter` 轉成 UTC。SQLite 照舊存 Unix 毫秒。
- **Postgres 要專用的資料庫**：gateway 啟動時看到「有表卻沒有 migration 紀錄」會拒絕（ADR 0009），別的服務的表也算，所以不能跟別的服務共用同一個資料庫（同一個 server 沒關係）。
- **DataProtection key 還在檔案**（`Gateway:DataDir/keys`）：用 Postgres 時，登入 cookie 在 DB、解密用的 key 在 volume，備份和還原要兩邊一起。

## 之後

- migration：兩個 provider 各一套，見 ADR 0009。
