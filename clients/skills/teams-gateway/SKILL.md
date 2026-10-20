---
name: teams-gateway
description: 讀 / 發個人版 Microsoft Teams（teams.live.com）訊息，透過 teams-gateway（一台掛著專用帳號的服務），不需要瀏覽器或本機登入。Use when 使用者要看某個 Teams 群的訊息、搜尋 / 列出 Teams 對話、或要發訊息到某個 Teams 群 / 對話。
allowed-tools: Bash(uv run *)
---

# teams-gateway skill

透過 teams-gateway 的 HTTP API 操作 Teams。gateway 用一個專用帳號掛著收發，所以這支 skill 不碰瀏覽器、不管登入 / token。

## 連線設定

需要 gateway 的 URL 與一把 API key（向管理者要，或 gateway `POST /admin/api-keys` 建）。擇一：

- 環境變數 `TEAMS_GATEWAY_URL`、`TEAMS_GATEWAY_API_KEY`
- `~/.config/teams-gateway/config.json`：`{"url": "https://...", "api_key": "tgw_..."}`

## 使用方式

```bash
uv run ${CLAUDE_SKILL_DIR}/scripts/teams.py list-chats [--query 關鍵字]
uv run ${CLAUDE_SKILL_DIR}/scripts/teams.py read  --chat <id|群名> [--limit 25]
uv run ${CLAUDE_SKILL_DIR}/scripts/teams.py send  --chat <id|群名> "訊息"
```

- `--chat` 接 thread id（`19:...@thread.v2`）或群名（對 topic 前綴 / 包含比對）。不確定群名就先 `list-chats`。
- 輸出都是 JSON，整理成給使用者看的形式再回報。

## 發話前確認

發訊息會以專用帳號的名義送進群組、且不可撤回。**發話前先把內容擬給使用者過目，確認後才送**（除非使用者明說直接發）。
