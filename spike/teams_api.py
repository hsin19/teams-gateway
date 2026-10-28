"""Spike 共用：chatsvc（teams.live.com 個人版）REST 呼叫。token 借用 teams skill 快取的 skypetoken，沒有就用 http_refresh.py 換的。"""

import base64
import json
import time
from pathlib import Path

import requests

BASE = "https://teams.live.com/api/chatsvc/consumer/v1/users/ME"
VIEW = "msnp24Equivalent|supportsMessageProperties|supportsExtendedHistory"
LOCAL = Path(__file__).resolve().parent / ".local"


def skypetoken() -> str:
    for path in (Path.home() / ".claude/teams/session.json", LOCAL / "http_skypetoken.json"):
        if path.exists():
            return json.loads(path.read_text())["token"]
    raise SystemExit("沒有 skypetoken：先跑 uv run http_refresh.py")


def claims(token: str) -> dict:
    return json.loads(base64.urlsafe_b64decode(token.split(".")[1] + "==="))


def headers(token: str) -> dict:
    # 只帶 token 時 chatsvc 只回一對一對話；要帶網頁 client 的識別 header 才回群組
    return {
        "x-skypetoken": token,
        "authentication": f"skypetoken={token}",
        "x-ms-client-type": "cdlworker",
        "x-ms-client-version": "1415/26082021043",
        "x-ms-user-type": "real-user",
        "ms-ic3-product": "tfl",
        "ms-ic3-additional-product": "Sfl",
        "accept": "application/json",
        "user-agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36",
    }


def get(path: str, **params) -> dict:
    r = requests.get(f"{BASE}{path}", headers=headers(skypetoken()), params=params, timeout=30)
    r.raise_for_status()
    return r.json()


def hours_left(token: str) -> float:
    return round((claims(token)["exp"] - time.time()) / 3600, 1)

# spike 自己建的測試群組 id 等，存在本機（gitignored）
LOCAL_STATE = LOCAL / "state.json"
