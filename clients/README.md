# clients（規劃中）

給別人接 teams-gateway 的 client：

- `python/`：Python client。包好 API key、`/v1` REST、`/v1/events` SSE（自動重連、補收），讓自訂 Python 程式直接 import 用。
- `cli/`：命令列工具（`teams chats` / `read` / `send` / `subscribe`），底下打 `/v1`，給人、shell script、Claude skill 用；可取代目前 workspace 的 teams skill 自己管瀏覽器 / token 的部分。

對外契約以 gateway 的 `/v1`（見根 README）與 `shared/TeamsGateway.Contracts` 為準。
