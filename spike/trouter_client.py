# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike：不靠瀏覽器，自己連 Trouter、註冊推播、收訊息事件。

    uv run trouter_client.py [秒數]

連上並註冊後會往測試群組發一則訊息，確認推播有進來。
"""

import asyncio
import json
import subprocess
import sys
import time
import uuid
from urllib.parse import urlencode

import requests
import websockets
from teams_api import skypetoken

TC = json.dumps({"cv": "2026.39.01.1", "ua": "TeamsCDL", "hr": "", "v": "1415/26100816141"}, separators=(",", ":"))
REGISTRAR = "https://edge.skype.com/registrar/prod/v2/registrations"


def register(token: str, epid: str, surl: str) -> int:
    body = {
        "clientDescription": {
            "appId": "TeamsCDLWebWorker",
            "aesKey": "",
            "languageId": "en-US",
            "platform": "chrome",
            "templateKey": "TeamsCDLWebWorker_2.7",
            "platformUIVersion": "1415/26100816141",
            "productContext": "TFL",
        },
        "registrationId": epid,
        "nodeId": "",
        "transports": {"TROUTER": [{"context": "", "path": surl, "ttl": 3600}]},
    }
    r = requests.post(REGISTRAR, headers={"X-Skypetoken": token, "content-type": "application/json"}, json=body, timeout=30)
    return r.status_code


async def main(seconds: int) -> None:
    token = skypetoken()
    epid = str(uuid.uuid4())
    query = urlencode({"tc": TC, "timeout": 40, "epid": epid, "cor_id": str(uuid.uuid4()), "con_num": f"{int(time.time() * 1000)}_0"})
    url = f"wss://go.trouter.skype.com/v4/c?{query}"
    start = time.monotonic()
    log = lambda *a: print(f"[{time.monotonic() - start:5.1f}s]", *a)
    seq = 0
    sent = False
    async with websockets.connect(url, max_size=None) as ws:
        async def emit(name: str, args: list | None = None) -> None:
            nonlocal seq
            seq += 1
            await ws.send(f"5:{seq}+::" + json.dumps({"name": name, **({"args": args} if args is not None else {})}))

        async def pinger() -> None:
            while True:
                await asyncio.sleep(30)
                await emit("ping")

        ping_task = asyncio.create_task(pinger())
        try:
            while time.monotonic() - start < seconds:
                try:
                    frame = await asyncio.wait_for(ws.recv(), timeout=1)
                except TimeoutError:
                    frame = None
                if frame == "1::":
                    log("connected, authenticating")
                    await emit("user.authenticate", [{"headers": {"X-Skypetoken": token}}])
                elif frame and frame.startswith("5:"):
                    event = json.loads(frame.split("::", 1)[1])
                    name = event.get("name")
                    if name == "trouter.connected":
                        surl = event["args"][0]["surl"]
                        log("trouter.connected, surl host:", surl.split("/")[2], "| registrar HTTP", register(token, epid, surl))
                    elif name == "trouter.message_loss":
                        await emit("trouter.processed_message_loss", event.get("args"))
                    else:
                        log("event", name)
                elif frame and frame.startswith("3:::"):
                    req = json.loads(frame[4:])
                    path = req.get("url", "").rsplit("/", 1)[-1]
                    body = json.loads(req["body"]) if req.get("body", "").startswith("{") else {}
                    res = body.get("resource", {})
                    log(f"PUSH {path}: {body.get('resourceType')} {res.get('threadtopic')!r} {res.get('imdisplayname')!r}: {str(res.get('content'))[:60]!r}")
                    ack = {
                        "id": req["id"],
                        "status": 200,
                        "headers": {"trouter-request": req["headers"].get("trouter-request", ""), "trouter-client": json.dumps({"cd": 1})},
                        "body": "",
                    }
                    await ws.send("3:::" + json.dumps(ack))
                elif frame and not frame.startswith("6:::"):
                    log("frame", frame[:120])

                if not sent and time.monotonic() - start > 10:
                    sent = True
                    log("=== 往測試群組發訊息 ===")
                    subprocess.run([sys.executable, "send.py", "trouter client 推播測試"], capture_output=True)
        finally:
            ping_task.cancel()


if __name__ == "__main__":
    asyncio.run(main(int(sys.argv[1]) if len(sys.argv) > 1 else 45))
