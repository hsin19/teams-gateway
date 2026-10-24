# ADR 0011：認證與授權分開，角色決定能用哪些端點；接上 Cloudflare Access

## 決定

- **認證（是誰）**：ASP.NET Core 的 authentication，三個 scheme，各自只認自己的 header。預設 scheme 看請求帶了哪種 header
  轉給對應的那個（`Cf-Access-Jwt-Assertion` → `X-Admin-Token` → 其他都給 `ApiKey`），一個請求只有一種身分、只認證一次：

  | scheme | 看什麼 | 產生的身分 |
  |---|---|---|
  | `ApiKey` | `Authorization: Bearer tgw_…` 或 `X-Api-Key` | key 的 id、名稱，角色 `api` |
  | `AdminToken` | `X-Admin-Token` 等於 `Gateway:AdminToken`（沒設就關閉） | 角色 `admin` |
  | `CloudflareAccess` | `Cf-Access-Jwt-Assertion`（`Gateway:CloudflareAccess:Issuer` 沒設就不註冊） | `email`，不給角色 |

- **這個請求的使用者**：認證完由一層 middleware 填進 scoped 的 `CurrentUser`，授權 handler 與端點都注入它。
- **授權（能做什麼）**：端點只要求角色（`RequireAuthorization("admin")`、`RequireAuthorization("api")`），不管身分是哪個 scheme 來的。沒有身分 401，有身分但沒有那個角色 403。
- **角色從哪來**：scheme 直接給的（API key、admin token）就用；只有 email 的身分，在端點要求的角色它還沒有時，才問 `IRoleDirectory`（現在是 `Gateway:SuperAdmins` 的 email → `admin`）。這一步在 authorization handler 裡、經過 `CurrentUser`，同一個請求只問一次。
- `Gateway:SuperAdmins` 跟登入方式無關：Cloudflare Access（之後接其他 OIDC 也一樣）只負責確認是誰。
- Cloudflare Access 的 JWT 用 JwtBearer 驗：只讀 `Cf-Access-Jwt-Assertion`；驗簽章、issuer、audience、期限；公鑰從 `{Issuer}/cdn-cgi/access/certs` 抓，交給 JwtBearer 的 ConfigurationManager 快取（每小時更新，遇到不認得的 kid 提早重抓、最多每分鐘一次）。設了 `Issuer` 就必須設 `Audience`，缺了 gateway 起不來。
- `/admin` 不再只能在部署環境內部打：放在 Cloudflare Access 後面，用 super admin 的身分就能從外面用。admin token 保留給內部（bootstrap）用。
- 修正 README「`/admin` 不要對外」。

## 背景

- `/admin` 能建 API key、匯入登入 cookie，等於整個 Teams 帳號，原本只能在部署環境內部打。
- 只靠 Cloudflare Access 的 policy 擋（只放自己的 email）也能用，但授權就散在 Cloudflare 的設定裡：gateway 看不到是誰，Access 的 policy 被改寬了 gateway 也不知道。讓 Cloudflare 只負責認證、名單跟著 gateway 的設定走，換 IdP 也不用動授權。
- 角色不在 `IClaimsTransformation` 裡加：它對每個認證過的請求都跑，名單之後放 DB 的話，只需要 `api` 的請求也會多查一次。放在 authorization handler，只有端點要求、身分又還沒有那個角色時才查。
- 不驗 audience 的話，同一個 Zero Trust team 底下任何 Access app 簽出來的 JWT 都會被接受，所以 audience 必填。
- 原本 `/v1` 用 endpoint filter、`/mcp` 用 middleware、`/admin` 用另一個 filter，各驗各的；統一成 scheme + 角色後，加一種登入方式只是多一個 scheme。

## 代價與注意

- 一個請求只認一種身分：同時帶 JWT 和 API key 的，只看 JWT。本來就不該同時帶兩種。
- 認證在授權之前、對每個請求都跑：帶著 API key 打公開端點（例如 `/health`）也會查一次 key。
- 兩條 admin 路都沒設定時，`/admin` 回 401（以前是 503「已停用」）：沒有 scheme 能給出 admin 身分。
- 授權在 routing 之後：content type 不對的請求（例如 `/mcp` 送非 JSON）在挑 endpoint 時就回 415，輪不到 401。
- webhook 掛在建立它的 API key 底下，所以只有 API key 的身分能登記；其他拿到 `api` 的身分（目前沒有）登記會被拒。
- gateway 只驗 JWT 本身：除了經過 Cloudflare 之外不要有別的對外入口，不然拿到一張有效 JWT 的人可以繞過 Access 重放到它過期。
- 只認 Cloudflare Access 的格式（header 與 certs 路徑）。之後接其他 IdP，多一個 scheme（例如標準的 OIDC JwtBearer）就好，角色與名單不用動。
