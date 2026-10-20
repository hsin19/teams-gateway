# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13"]
# ///
"""Spike：用 profile 複本開 teams.live.com，錄下所有 HTTP / WebSocket 流量。

完整紀錄（含 token）只寫進 spike/.local/capture.jsonl，終端機只印去敏後的摘要。

    uv run spike/capture.py [秒數]
"""

import asyncio
import json
import sys
from collections import Counter
from urllib.parse import urlsplit

from cdp import LOCAL, Chrome, redact_headers

INTERESTING = ("login.", "auth", "token", "skype", "chatsvc", "trouter", "registrar", "endpoints", "poll")


async def main(seconds: int) -> None:
    LOCAL.mkdir(parents=True, exist_ok=True)
    out = (LOCAL / "capture.jsonl").open("w")
    requests: dict[str, dict] = {}
    hosts: Counter = Counter()
    async with Chrome() as chrome:
        session = await chrome.new_page()
        await chrome.send("Network.enable", {"maxPostDataSize": 65536}, session)
        await chrome.send("Page.navigate", {"url": "https://teams.live.com/v2/"}, session)
        loop = asyncio.get_running_loop()
        deadline = loop.time() + seconds
        while (remaining := deadline - loop.time()) > 0:
            try:
                msg = await asyncio.wait_for(chrome.events.get(), timeout=remaining)
            except TimeoutError:
                break
            method, params = msg.get("method"), msg.get("params", {})
            out.write(json.dumps(msg) + "\n")
            if method == "Network.requestWillBeSent":
                req = params["request"]
                requests[params["requestId"]] = req
                hosts[urlsplit(req["url"]).netloc] += 1
            elif method == "Network.loadingFinished":
                req = requests.get(params["requestId"])
                if req and any(k in req["url"] for k in INTERESTING):
                    try:
                        body = await chrome.send("Network.getResponseBody", {"requestId": params["requestId"]}, session)
                        out.write(json.dumps({"body_for": params["requestId"], "url": req["url"], **body}) + "\n")
                    except RuntimeError:
                        pass
            elif method in ("Network.webSocketCreated",):
                print("WS created:", params["url"].split("?")[0])
    out.close()

    print("\n== hosts ==")
    for host, n in hosts.most_common(30):
        print(f"{n:5d}  {host}")
    print("\n== interesting requests ==")
    seen = set()
    for req in requests.values():
        url = req["url"]
        key = (req["method"], url.split("?")[0])
        if key in seen or not any(k in url for k in INTERESTING):
            continue
        seen.add(key)
        print(req["method"], url.split("?")[0][:150], "| headers:", sorted(redact_headers(req.get("headers")).keys()))


if __name__ == "__main__":
    asyncio.run(main(int(sys.argv[1]) if len(sys.argv) > 1 else 45))
