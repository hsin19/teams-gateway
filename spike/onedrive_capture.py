# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13"]
# ///
"""Spike：網頁版 Teams 怎麼顯示只放在發送者 OneDrive 的圖（附件 pasted-image-*.png、previewUrl 是空字串）？

用 profile 複本開到那則訊息，錄下所有 OneDrive / Graph / token 請求：打哪個 host 與路徑、怎麼認證、換 token 時要的 scope。
完整紀錄（含 token）只寫進 spike/.local/onedrive_capture.jsonl，終端機只印去敏後的摘要。結論在 FINDINGS.md〈OneDrive 分享檔〉。

    uv run spike/onedrive_capture.py <thread id> <訊息 id> [秒數]
"""

import asyncio
import base64
import json
import re
import sys
from urllib.parse import parse_qsl, quote, urlsplit

from cdp import LOCAL, Chrome, copy_profile

WATCH = ("onedrive", "personalcontent", "graph.microsoft", "1drv", "sharepoint", "storage.live", "photos.", "/oauth2/", "login.live")
TOKEN_RE = re.compile(r"(eyJ[\w-]{10,}\.[\w-]+\.[\w-]+|[A-Za-z0-9_\-*!$+/=.%]{120,})")
SAFE_PARAMS = {"client_id", "grant_type", "scope", "redirect_uri", "response_type", "prompt", "claims", "resource"}


def redact(s: str) -> str:
    return TOKEN_RE.sub("<TOKEN>", s)


def auth_kind(headers: dict) -> str:
    for k, v in (headers or {}).items():
        if k.lower() == "authorization":
            scheme = v.split(" ", 1)[0]
            token = v.split(" ", 1)[-1]
            aud = ""
            if token.count(".") == 2:
                try:
                    payload = token.split(".")[1]
                    claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
                    aud = f" aud={claims.get('aud')} scp={claims.get('scp')}"
                except Exception:
                    aud = " (jwt, unreadable)"
            else:
                aud = f" (opaque, {len(token)} chars)"
            return scheme + aud
    return "-"


async def main(thread: str, message: str, seconds: int) -> None:
    LOCAL.mkdir(parents=True, exist_ok=True)
    out = (LOCAL / "onedrive_capture.jsonl").open("w")
    requests: dict[str, dict] = {}
    statuses: dict[str, tuple[int, str]] = {}
    async with Chrome(profile=copy_profile()) as chrome:
        session = await chrome.new_page()
        await chrome.send("Network.enable", {"maxPostDataSize": 65536}, session)
        # 網頁版多半在 Web Worker / Service Worker 裡抓資料，要連它們一起錄
        await chrome.send("Target.setAutoAttach", {"autoAttach": True, "waitForDebuggerOnStart": False, "flatten": True}, session)
        await chrome.send("Page.enable", {}, session)
        await chrome.send("Emulation.setDeviceMetricsOverride", {"width": 1440, "height": 900, "deviceScaleFactor": 1, "mobile": False}, session)
        await chrome.send("Page.navigate", {"url": "https://teams.live.com/v2/"}, session)

        loop = asyncio.get_running_loop()
        start = loop.time()
        navigated = False
        continued = False
        attaching: set[asyncio.Task] = set()
        while (remaining := start + seconds - loop.time()) > 0:
            if not navigated and loop.time() - start > 25:
                link = f"https://teams.live.com/l/message/{quote(thread)}/{message}?context=%7B%22contextType%22%3A%22chat%22%7D"
                await chrome.send("Page.navigate", {"url": link}, session)
                navigated = True
            if navigated and not continued and loop.time() - start > 32:
                # 深連結會先停在「Join the community」中間頁
                clicked = await chrome.send(
                    "Runtime.evaluate",
                    {"expression": "(() => { const b = [...document.querySelectorAll('button,a')].find(e => /Continue on this browser/i.test(e.textContent)); if (b) { b.click(); return true } return false })()", "returnByValue": True},
                    session,
                )
                continued = clicked.get("result", {}).get("value") is True
                print("clicked continue:", continued)
                if not continued and loop.time() - start > 45:
                    continued = True
            try:
                msg = await asyncio.wait_for(chrome.events.get(), timeout=min(remaining, 1))
            except TimeoutError:
                continue
            method, params, sid = msg.get("method"), msg.get("params", {}), msg.get("sessionId")
            key = f"{sid}:{params.get('requestId')}"
            if method == "Target.attachedToTarget":
                child = params["sessionId"]

                async def attach(child: str = child) -> None:
                    # 有些 target 一下就不見或不回應，別讓它卡住整個錄製
                    for cmd, args in (
                        ("Network.enable", {"maxPostDataSize": 65536}),
                        ("Target.setAutoAttach", {"autoAttach": True, "waitForDebuggerOnStart": False, "flatten": True}),
                        ("Runtime.runIfWaitingForDebugger", {}),
                    ):
                        try:
                            await chrome.send(cmd, args, child)
                        except (RuntimeError, TimeoutError):
                            pass

                attaching.add(asyncio.create_task(attach()))
            elif method == "Network.requestWillBeSent":
                req = params["request"]
                if any(w in req["url"] for w in WATCH):
                    requests[key] = req
                    out.write(json.dumps(msg) + "\n")
            elif method == "Network.responseReceived" and key in requests:
                resp = params["response"]
                statuses[key] = (resp["status"], resp.get("mimeType", ""))
                out.write(json.dumps(msg) + "\n")
            elif method == "Network.loadingFinished" and key in requests:
                req = requests[key]
                if "/oauth2/" in req["url"] or "json" in statuses.get(key, (0, ""))[1]:
                    try:
                        body = await chrome.send("Network.getResponseBody", {"requestId": params["requestId"]}, sid)
                        out.write(json.dumps({"body_for": key, "url": req["url"], **body}) + "\n")
                    except RuntimeError:
                        pass

        for task in attaching:
            task.cancel()
        shot = await chrome.send("Page.captureScreenshot", {"format": "png"}, session)
        (LOCAL / "onedrive_capture.png").write_bytes(base64.b64decode(shot["data"]))
    out.close()

    print("== watched requests ==")
    for rid, req in requests.items():
        status, mime = statuses.get(rid, (0, ""))
        parts = urlsplit(req["url"])
        line = f"{req['method']} {status} {mime} {parts.netloc}{redact(parts.path)[:140]}"
        if parts.query:
            line += "?" + redact(parts.query)[:160]
        print(line)
        print("    auth:", auth_kind(req.get("headers")))
        if req.get("postData") and "/oauth2/" in req["url"]:
            print("    form:", {k: (v if k in SAFE_PARAMS else f"<len {len(v)}>") for k, v in parse_qsl(req["postData"])})
    print(f"\nscreenshot: {LOCAL / 'onedrive_capture.png'}")


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 70))
