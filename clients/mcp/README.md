# MCP

gateway 原生在 `/mcp`（Streamable HTTP）開 MCP server，tool：`list_chats`、`read_messages`、`send_message`。
任何 MCP client 都能接，帶 API key 即可。為什麼用 MCP 不用 skill 見 `docs/adr/0005`。

API key 用 gateway 的 `POST /admin/api-keys` 建（見 gateway README）。

## 接法

Claude Code：

```bash
claude mcp add --transport http teams-gateway http://127.0.0.1:5199/mcp \
  --header "Authorization: Bearer tgw_..."
```

Claude Desktop / Cursor（MCP 設定檔）：

```json
{
  "mcpServers": {
    "teams-gateway": {
      "type": "http",
      "url": "http://127.0.0.1:5199/mcp",
      "headers": { "Authorization": "Bearer tgw_..." }
    }
  }
}
```

接上後 agent 就有 `list_chats` / `read_messages` / `send_message` 三個 tool。`chat` 參數給 thread id 或群名都可以。
