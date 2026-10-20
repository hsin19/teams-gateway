# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike：看 Trouter WebSocket 上的推播長什麼樣子。

開網頁版 → 等 Trouter 連上 → 往測試群組發一則訊息 → 印出 Trouter 上收到的 frame（去敏、截斷）。
完整 frame 存 spike/.local/trouter.jsonl。

    uv run trouter_watch.py
"""

import asyncio
import json
import re
import subprocess
import sys

from cdp import LOCAL, Chrome

TOKEN_RE = re.compile(r"(eyJ[\w-]{10,}\.[\w-]+\.[\w-]+|[A-Za-z0-9_\-*!$+/=]{120,})")


def redact(s: str) -> str:
    return TOKEN_RE.sub("<TOKEN>", s)


async def main() -> None:
    out = (LOCAL / "trouter.jsonl").open("w")
    trouter_ids: set[str] = set()
    sent = False
    async with Chrome() as chrome:
        session = await chrome.new_page()
        await chrome.send("Network.enable", {}, session)
        await chrome.send("Page.navigate", {"url": "https://teams.live.com/v2/"}, session)
        loop = asyncio.get_running_loop()
        start = loop.time()
        connected_at = None
        while loop.time() - start < 120:
            try:
                msg = await asyncio.wait_for(chrome.events.get(), timeout=1)
            except TimeoutError:
                msg = None
            if msg:
                method, params = msg.get("method"), msg.get("params", {})
                if method == "Network.requestWillBeSent" and "registrar" in params["request"]["url"] and params["request"]["method"] == "POST":
                    try:
                        post = await chrome.send("Network.getRequestPostData", {"requestId": params["requestId"]}, session)
                        out.write(json.dumps({"t": loop.time() - start, "registrar": post["postData"]}) + "\n")
                        print(f"[{loop.time() - start:5.1f}s] registrar POST {redact(post['postData'])[:1200]}")
                    except RuntimeError as e:
                        print("registrar post data unavailable:", e)
                elif method == "Network.responseReceived" and "registrar" in params["response"]["url"]:
                    print(f"[{loop.time() - start:5.1f}s] registrar -> HTTP {params['response']['status']}")
                elif method == "Network.webSocketCreated" and "trouter" in params["url"]:
                    trouter_ids.add(params["requestId"])
                    connected_at = loop.time()
                    print(f"[{loop.time() - start:5.1f}s] trouter connected")
                elif method in ("Network.webSocketFrameReceived", "Network.webSocketFrameSent") and params.get("requestId") in trouter_ids:
                    data = params["response"]["payloadData"]
                    out.write(json.dumps({"t": loop.time() - start, "dir": method[-8:], "data": data}) + "\n")
                    print(f"[{loop.time() - start:5.1f}s] {'<-' if method.endswith('Received') else '->'} {redact(data)[:400]}")
            if connected_at and not sent and loop.time() - connected_at > 30:
                sent = True
                print(f"[{loop.time() - start:5.1f}s] === 往測試群組發訊息 ===")
                subprocess.run([sys.executable, "send.py", "trouter 推播測試"], cwd=LOCAL.parent, capture_output=True)
    out.close()


if __name__ == "__main__":
    asyncio.run(main())
