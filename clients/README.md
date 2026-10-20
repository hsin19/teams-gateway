# clients

給別人接 teams-gateway 的 client：

- `python/`：Python client（已做）。包好 API key、`/v1` REST、`/v1/events` SSE（自動重連），給**程式 / 常駐訂閱**（背景常駐的即時監控）直接 import 用。
- `mcp/`：給 **agent** 用的接法說明。gateway 原生在 `/mcp` 開 MCP server（讀/發是純 function，不需要 skill 的流程指引，見 `docs/adr/0005`），任何 MCP client（Claude Code / Desktop / Cursor）帶 API key 就能接。

對外契約以 gateway 的 `/v1`（見根 README）與 `shared/TeamsGateway.Contracts` 為準。
