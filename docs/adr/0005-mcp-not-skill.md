# ADR 0005：agent 介面用 MCP，不用 skill

## 決定

gateway 原生開一個 MCP server（`/mcp`，Streamable HTTP），把讀 / 發開成 MCP tool。**不**另外出 Claude skill。

## 為什麼

- **這些操作是純 function，沒有需要 LLM 介入的流程。** skill 的價值在於「帶著 LLM 走一套方法／判斷」；list / read / send 沒有這種東西，連「用群名找對話」都在 gateway 端解析成 function。這種東西本來就該是 typed function，skill 的指引開銷幾乎是零。
- **MCP 是跨家標準。** 任何 MCP client（Claude Code、Claude Desktop、Cursor、其他 agent）都能接；不綁單一家。
- **掛在 core 最省。** 官方 `ModelContextProtocol.AspNetCore`（v2）在同一個 ASP.NET 服務上開 `/mcp`，tool 直接重用 `ChatsvcClient`——同一個服務、同一把 API key，同時 MCP + REST + SSE，不用多一支 script 維護。

## 分工

| 介面 | 給誰 | 性質 |
|---|---|---|
| **MCP `/mcp`** | agent（Claude Code / Desktop / Cursor…） | 請求／回應的 tool：list_chats / read_messages / send_message（後來加了 get_attachments，輸出改成給 LLM 讀的 log 式純文字，見 `clients/mcp/README.md`） |
| **REST `/v1` + SSE** | 程式、常駐訂閱（背景常駐的即時監控）、Python client | 含 SSE 長連線即時事件 |

MCP 不取代 SSE：即時長連線訂閱還是走 SSE + Python client。

## 認證

`/mcp` 跟 `/v1` 一樣要 API key（MCP client 以 `Authorization: Bearer tgw_...` header 帶）。gateway 用 middleware 重用 `ApiKeyService` 驗。
