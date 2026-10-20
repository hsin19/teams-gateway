# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike：連 Worker 一起錄（網頁版的訊息功能跑在 Web Worker，只錄主頁面看不到 chatsvc 的請求）。

錄 chatsvc / endpoints / subscriptions / trouter 相關的請求與 WebSocket frame；中途往測試群組發一則訊息。

    uv run worker_capture.py [秒數]
"""

import asyncio
import json
import re
import subprocess
import sys

from cdp import LOCAL, Chrome

TOKEN_RE = re.compile(r"(eyJ[\w-]{10,}\.[\w-]+\.[\w-]+|[A-Za-z0-9_\-*!$+/=]{120,})")
INTERESTING = ("chatsvc", "endpoints", "subscriptions", "poll", "registrar", "trouter", "authz", "skypetoken")


def redact(s: str) -> str:
    return TOKEN_RE.sub("<TOKEN>", s)


async def enable(chrome: Chrome, session: str) -> None:
    await chrome.send("Network.enable", {}, session)
    await chrome.send("Target.setAutoAttach", {"autoAttach": True, "waitForDebuggerOnStart": False, "flatten": True}, session)


async def main(seconds: int) -> None:
    out = (LOCAL / "worker_capture.jsonl").open("w")
    kinds: dict[str, str] = {}
    sockets: set[str] = set()
    async with Chrome() as chrome:
        page = await chrome.new_page()
        kinds[page] = "page"
        await enable(chrome, page)
        await chrome.send("Page.navigate", {"url": "https://teams.live.com/v2/"}, page)
        loop = asyncio.get_running_loop()
        start = loop.time()
        sent = False
        while (elapsed := loop.time() - start) < seconds:
            if not sent and elapsed > 45:
                sent = True
                print(f"[{elapsed:5.1f}s] === 往測試群組發訊息 ===")
                subprocess.run([sys.executable, "send.py", "worker capture 推播測試"], cwd=LOCAL.parent, capture_output=True)
            try:
                msg = await asyncio.wait_for(chrome.events.get(), timeout=1)
            except TimeoutError:
                continue
            out.write(json.dumps(msg) + "\n")
            method, params, session = msg.get("method"), msg.get("params", {}), msg.get("sessionId")
            if method == "Target.attachedToTarget":
                child = params["sessionId"]
                kinds[child] = params["targetInfo"]["type"]
                print(f"[{elapsed:5.1f}s] attached {kinds[child]}: {params['targetInfo']['url'][:90]}")
                try:
                    await enable(chrome, child)
                    await chrome.send("Runtime.runIfWaitingForDebugger", {}, child)
                except RuntimeError as e:
                    print("  enable failed:", e)
            elif method == "Network.requestWillBeSent":
                req = params["request"]
                if any(k in req["url"] for k in INTERESTING):
                    body = ""
                    if req.get("hasPostData"):
                        try:
                            body = (await chrome.send("Network.getRequestPostData", {"requestId": params["requestId"]}, session))["postData"]
                        except RuntimeError:
                            body = "<unavailable>"
                    out.write(json.dumps({"post_for": params["requestId"], "body": body}) + "\n")
                    print(f"[{elapsed:5.1f}s] [{kinds.get(session, '?')}] {req['method']} {req['url'].split('?')[0][:110]} {redact(body)[:300]}")
            elif method == "Network.webSocketCreated":
                sockets.add(params["requestId"])
                print(f"[{elapsed:5.1f}s] [{kinds.get(session, '?')}] WS {params['url'].split('?')[0]}")
            elif method == "Network.webSocketFrameReceived" and params["requestId"] in sockets:
                data = params["response"]["payloadData"]
                if '"ping"' not in data and "pong" not in data:
                    print(f"[{elapsed:5.1f}s] [{kinds.get(session, '?')}] <- {redact(data)[:500]}")
    out.close()


if __name__ == "__main__":
    asyncio.run(main(int(sys.argv[1]) if len(sys.argv) > 1 else 100))
