# CI/CD Workflows

## 架構

```
check.yml    push 到 main · workflow_call · workflow_dispatch
             → build · unit test · functional test（SQLite、Postgres）· shellcheck
pr.yml       pull_request → check.yml · dependency review → auto-merge（Dependabot）
release.yml  v* tag       → check.yml → 每個 image 在 x64 + arm64 runner 上 smoke check → 推 amd64 + arm64 到 GHCR
```

`check.yml` 是「這棵樹過了沒」的唯一定義。另外兩個 workflow 都呼叫它，PR 和 release 用同一套標準，清單不會兩邊各長各的。
要加檢查就加在這裡，不加在別處。`main` 沒有自己的部署 workflow，所以 `check.yml` 也在 push 到 `main` 時跑：
直接 push 或 Dependabot merge 進來的，靠這次跑來守住跟 PR 一樣的標準。

每組測試各自一個 step，條件是 `!cancelled()` 而且 build 成功：一次跑完就看得到所有失敗，不會停在第一個；
編譯錯誤也不會讓後面整排 step 一起紅、把真正的原因埋掉。

Postgres 版的 functional test 經 Aspire 起 container（`GATEWAY_TEST_DB=postgres`）。runner 有 Docker，
DCP 從 NuGet 來（測試專案設了 `AspireUseCliBundle=false`），所以不用裝 Aspire CLI。

E2E 測試（`tests/TeamsGateway.Gateway.E2E`）刻意不跑：它會登入真的 Teams 帳號、真的發訊息。

workflow 用觸發它的事件命名，不叫 `ci.yml`：它們跑的是同一套檢查，CI 不是分辨它們的地方。

## Release

推一個 tag：`git tag v1.2.3 && git push origin v1.2.3`。image 是 `ghcr.io/<owner>/teams-gateway` 與
`ghcr.io/<owner>/teams-slack-relay`，tag 是 `1.2.3`、`1.2` 與 `sha-<短 sha>`。

- 沒有 Dockerfile：`dotnet publish -t:PublishContainer` 照專案設定（`services/Directory.Build.props` 與各自的 csproj，
  見 ADR 0010）出 image、直接推到 registry，workflow 裡沒有 Docker 的 action。
- 每個 tag 是含 `linux/amd64` 與 `linux/arm64` 的 image index，兩個架構都在同一台 x64 runner 上出、不用模擬：
  程式是 IL，分架構的只有 NuGet 帶來的 native 函式庫。SDK 會先把各架構推成 `<tag>-linux-x64` /
  `<tag>-linux-arm64`，所以 GHCR 上也看得到這些 tag；部署用沒有後綴的那個。
- 推之前，每個 image 都在兩個架構的原生 runner（`ubuntu-24.04`、`ubuntu-24.04-arm`）上 build 進本機 Docker、
  跑 smoke check：gateway 在沒有任何 secret 的情況下要回 `/health` Healthy，relay 要回 `/alive` Healthy
  （它的 `/health` 要有 Slack 頻道、連著 gateway，在這裡一定是 Unhealthy）。gateway 拿到一個屬於 uid 1654
  的 `/data`，跟部署端要準備的一樣。推的時候是同一個 commit 重新 build 的，不是 smoke 跑過的那幾份。
- GHCR 新推的 package 預設是 private。到 package 設定改成 public 一次，或改成授權給部署端的 repo 讀取。
  `org.opencontainers.image.source` label 會把 package 連到這個 repo。

## Dependency review

**Dependency graph** 打開之前，`pr.yml` 的 `dependency-review` job 會讓每個 PR 都失敗，訊息是
"Dependency review is not supported on this repository"。打開 Dependabot alerts（Settings → Advanced Security）
會一併打開它。這個 job 也是 `auto-merge` 的前提，graph 沒開的話 Dependabot 的 PR 都不會 merge。

## Dependabot auto-merge

`pr.yml` 用 `secrets.AUTOMERGE_TOKEN` squash-merge 綠燈的 Dependabot PR。用專用的 token 而不是預設的
`GITHUB_TOKEN`：`GITHUB_TOKEN` 做的 push 不會觸發別的 workflow，merge 之後的 `main` 就不會跑 `check.yml`。設定方式：

1. 建一把只給這個 repo 的 fine-grained personal access token，**Contents** 與 **Pull requests** 設成 read and write。
2. 加成 **Dependabot** secret，名稱 `AUTOMERGE_TOKEN`（Settings → Secrets and variables → Dependabot）。
   Dependabot 的 PR 讀的是 Dependabot 的 secret，不是 Actions 的。

merge 之前，job 會確認 check 跑完之後 `main` 沒有動過。動過的話改請 Dependabot rebase，rebase 後的 PR 會再過一次檢查。

`release.yml` 用到的 action 在 `check.yml` 也都有用到，所以 PR 綠燈就涵蓋了它們的升級。要維持這樣：
只在打 tag 時才用到的 action，第一次跑就是真的 release，那它就要有自己的 Dependabot group、不能進 auto-merge。

base image 沒有固定版本，也不需要 Dependabot：`noble-chiseled` 是浮動 tag，每次 release 都會拿到最新修補過的 runtime。
