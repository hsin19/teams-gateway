# /// script
# requires-python = ">=3.11"
# dependencies = ["httpx>=0.27"]
# ///
"""透過 teams-gateway 讀 / 發個人版 Teams 訊息。輸出 JSON。

連線設定（擇一）：
  - 環境變數 TEAMS_GATEWAY_URL / TEAMS_GATEWAY_API_KEY
  - ~/.config/teams-gateway/config.json : {"url": "...", "api_key": "tgw_..."}

用法：
  uv run teams.py list-chats [--query 關鍵字]
  uv run teams.py read  --chat <id|群名> [--limit 25]
  uv run teams.py send  --chat <id|群名> "訊息"
"""

import argparse
import json
import os
import sys
from pathlib import Path

import httpx

CONFIG = Path.home() / ".config" / "teams-gateway" / "config.json"


def load_conn() -> tuple[str, str]:
    url = os.environ.get("TEAMS_GATEWAY_URL")
    key = os.environ.get("TEAMS_GATEWAY_API_KEY")
    if (not url or not key) and CONFIG.exists():
        cfg = json.loads(CONFIG.read_text())
        url = url or cfg.get("url")
        key = key or cfg.get("api_key")
    if not url or not key:
        sys.exit(f"[SETUP] 缺連線設定。設 TEAMS_GATEWAY_URL / TEAMS_GATEWAY_API_KEY，或寫 {CONFIG}")
    return url.rstrip("/"), key


def client() -> httpx.Client:
    url, key = load_conn()
    return httpx.Client(base_url=url, headers={"Authorization": f"Bearer {key}"}, timeout=30)


def resolve_chat(http: httpx.Client, spec: str) -> str:
    """接受 thread id（19:...）或群名（對 topic 前綴 / 包含比對，不分大小寫）。"""
    if spec.startswith("19:") or "@thread" in spec:
        return spec
    chats = http.get("/v1/chats").raise_for_status().json()
    low = spec.lower()
    hit = next((c for c in chats if (c.get("topic") or "").lower().startswith(low)), None) \
        or next((c for c in chats if low in (c.get("topic") or "").lower()), None)
    if not hit:
        names = [c["topic"] for c in chats if c.get("topic")][:40]
        sys.exit(f"[ERROR] 找不到群 '{spec}'。可選（前 40）：\n  " + "\n  ".join(names))
    return hit["id"]


def cmd_list(http: httpx.Client, args) -> None:
    chats = http.get("/v1/chats").raise_for_status().json()
    if args.query:
        low = args.query.lower()
        chats = [c for c in chats if low in (c.get("topic") or "").lower()]
    out = [{"id": c["id"], "topic": c.get("topic"), "type": c.get("threadType")} for c in chats]
    print(json.dumps(out, ensure_ascii=False, indent=2))


def cmd_read(http: httpx.Client, args) -> None:
    chat_id = resolve_chat(http, args.chat)
    page = http.get(f"/v1/chats/{chat_id}/messages", params={"limit": args.limit}).raise_for_status().json()
    out = [{"time": m.get("time"), "from": m.get("fromName"), "type": m.get("type"), "content": m.get("content")}
           for m in page["messages"]]
    print(json.dumps(out, ensure_ascii=False, indent=2))


def cmd_send(http: httpx.Client, args) -> None:
    chat_id = resolve_chat(http, args.chat)
    r = http.post(f"/v1/chats/{chat_id}/messages", json={"text": args.text}).raise_for_status().json()
    print(json.dumps({"sent": True, "chat": chat_id, "clientMessageId": r.get("clientMessageId")}, ensure_ascii=False))


def main() -> None:
    parser = argparse.ArgumentParser(prog="teams")
    sub = parser.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("list-chats"); p.add_argument("--query")
    p = sub.add_parser("read"); p.add_argument("--chat", required=True); p.add_argument("--limit", type=int, default=25)
    p = sub.add_parser("send"); p.add_argument("--chat", required=True); p.add_argument("text")
    args = parser.parse_args()

    with client() as http:
        try:
            {"list-chats": cmd_list, "read": cmd_read, "send": cmd_send}[args.cmd](http, args)
        except httpx.HTTPStatusError as e:
            sys.exit(f"[ERROR] gateway HTTP {e.response.status_code}: {e.response.text[:200]}")


if __name__ == "__main__":
    main()
