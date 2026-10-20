# clients（規劃中）

給別人接 teams-gateway 的 client：

- `python/`：Python client（已做）。包好 API key、`/v1` REST、`/v1/events` SSE（自動重連），讓自訂 Python 程式直接 import 用。
- `skills/`：把能力包成 Claude skill（已做 `teams-gateway` 讀/發），其他人用 `npx skills` 或 plugin 安裝。取代傳統 CLI——大家都用 agent 了，裝個 skill 就會用，也不用再管 workspace teams skill 自己的瀏覽器 / token。

對外契約以 gateway 的 `/v1`（見根 README）與 `shared/TeamsGateway.Contracts` 為準。
