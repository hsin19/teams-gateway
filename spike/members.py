# /// script
# requires-python = ">=3.12"
# dependencies = ["requests"]
# ///
"""Spike：查指定對話的成員與最近訊息時間。"""

import sys
import requests
from teams_api import VIEW, headers, skypetoken

THREADS = "https://teams.live.com/api/chatsvc/consumer/v1/threads"
CONVS = "https://teams.live.com/api/chatsvc/consumer/v1/users/ME/conversations"
H = headers(skypetoken())
for tid in sys.argv[1:]:
    r = requests.get(f"{THREADS}/{tid}", headers=H, params={"view": "msnp24Equivalent"}, timeout=30)
    members = [m.get("id") for m in r.json().get("members", [])] if r.ok else f"HTTP {r.status_code}"
    m = requests.get(f"{CONVS}/{tid}/messages", headers=H, params={"view": VIEW, "pageSize": 3, "startTime": 0}, timeout=30)
    msgs = m.json().get("messages", []) if m.ok else []
    last = [(x.get("composetime", "")[:19], x.get("messagetype")) for x in msgs[:3]]
    print(tid, "| members:", members if isinstance(members, str) else f"{len(members)} {members[:5]}", "| messages HTTP", m.status_code, last)
