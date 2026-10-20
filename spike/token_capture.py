# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13"]
# ///
"""Spike：逼網頁版重新拿 token，錄下整條認證流程（含 Worker）。

    uv run token_capture.py skypetoken   # 只刪 skypetoken cookie → 看 skypetoken 怎麼換
    uv run token_capture.py msal         # 再清掉 teams.live.com 的 storage（MSAL 快取）→ 看靜默授權

每次都從 teams skill 的 profile 重新複製一份，不影響原本的登入。完整紀錄存 spike/.local/token_<mode>.jsonl。
"""

import asyncio
import json
import re
import shutil
import sys
from urllib.parse import parse_qsl, urlsplit

from cdp import LOCAL, Chrome, copy_profile

TOKEN_RE = re.compile(r"(eyJ[\w-]{10,}\.[\w-]+\.[\w-]+|[A-Za-z0-9_\-*!$+/=.]{100,})")
AUTH_HINTS = ("login.", "/authz", "skypetoken", "oauth2", "/auth/", "authorize", "/token")
SAFE_PARAMS = {"client_id", "grant_type", "scope", "redirect_uri", "response_type", "prompt", "response_mode", "code_challenge_method", "nonce", "state"}


def redact(s: str) -> str:
    return TOKEN_RE.sub("<TOKEN>", s)


def params_summary(text: str) -> dict:
    return {k: (v if k in SAFE_PARAMS else f"<len {len(v)}>") for k, v in parse_qsl(text)}


async def main(mode: str) -> None:
    profile = copy_profile(refresh=True)
    if mode == "msal":
        # MSAL 快取與網頁快取的 skypetoken 都在這些 storage；只留 cookie（含 login.live.com 的登入狀態）
        for sub in ("Local Storage", "Session Storage", "IndexedDB", "Service Worker", "File System"):
            shutil.rmtree(profile / "Default" / sub, ignore_errors=True)
    out = (LOCAL / f"token_{mode}.jsonl").open("w")
    kinds: dict[str, str] = {}
    async with Chrome() as chrome:
        page = await chrome.new_page()
        kinds[page] = "page"

        async def enable(session: str) -> None:
            await chrome.send("Network.enable", {}, session)
            await chrome.send("Target.setAutoAttach", {"autoAttach": True, "waitForDebuggerOnStart": False, "flatten": True}, session)

        await enable(page)
        await chrome.send("Network.deleteCookies", {"name": "skypetoken", "domain": "teams.live.com"}, page)
        await chrome.send("Network.deleteCookies", {"name": "skypetoken", "domain": ".teams.live.com"}, page)
        await chrome.send("Page.navigate", {"url": "https://teams.live.com/v2/"}, page)

        loop = asyncio.get_running_loop()
        start = loop.time()
        requests: dict[str, dict] = {}
        while loop.time() - start < 60:
            try:
                msg = await asyncio.wait_for(chrome.events.get(), timeout=1)
            except TimeoutError:
                continue
            out.write(json.dumps(msg) + "\n")
            method, params, session = msg.get("method"), msg.get("params", {}), msg.get("sessionId")
            t = f"[{loop.time() - start:5.1f}s]"
            if method == "Target.attachedToTarget":
                kinds[params["sessionId"]] = params["targetInfo"]["type"]
                try:
                    await enable(params["sessionId"])
                except RuntimeError:
                    pass
            elif method == "Network.requestWillBeSent":
                req = params["request"]
                if req["method"] == "OPTIONS" or not any(h in req["url"] for h in AUTH_HINTS) or "imageauth" in req["url"]:
                    continue
                requests[params["requestId"]] = req
                body = ""
                if req.get("hasPostData"):
                    try:
                        body = (await chrome.send("Network.getRequestPostData", {"requestId": params["requestId"]}, session))["postData"]
                    except RuntimeError:
                        body = req.get("postData", "")
                    out.write(json.dumps({"post_for": params["requestId"], "body": body}) + "\n")
                u = urlsplit(req["url"])
                summary = params_summary(body) if "=" in body and not body.startswith("{") else redact(body)[:200]
                print(t, f"[{kinds.get(session, '?')}]", req["method"], f"{u.netloc}{u.path}", params_summary(u.query) or "", summary or "")
            elif method == "Network.responseReceived" and params["requestId"] in requests:
                resp = params["response"]
                loc = resp.get("headers", {}).get("location") or resp.get("headers", {}).get("Location") or ""
                print(t, "   ->", resp["status"], redact(urlsplit(loc).path)[:80] if loc else "")
            elif method == "Network.loadingFinished" and params["requestId"] in requests:
                try:
                    body = (await chrome.send("Network.getResponseBody", {"requestId": params["requestId"]}, session))["body"]
                    out.write(json.dumps({"body_for": params["requestId"], "body": body}) + "\n")
                    keys = list(json.loads(body)) if body.startswith("{") else body[:80]
                    print(t, "   body keys:", redact(str(keys))[:200])
                except (RuntimeError, json.JSONDecodeError):
                    pass
        cookies = (await chrome.send("Network.getAllCookies", {}, page))["cookies"]
        print("skypetoken cookie after:", any(c["name"] == "skypetoken" for c in cookies))
    out.close()


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1] if len(sys.argv) > 1 else "skypetoken"))
