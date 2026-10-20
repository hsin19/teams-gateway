# /// script
# requires-python = ">=3.12"
# dependencies = ["requests"]
# ///
"""Spike：列出對話（含群組）與成員數，找沒有其他人的對話。"""

import html
from teams_api import VIEW, get, hours_left, skypetoken

print("skypetoken 剩", hours_left(skypetoken()), "小時")
data = get("/conversations", view=VIEW, pageSize=500)
for c in sorted(data["conversations"], key=lambda c: int(c.get("version") or 0), reverse=True):
    tp = c.get("threadProperties") or {}
    title = html.unescape(tp.get("topic") or tp.get("displayname") or "")
    kind = tp.get("productThreadType") or ""
    print(f"{c['id'][:58]:58s} {kind:16s} members={tp.get('membercount') or '-':>4} {title[:40]}")
