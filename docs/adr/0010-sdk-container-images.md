# ADR 0010：image 由 .NET SDK 出，不寫 Dockerfile

## 決定

- 兩個服務的 image 用 `dotnet publish -t:PublishContainer` 出。repository 名稱、環境變數寫在各自的 csproj，架構與 base image 寫在 `services/Directory.Build.props`。Dockerfile 與 `.dockerignore` 拿掉。
- 同一個 tag 含 `linux/amd64` 與 `linux/arm64`：`ContainerRuntimeIdentifiers` 讓 SDK 各 publish 一份，再合成一個 OCI image index。CI 在 x64 runner 上一次出兩份，不用 QEMU，也不用 arm64 runner。
- base image 是 `aspnet:10.0-noble-chiseled`（relay 也是，因為 ServiceDefaults 參照了 ASP.NET Core）：沒有 shell、沒有套件管理，以非 root（uid 1654）執行。
- release 照舊先在 runner 上 smoke check 再推，但只能跑 x64 那份。

## 背景

- 兩個服務都是純 .NET、走 JIT，image 裡只需要 publish 的輸出。Dockerfile 主要在處理 monorepo 的 restore（先 copy 各個 csproj 吃 layer cache）跟用 `.dockerignore` 擋 secret；SDK 直接拿 build 的結果組 image，這兩件都不用管。
- 多架構：Dockerfile 要靠 buildx，不是 QEMU 模擬（慢），就是各架構的 runner 各 build 一份再合併。JIT 編出來是 IL，分架構的只有 NuGet 帶來的 native 函式庫（SQLite），SDK 在任何一台都組得出兩份。順帶不再綁 arm64 runner（只有公開 repo 免費）。
- gateway 的 image 原本裝了 `curl`，給 compose 的 healthcheck 用。compose 已經移到部署端，CI 的 smoke check 從 host 打 `/health`，image 裡用不到。SDK 出的 image 不能 `RUN`，剛好也不需要。

## 代價與注意

- **非 root**：`/data` 要讓 uid 1654 寫得進去。Docker 新建的 volume、bind mount 自動建的目錄都屬於 root，沒處理的話 gateway 一啟動就因為開不了 SQLite 而失敗。relay 掛進來的 `appsettings.Local.json` 也要讓它讀得到。
- **PID 1**：entrypoint 直接是 `dotnet`，跑起來就是 container 的 PID 1，而 kernel 不會對 PID 1 套用訊號的預設動作。未處理的例外讓 runtime `abort()` 時，SIGABRT 沒有作用，process 卡在 100% CPU 不結束，restart policy 也不會動。原本的 Dockerfile 也是一樣，只是當時以 root 跑，比較不會一啟動就撞到。處理分兩層：
  - 兩個服務的 `Program.cs` 把整段包在 try/catch 裡，例外自己印到 stderr、回 exit code 1（`HostAbortedException` 是 `WebApplicationFactory` 用來中止 Main 的，放它過去）。啟動失敗、`Run` 丟出來的例外都走這條，`StartupFailureTests` 擋著。
  - 背景執行緒上的未處理例外不經過 Main，接不到，部署端還是要開 init（compose 的 `init: true`）。
  - 2026-10-24 用 `/data` 寫不進去的情況驗證過：包之前沒有 init 會一直卡著、`--init` 時以 134 結束；包之後沒有 init 也在 1 秒內以 1 結束。
- **沒有 shell**：`docker exec` 進不去。要查問題看 log 與 OTel，DB 從掛出來的 `/data` 或 Postgres 看。
- **SDK 的 bug**：SDK 10.0 出多架構 image 時，會把值裡有 `:` 的 label 與環境變數截斷（`org.opencontainers.image.source` 變成 `https`，GHCR 就連不到 repo）。`services/Directory.Build.targets` 補了一個重拆的 target。label 的部分 upstream 修了（dotnet/sdk#55414），等 SDK 帶了修正、環境變數也修好就拿掉。
- 之後需要 `RUN`、系統套件或非 .NET 的東西時，再回頭寫 Dockerfile。
