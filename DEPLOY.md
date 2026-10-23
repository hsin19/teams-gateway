# 部署（Docker / Swarm）

兩個服務:`gateway`（連 Teams、對外 `/v1`）+ `relay`（訂閱 gateway、轉進 Slack）。`tools/`（Aspire AppHost）是 dev-only,不部署。

## 狀態與限制

- **單副本**:gateway 一條 Trouter 連線 + 一個 SQLite writer;relay 一個 SSE consumer。兩者都 `replicas: 1`,不可水平擴。
- **持久化**:gateway 的 `/data`（cookie + DataProtection key + SQLite）掛 `gateway-data` volume,不然每次重啟要重匯 cookie。
- **schema**:目前不做 migration。程式的 DB model 改過時 gateway 會拒絕啟動並提示,要把 volume 裡的 `gateway.db` 刪掉重建、重新 bootstrap。正式長期部署前再補 migration。
- **egress**:gateway 要連得到 `login.live.com` / `teams.live.com` / chatsvc / `*.trouter.skype.com`（含 WebSocket）。一般網路沒問題;受限網段要先開白名單。

## 安全注意

- **明文 HTTP**:5199 上的 admin token、API key、訊息內容都是明文。只開在信任的內網;要跨網段就前面擋一層 TLS reverse proxy,`/admin` 最好只讓管理機連得到。
- **`/data` 等於帳號本身**:SQLite 裡的登入 cookie / webhook secret 是用 DataProtection 加密的,但解密用的 key（`/data/keys`）就放在同一個 volume、沒再加密——拿到整個 volume 就拿得到 Teams 帳號。volume 的權限、備份要當密碼看待。
- **webhook 目標**:預設不能打 loopback / 私有網段 / link-local（防止拿 gateway 當跳板打內網或雲端 metadata）。訂閱者跟 gateway 在同機、同網段或同一個 docker 網路時,設 `Gateway__WebhookAllowPrivateNetworks=true`（link-local 還是不行）。
- **health**:`/health`（含 DB）、`/alive` 不需要認證,只回 `Healthy` / `Unhealthy`,不帶細節。

## 本機（docker compose）

```bash
export GATEWAY_ADMIN_TOKEN=$(openssl rand -hex 32)
docker compose up -d --build gateway        # 先只起 gateway
```

一次性 bootstrap（寫進 gateway-data volume,之後重啟免重做）:

```bash
BASE=http://localhost:5199
# 1) 匯入登入 cookie（從已登入的瀏覽器 profile 用 spike/http_refresh.py export 出來）
curl -X PUT  -H "X-Admin-Token: $GATEWAY_ADMIN_TOKEN" --data-binary @login_cookies.json $BASE/admin/login-cookies
curl -X POST -H "X-Admin-Token: $GATEWAY_ADMIN_TOKEN" $BASE/admin/auth/refresh      # 驗證換得到 token
# 2) 訂閱要轉的 Teams 群
curl -X PUT  -H "X-Admin-Token: $GATEWAY_ADMIN_TOKEN" -H "Content-Type: application/json" -d '{"chat":"General Chat"}' $BASE/admin/subscriptions
# 3) 給 relay 建一把 API key（回的 apiKey 只出現一次）
curl -X POST -H "X-Admin-Token: $GATEWAY_ADMIN_TOKEN" -H "Content-Type: application/json" -d '{"name":"slack-relay"}' $BASE/admin/api-keys
```

Routes（Teams 對話→Slack 頻道）放 `services/TeamsGateway.SlackRelay/appsettings.Local.json`（gitignored,compose 掛檔進 relay）。然後起 relay:

```bash
export RELAY_GATEWAY_APIKEY=tgw_…      # 上一步建的
export RELAY_SLACK_BOTTOKEN=xoxb-…
docker compose up -d relay
```

## Swarm 部署

`docker stack deploy` **忽略 `build:`**,要先 build + push 到 registry:

```bash
export REGISTRY=registry.gitlab.example.com/<group>/teams-gateway TAG=$(git rev-parse --short HEAD)
docker compose build && docker compose push
# secret 用當下 shell env 代入；Routes 建議改用 swarm config 掛 /app/appsettings.Local.json
GATEWAY_ADMIN_TOKEN=… RELAY_GATEWAY_APIKEY=… RELAY_SLACK_BOTTOKEN=… \
  docker stack deploy -c docker-compose.yml teams
```

`gateway-data` 是 node 本機的 volume:task 被排到別台 node 就是一個空的 volume（cookie / key / DB 都不在,要重新 bootstrap）。用 `deploy.placement.constraints` 把 gateway 釘在固定 node（例如 `node.labels.teams-gateway == true`,先 `docker node update --label-add teams-gateway=true <node>`）。

正式上建議把 secret 換成 docker swarm `secrets:`、Routes 換成 `configs:`（bind mount 綁在特定 node,Swarm 下不穩）。GitLab CI 負責 build/test + push image,deploy 這段在 node 上跑。
