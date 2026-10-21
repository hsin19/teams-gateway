# 部署（Docker / Swarm）

兩個服務:`gateway`（連 Teams、對外 `/v1`）+ `relay`（訂閱 gateway、轉進 Slack）。`tools/`（Aspire AppHost）是 dev-only,不部署。

## 狀態與限制

- **單副本**:gateway 一條 Trouter 連線 + 一個 SQLite writer;relay 一個 SSE consumer。兩者都 `replicas: 1`,不可水平擴。
- **持久化**:gateway 的 `/data`（cookie + DataProtection key + SQLite）掛 `gateway-data` volume,不然每次重啟要重匯 cookie。
- **egress**:gateway 要連得到 `login.live.com` / `teams.live.com` / chatsvc / `*.trouter.skype.com`（含 WebSocket）。一般網路沒問題;受限網段要先開白名單。

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
curl -X PUT  -H "X-Admin-Token: $GATEWAY_ADMIN_TOKEN" -d '{"chat":"General Chat"}' $BASE/admin/subscriptions
# 3) 給 relay 建一把 API key（回的 apiKey 只出現一次）
curl -X POST -H "X-Admin-Token: $GATEWAY_ADMIN_TOKEN" -d '{"name":"slack-relay"}' $BASE/admin/api-keys
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

正式上建議把 secret 換成 docker swarm `secrets:`、Routes 換成 `configs:`（bind mount 綁在特定 node,Swarm 下不穩）。GitLab CI 負責 build/test + push image,deploy 這段在 node 上跑。
