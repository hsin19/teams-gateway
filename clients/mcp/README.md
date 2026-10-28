# MCP

gateway 原生在 `/mcp`（Streamable HTTP）開 MCP server，tool：`list_chats`、`read_messages`、`get_attachments`、`send_message`。
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

## tool

`chat` 參數給 thread id 或群名都可以；群名對到不只一個會報錯並列出候選。

| tool | 做什麼 |
|---|---|
| `list_chats` | 列對話，一行一個：`<thread id> \| <topic> \| last <時間>`，最近有動靜的在前，每次最多 `limit` 個（1–200，預設 30）；有下一頁時最後一行會給 `offset`，傳回 `offset` 參數繼續。`query` 篩群名 |
| `read_messages` | 讀一段時間的訊息，log 格式（見下）。`since` / `until`（前閉後開）、`limit`（1–200，預設 50，留最新的） |
| `get_attachments` | 開一則訊息的圖片與附件，訊息 id 取自 `[image #<id>]` / `[file: … #<id>]`。圖片（內嵌 + 附加）回 image content，一次最多 10 張；其他檔案的下載**還沒做**，只列檔名，只有檔案時回錯誤 |
| `send_message` | 以 gateway 綁定的帳號發一則文字訊息，不可撤回 |

`read_messages` 的輸出（給 LLM 讀，不是穩定的契約；程式要資料走 `/v1`）：

```
# 19:example@thread.v2 | 4 messages | times in UTC+08:00
2026-01-02 06:58:12 阿姨 (edited): [re 大舅: "明天爬山六點公園門口集合"] 收到！
  我會帶水果過去
2026-01-02 07:30:00 阿嬤: [2 images #1767310200000] 早安 🌸 平安喜樂
2026-01-02 07:45:10 小明: [re 阿嬤: "早安 🌸 平安喜樂"] 阿嬤早安
2026-01-02 08:02:33 阿姨: [file: 滷肉飯食譜.pdf #1767312153000]
# more: until=2026-01-02T06:58:12.120+08:00
```

- 照發話時間由舊到新；多行內容的後續行縮排兩格，所以新的一則一定從時間開頭。引用只留前 60 字。
- 範圍內還有更舊的沒回時才有最後那行 `# more:`，帶它的 `until` 再呼叫就往回翻。
- 時間照 `timezone` 參數或 gateway 的 `Gateway:McpTimeZone`（預設 UTC）。給 UTC offset（`+08:00`）；IANA 名稱（`Asia/Taipei`）要主機有時區資料，部署用的 chiseled image 沒有，所以 tool 說明只教 offset；`since` / `until` 沒帶 offset 的也當成這個時區。
