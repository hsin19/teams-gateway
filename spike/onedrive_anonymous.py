# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13"]
# ///
"""Spike：用全新、沒登入的 profile 開 OneDrive 分享連結（1drv.ms），錄下檢視器怎麼匿名拿到檔案：
打哪些 host 與路徑、用什麼認證、頁面上的圖多大。完整紀錄只寫進 spike/.local/onedrive_anonymous.jsonl，終端機只印去敏後的摘要。

    uv run spike/onedrive_anonymous.py <1drv.ms 連結> [秒數]
"""

import asyncio
import json
import re
import shutil
import sys
import tempfile
from pathlib import Path
from urllib.parse import urlsplit

from cdp import LOCAL, Chrome

WATCH = ("svc.ms", "onedrive", "personalcontent", "graph.microsoft", "1drv", "sharepoint", "storage.live", "badger")
TOKEN_RE = re.compile(r"(eyJ[\w-]{10,}\.[\w-]+\.[\w-]+|[A-Za-z0-9_\-*!$+/=.%]{80,})")


def redact(s: str) -> str:
    return TOKEN_RE.sub("<TOKEN>", s)


def auth_scheme(headers: dict) -> str:
    for k, v in (headers or {}).items():
        if k.lower() == "authorization":
            return v.split(" ", 1)[0]
    return "-"


async def main(link: str, seconds: int) -> None:
    LOCAL.mkdir(parents=True, exist_ok=True)
    out = (LOCAL / "onedrive_anonymous.jsonl").open("w")
    profile = Path(tempfile.mkdtemp(prefix="anon-", dir=LOCAL))
    requests: dict[str, dict] = {}
    statuses: dict[str, tuple[int, str]] = {}
    try:
        async with Chrome(profile=profile) as chrome:
            page = await chrome.new_page()
            await chrome.send("Network.enable", {"maxPostDataSize": 65536}, page)
            await chrome.send("Target.setAutoAttach", {"autoAttach": True, "waitForDebuggerOnStart": False, "flatten": True}, page)
            await chrome.send("Page.navigate", {"url": link}, page)
            loop = asyncio.get_running_loop()
            deadline = loop.time() + seconds
            while (remaining := deadline - loop.time()) > 0:
                try:
                    msg = await asyncio.wait_for(chrome.events.get(), timeout=min(remaining, 1))
                except TimeoutError:
                    continue
                method, params, sid = msg.get("method"), msg.get("params", {}), msg.get("sessionId")
                key = f"{sid}:{params.get('requestId')}"
                if method == "Target.attachedToTarget":
                    for cmd in ("Network.enable", "Runtime.runIfWaitingForDebugger"):
                        asyncio.create_task(chrome.send(cmd, {}, params["sessionId"])).add_done_callback(lambda t: t.exception())
                elif method == "Network.requestWillBeSent" and any(w in params["request"]["url"] for w in WATCH):
                    requests[key] = params["request"]
                    out.write(json.dumps(msg) + "\n")
                elif method == "Network.responseReceived" and key in requests:
                    statuses[key] = (params["response"]["status"], params["response"].get("mimeType", ""))
                    out.write(json.dumps(msg) + "\n")
                elif method == "Network.loadingFinished" and key in requests and "json" in statuses.get(key, (0, ""))[1]:
                    try:
                        body = await chrome.send("Network.getResponseBody", {"requestId": params["requestId"]}, sid)
                        out.write(json.dumps({"body_for": key, "url": requests[key]["url"], **body}) + "\n")
                    except (RuntimeError, TimeoutError):
                        pass
            images = await chrome.send(
                "Runtime.evaluate",
                {"expression": "[...document.images].filter(i => i.naturalWidth > 300).map(i => `${i.naturalWidth}x${i.naturalHeight}`)", "returnByValue": True},
                page,
            )
    finally:
        out.close()
        shutil.rmtree(profile, ignore_errors=True)

    print("== watched requests ==")
    for key, req in requests.items():
        status, mime = statuses.get(key, (0, ""))
        if req["method"] == "OPTIONS":
            continue
        parts = urlsplit(req["url"])
        print(f"{req['method']} {status} {mime} auth={auth_scheme(req.get('headers'))} {parts.netloc}{redact(parts.path)[:120]}" + (f"?{redact(parts.query)[:120]}" if parts.query else ""))
    print("images on page:", images.get("result", {}).get("value"))


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 25))
