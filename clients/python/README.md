# teams-gateway-client（Python）

個人版 Teams gateway 的 Python client：REST（列對話 / 讀 / 發）+ SSE 事件串流（斷線自動重連）。

```python
from teams_gateway import TeamsGatewayClient

with TeamsGatewayClient("http://localhost:5199", api_key="tgw_...") as gw:
    for chat in gw.list_chats():
        print(chat.topic, chat.id)

    msgs, cursor = gw.read_messages(chat_id, limit=30)   # cursor 往更舊翻頁
    gw.send(chat_id, "hello")

    for msg in gw.events(chats=[chat_id]):                # SSE，像 Slack Socket Mode
        print(msg.from_name, msg.content)
```

API key 用 gateway 的 `POST /admin/api-keys` 建（見 gateway README）。

## 安裝

```
uv pip install ./clients/python
# 或 PEP 723 / pyproject：teams-gateway-client @ <repo>#subdirectory=clients/python
```
