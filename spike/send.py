# /// script
# requires-python = ">=3.12"
# dependencies = ["requests"]
# ///
"""Spike：建一個只有自己的測試群組（已存在就沿用），用 REST 發一則訊息再讀回來。

    uv run send.py "訊息內容"
"""

import json
import sys
import time

import requests
from teams_api import LOCAL_STATE, VIEW, claims, headers, skypetoken

CHATSVC = "https://teams.live.com/api/chatsvc/consumer/v1"
TOPIC = "teams-gateway test"

token = skypetoken()
H = headers(token) | {"content-type": "application/json"}
me = "8:" + claims(token)["skypeid"]

state = json.loads(LOCAL_STATE.read_text()) if LOCAL_STATE.exists() else {}
tid = state.get("test_thread")
if not tid:
    body = {"members": [{"id": me, "role": "Admin"}], "properties": {"threadType": "chat", "topic": TOPIC}}
    r = requests.post(f"{CHATSVC}/threads", headers=H, json=body, timeout=30)
    print("create thread:", r.status_code, r.headers.get("Location", "")[-80:], r.text[:200])
    r.raise_for_status()
    tid = r.headers["Location"].rstrip("/").split("/")[-1]
    state["test_thread"] = tid
    LOCAL_STATE.parent.mkdir(parents=True, exist_ok=True)
    LOCAL_STATE.write_text(json.dumps(state, indent=1))
print("test thread:", tid)

text = sys.argv[1] if len(sys.argv) > 1 else f"hello from teams-gateway spike {time.strftime('%H:%M:%S')}"
msg = {
    "content": text,
    "messagetype": "Text",
    "contenttype": "text",
    "clientmessageid": str(int(time.time() * 1000)),
    "imdisplayname": "teams-gateway",
}
r = requests.post(f"{CHATSVC}/users/ME/conversations/{tid}/messages", headers=H, json=msg, timeout=30)
print("send:", r.status_code, r.text[:200])

r = requests.get(f"{CHATSVC}/users/ME/conversations/{tid}/messages", headers=H, params={"view": VIEW, "pageSize": 5, "startTime": 0}, timeout=30)
for m in r.json().get("messages", [])[:3]:
    print("read back:", m.get("composetime", "")[:19], m.get("messagetype"), m.get("from", "")[-30:], repr(m.get("content", ""))[:80])
