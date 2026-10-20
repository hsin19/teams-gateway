# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike：只靠 login.live.com 的登入 cookie，用純 HTTP 換到 skypetoken。

    uv run http_refresh.py export   # 開一次瀏覽器，把登入 cookie 匯出到 spike/.local/login_cookies.json
    uv run http_refresh.py          # 純 HTTP：靜默授權 → code → token → skypetoken → 打 chatsvc 驗證

流程對應網頁版 MSAL：authorize(prompt=none, PKCE) → authorization_code → refresh_token(auth scope) → authz/consumer。
"""

import asyncio
import base64
import hashlib
import json
import secrets
import sys
import time
import uuid
from datetime import datetime
from urllib.parse import parse_qs, urlencode, urljoin, urlsplit

import requests
from cdp import LOCAL, Chrome, copy_profile
from teams_api import BASE, VIEW, claims, headers

CLIENT_ID = "4b3e8f46-56d3-427f-b1e2-d239b2ea6bca"
REDIRECT_URI = "https://teams.live.com/v2/authv2"
AUTHORIZE = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"
TOKEN = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
AUTHZ = "https://teams.live.com/api/auth/v2.0/authz/consumer"
LOGIN_SCOPE = "https://mtsvc.fl.teams.microsoft.com/teams.mt.readwrite openid profile offline_access"
AUTH_SCOPE = "https://auth.fl.teams.microsoft.com/teams.auth.readwrite openid profile offline_access"
COOKIES = LOCAL / "login_cookies.json"
UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36"


async def export() -> None:
    async with Chrome(profile=copy_profile(refresh=True)) as chrome:
        cookies = (await chrome.send("Storage.getCookies", {}))["cookies"]
    keep = [c for c in cookies if any(d in c["domain"] for d in ("live.com", "microsoftonline.com", "microsoft.com"))]
    COOKIES.write_text(json.dumps(keep))
    print(f"匯出 {len(keep)} 個 cookie（值不印）")
    for c in sorted(keep, key=lambda c: c.get("expires", 0)):
        if "login" in c["domain"]:
            exp = "session" if c.get("expires", -1) <= 0 else datetime.fromtimestamp(c["expires"]).strftime("%Y-%m-%d")
            print(f"  {c['domain']:28s} {c['name']:24s} 到期 {exp}")


def session() -> requests.Session:
    s = requests.Session()
    s.headers["User-Agent"] = UA
    for c in json.loads(COOKIES.read_text()):
        s.cookies.set(c["name"], c["value"], domain=c["domain"], path=c.get("path", "/"))
    return s


def pkce() -> tuple[str, str]:
    verifier = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode()
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b"=").decode()
    return verifier, challenge


def silent_authorize(s: requests.Session, challenge: str) -> str:
    params = {
        "client_id": CLIENT_ID,
        "scope": LOGIN_SCOPE,
        "redirect_uri": REDIRECT_URI,
        "response_mode": "fragment",
        "response_type": "code",
        "prompt": "none",
        "nonce": str(uuid.uuid4()),
        "state": str(uuid.uuid4()),
        "code_challenge": challenge,
        "code_challenge_method": "S256",
    }
    url = f"{AUTHORIZE}?{urlencode(params)}"
    for hop in range(10):
        r = s.get(url, allow_redirects=False, timeout=30)
        loc = r.headers.get("Location", "")
        print(f"  hop {hop}: {r.status_code} {urlsplit(url).netloc}{urlsplit(url).path} -> {urlsplit(loc).netloc}{urlsplit(loc).path}")
        if loc.startswith(REDIRECT_URI):
            fragment = parse_qs(urlsplit(loc).fragment)
            if "code" in fragment:
                return fragment["code"][0]
            raise SystemExit(f"沒拿到 code：{ {k: v[0][:80] for k, v in fragment.items()} }")
        if not loc:
            raise SystemExit(f"停在 {r.status_code}，body 開頭：{r.text[:300]!r}")
        url = urljoin(url, loc)
    raise SystemExit("redirect 太多次")


def main() -> None:
    s = session()
    verifier, challenge = pkce()
    print("1. 靜默授權")
    code = silent_authorize(s, challenge)
    print("   拿到 code（長度", len(code), "）")

    print("2. code → token")
    origin = {"Origin": "https://teams.live.com"}
    r = s.post(TOKEN, headers=origin, timeout=30, data={
        "client_id": CLIENT_ID, "redirect_uri": REDIRECT_URI, "scope": LOGIN_SCOPE, "code": code,
        "code_verifier": verifier, "grant_type": "authorization_code", "client_info": "1",
    })
    print("  ", r.status_code, {k: (v if k in ("expires_in", "refresh_token_expires_in", "scope") else "<...>") for k, v in r.json().items()})
    refresh_token = r.json()["refresh_token"]

    print("3. refresh_token → auth scope")
    r = s.post(TOKEN, headers=origin, timeout=30, data={
        "client_id": CLIENT_ID, "redirect_uri": REDIRECT_URI, "scope": AUTH_SCOPE,
        "refresh_token": refresh_token, "grant_type": "refresh_token", "client_info": "1",
    })
    print("  ", r.status_code, {k: (v if k in ("expires_in", "refresh_token_expires_in", "scope") else "<...>") for k, v in r.json().items()})
    access_token = r.json()["access_token"]

    print("4. authz/consumer → skypetoken")
    r = requests.post(AUTHZ, timeout=30, headers={
        "authorization": f"Bearer {access_token}",
        "x-ms-client-type": "web",
        "x-ms-client-version": "1415/26100816141",
        "ms-teams-authz-type": "ExplicitLogin",
        "claimschallengecapable": "true",
        "User-Agent": UA,
    })
    token = r.json()["skypeToken"]["skypetoken"]
    print("  ", r.status_code, "skypeid", claims(token)["skypeid"], "| expiresIn", r.json()["skypeToken"]["expiresIn"])

    print("5. 用新 skypetoken 打 chatsvc")
    r = requests.get(f"{BASE}/conversations", headers=headers(token), params={"view": VIEW, "pageSize": 5}, timeout=30)
    print("  ", r.status_code, len(r.json().get("conversations", [])), "conversations")
    (LOCAL / "http_skypetoken.json").write_text(json.dumps({"token": token, "at": time.time()}))


if __name__ == "__main__":
    if sys.argv[1:] == ["export"]:
        asyncio.run(export())
    else:
        main()
