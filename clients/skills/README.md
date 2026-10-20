# skills

把 teams-gateway 的能力包成 Claude skill，讓 agent 直接用；其他人用 `npx skills` 或 Claude plugin 安裝。

- `teams-gateway/`：讀 / 發 Teams 訊息。script 自帶相依（PEP 723），只需要 gateway URL + API key，不碰瀏覽器。

## 安裝（給別人）

```bash
npx skills add <repo>/clients/skills/teams-gateway      # 或 Claude plugin
```

安裝後設 `TEAMS_GATEWAY_URL` / `TEAMS_GATEWAY_API_KEY`（或 `~/.config/teams-gateway/config.json`）即可。

> 設計理念：大家都用 agent 做事，與其做傳統 CLI，不如包成 skill——一個 SKILL.md + 一支 `uv run` script，裝了 agent 就會用。
