# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike：用帳號（MRI）查顯示名稱——網頁版看到不認識的人時打的 mt/beta/users/fetchShortProfile。

    uv run profiles.py 8:live:xxx 8:live:yyy

token 照 http_refresh.py 的流程換；這支 API 要 MT 服務的 Bearer token（換 skypetoken 時第一步 authorization_code 拿到的那顆）。
"""
import json
import sys

import requests

from http_refresh import AUTH_SCOPE, AUTHZ, CLIENT_ID, LOGIN_SCOPE, REDIRECT_URI, TOKEN, UA, pkce, session, silent_authorize

PROFILES = ("https://teams.live.com/api/mt/beta/users/fetchShortProfile"
            "?isMailAddress=false&enableGuest=true&skypeTeamsInfo=true&canBeSmtpAddress=false"
            "&includeIBBarredUsers=false&includeDisabledAccounts=false&ggEnabled=true")


def tokens() -> tuple[str, str]:
    """(MT 的 access token, skypetoken)"""
    s = session()
    verifier, challenge = pkce()
    code = silent_authorize(s, challenge)
    origin = {"Origin": "https://teams.live.com"}
    login = s.post(TOKEN, headers=origin, timeout=30, data={
        "client_id": CLIENT_ID, "redirect_uri": REDIRECT_URI, "scope": LOGIN_SCOPE, "code": code,
        "code_verifier": verifier, "grant_type": "authorization_code", "client_info": "1",
    }).json()
    print("MT token: expires_in", login.get("expires_in"), "| scope", login.get("scope"))
    auth = s.post(TOKEN, headers=origin, timeout=30, data={
        "client_id": CLIENT_ID, "redirect_uri": REDIRECT_URI, "scope": AUTH_SCOPE,
        "refresh_token": login["refresh_token"], "grant_type": "refresh_token", "client_info": "1",
    }).json()
    skype = requests.post(AUTHZ, timeout=30, headers={
        "authorization": f"Bearer {auth['access_token']}", "x-ms-client-type": "web", "x-ms-client-version": "1415/26100816141",
        "ms-teams-authz-type": "ExplicitLogin", "claimschallengecapable": "true", "User-Agent": UA,
    }).json()["skypeToken"]["skypetoken"]
    return login["access_token"], skype


def fetch(mris: list[str], skypetoken: str, mt_token: str | None) -> requests.Response:
    h = {"x-skypetoken": skypetoken, "x-ms-client-type": "cdlworker", "x-ms-client-version": "1415/26100816141",
         "content-type": "application/json;charset=UTF-8", "User-Agent": UA}
    if mt_token:
        h["authorization"] = f"Bearer {mt_token}"
    return requests.post(PROFILES, headers=h, data=json.dumps(mris), timeout=30)


def shape(x):
    if isinstance(x, dict):
        return {k: shape(v) for k, v in x.items()}
    if isinstance(x, list):
        return [shape(x[0])] if x else []
    return type(x).__name__


if __name__ == "__main__":
    mris = sys.argv[1:]
    mt, skype = tokens()
    for label, bearer in (("只帶 skypetoken", None), ("skypetoken + MT Bearer", mt)):
        r = fetch(mris, skype, bearer)
        print(f"\n[{label}] HTTP {r.status_code}")
        if not r.ok:
            print("  ", r.text[:200])
            continue
        data = r.json()
        print("  shape:", json.dumps(shape(data), ensure_ascii=False)[:800])
        for p in data.get("value", data if isinstance(data, list) else []):
            print("  ", p.get("mri"), "→", p.get("displayName"))
