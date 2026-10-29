# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike：Slack 上編輯 / 刪除已送出的那則，同步到 Teams 會用到的 chatsvc 行為。只發到只有自己的測試群組。

    uv run http_refresh.py                # 先換 skypetoken
    uv run edit_delete.py [<thread id>]   # 沒給就用 .local/state.json 的 test_thread

一邊連 Trouter 收推播，一邊依序：
  E1  Text 發一則，PUT 改成另一段 Text
  E2  RichText/Html 發一則，PUT 改成另一段 HTML
  Q1  引用 E1 的回覆，PUT 時重帶 blockquote + qtdMsgs
  Q2  引用 E1 的回覆，PUT 只給新文字（不帶引用）
  D1  發一則，DELETE（不帶參數）
  D2  發一則，DELETE ?behavior=softDelete
  E3  Text 發一則，PUT 成 RichText/Html、不帶 clientmessageid 與 imdisplayname
  D3  發一則、刪掉，再 PUT（刪掉的能不能改）、再 DELETE 一次
  另外 PUT / DELETE 一個不存在的 id
最後印出 PUT / DELETE 的回應、推播、讀回來三邊各看到什麼。
"""

import asyncio
import json
import sys
import time
import uuid
from urllib.parse import urlencode

import requests
import websockets
from teams_api import LOCAL_STATE, VIEW, claims, headers, skypetoken
from trouter_client import TC, register

CHATSVC = "https://teams.live.com/api/chatsvc/consumer/v1"

token = skypetoken()
H = headers(token) | {"content-type": "application/json"}
me = "8:" + claims(token)["skypeid"]

state = json.loads(LOCAL_STATE.read_text()) if LOCAL_STATE.exists() else {}
tid = sys.argv[1] if len(sys.argv) > 1 else state["test_thread"]
MESSAGES = f"{CHATSVC}/users/ME/conversations/{tid}/messages"


def only_me() -> None:
    r = requests.get(f"{CHATSVC}/threads/{tid}", headers=H, params={"view": "msnp24Equivalent"}, timeout=30)
    r.raise_for_status()
    members = [m.get("id") for m in r.json().get("members", [])]
    if members != [me]:
        raise SystemExit(f"測試群組的成員不是只有自己（{len(members)} 人），不發")
    print("test thread ok: only me,", r.json().get("properties", {}).get("topic"))


def cmid() -> str:
    time.sleep(0.002)
    return f"{time.time():.6f}".replace(".", "")


def body(content: str, html: bool, client_id: str, properties: dict | None = None) -> dict:
    b = {"content": content, "messagetype": "RichText/Html" if html else "Text", "contenttype": "text",
         "clientmessageid": client_id, "imdisplayname": "teams-gateway"}
    if properties is not None:
        b["properties"] = properties
    return b


def post(label: str, b: dict) -> str:
    r = requests.post(MESSAGES, headers=H, json=b, timeout=30)
    print(f"[{label}] POST {r.status_code} {r.text[:120]!r}")
    r.raise_for_status()
    return str(r.json()["OriginalArrivalTime"])


def put(label: str, msg_id: str, b: dict) -> None:
    r = requests.put(f"{MESSAGES}/{msg_id}", headers=H, json=b, timeout=30)
    print(f"[{label}] PUT {r.status_code} body={r.text[:200]!r} headers={ {k: v for k, v in r.headers.items() if k.lower() in ('location', 'content-type')} }")


def delete(label: str, msg_id: str, params: dict | None = None) -> None:
    r = requests.delete(f"{MESSAGES}/{msg_id}", headers=H, params=params or {}, timeout=30)
    print(f"[{label}] DELETE {params or ''} {r.status_code} body={r.text[:200]!r}")


def recent(n: int = 30) -> list[dict]:
    r = requests.get(MESSAGES, headers=H, params={"view": VIEW, "pageSize": n, "startTime": 0}, timeout=30)
    r.raise_for_status()
    return r.json().get("messages", [])


def quote(quoted_id: str) -> tuple[str, dict]:
    block = (f'<blockquote itemscope="" itemtype="http://schema.skype.com/Reply" itemid="{quoted_id}">'
             f'<strong itemprop="mri" itemid="{me}">teams-gateway</strong>'
             f'<span itemprop="time" itemid="{quoted_id}"></span>'
             f'<p itemprop="preview">preview</p></blockquote>')
    props = {"qtdMsgs": json.dumps([{"messageId": quoted_id, "sender": me, "time": int(quoted_id)}])}
    return block, props


def experiments() -> dict[str, str]:
    """label → Teams id"""
    ids: dict[str, str] = {}

    c = cmid()
    ids["E1"] = post("E1", body("[spike E1] before edit", False, c))
    time.sleep(1)
    put("E1", ids["E1"], body("[spike E1] after edit", False, c))

    c = cmid()
    ids["E2"] = post("E2", body("<p>[spike E2] <b>before</b> edit</p>", True, c))
    time.sleep(1)
    put("E2", ids["E2"], body("<p>[spike E2] <i>after</i> edit</p>", True, c))

    block, props = quote(ids["E1"])
    c = cmid()
    ids["Q1"] = post("Q1", body(block + "<p>[spike Q1] reply before edit</p>", True, c, props))
    time.sleep(1)
    put("Q1", ids["Q1"], body(block + "<p>[spike Q1] reply after edit (quote resent)</p>", True, c, props))

    c = cmid()
    ids["Q2"] = post("Q2", body(block + "<p>[spike Q2] reply before edit</p>", True, c, props))
    time.sleep(1)
    put("Q2", ids["Q2"], body("<p>[spike Q2] reply after edit (no quote)</p>", True, c))

    ids["D1"] = post("D1", body("[spike D1] to be deleted", False, cmid()))
    time.sleep(1)
    delete("D1", ids["D1"])

    ids["D2"] = post("D2", body("[spike D2] to be soft-deleted", False, cmid()))
    time.sleep(1)
    delete("D2", ids["D2"], {"behavior": "softDelete"})

    ids["E3"] = post("E3", body("[spike E3] before edit", False, cmid()))
    time.sleep(1)
    put("E3", ids["E3"], {"content": "<p>[spike E3] after edit, no clientmessageid</p>", "messagetype": "RichText/Html", "contenttype": "text"})

    ids["D3"] = post("D3", body("[spike D3] deleted then edited", False, cmid()))
    time.sleep(1)
    delete("D3", ids["D3"])
    put("D3 after delete", ids["D3"], body("[spike D3] edit after delete", False, cmid()))
    delete("D3 again", ids["D3"])

    put("unknown id", "1", body("[spike] unknown id", False, cmid()))
    delete("unknown id", "1")
    return ids


async def watch(pushes: list[dict], ready: asyncio.Event, stop: asyncio.Event) -> None:
    epid = str(uuid.uuid4())
    query = urlencode({"tc": TC, "timeout": 40, "epid": epid, "cor_id": str(uuid.uuid4()), "con_num": f"{int(time.time() * 1000)}_0"})
    seq = 0
    last_ping = time.monotonic()
    async with websockets.connect(f"wss://go.trouter.skype.com/v4/c?{query}", max_size=None) as ws:
        async def emit(name: str, args: list | None = None) -> None:
            nonlocal seq
            seq += 1
            await ws.send(f"5:{seq}+::" + json.dumps({"name": name, **({"args": args} if args is not None else {})}))

        while not stop.is_set():
            if time.monotonic() - last_ping > 25:
                last_ping = time.monotonic()
                await emit("ping")
            try:
                frame = await asyncio.wait_for(ws.recv(), timeout=1)
            except TimeoutError:
                continue
            if frame == "1::":
                await emit("user.authenticate", [{"headers": {"X-Skypetoken": token}}])
            elif frame.startswith("5:"):
                event = json.loads(frame.split("::", 1)[1])
                if event.get("name") == "trouter.connected":
                    status = await asyncio.to_thread(register, token, epid, event["args"][0]["surl"])
                    print("trouter registrar HTTP", status)
                    ready.set()
                elif event.get("name") == "trouter.message_loss":
                    await emit("trouter.processed_message_loss", event.get("args"))
            elif frame.startswith("3:::"):
                req = json.loads(frame[4:])
                b = json.loads(req["body"]) if req.get("body", "").startswith("{") else {}
                if "resource" in b:
                    pushes.append({"resourceType": b.get("resourceType"), **b["resource"]})
                await ws.send("3:::" + json.dumps({
                    "id": req["id"], "status": 200, "body": "",
                    "headers": {"trouter-request": req["headers"].get("trouter-request", ""), "trouter-client": json.dumps({"cd": 1})},
                }))


def brief(m: dict) -> str:
    props = m.get("properties") or {}
    keep = {k: props[k] for k in ("edittime", "deletetime", "qtdMsgs") if k in props}
    return (f"type={m.get('messagetype')} version={m.get('version')} cmid={m.get('clientmessageid')} "
            f"skypeeditedid={m.get('skypeeditedid')} props={keep} content={m.get('content')!r}")


async def main() -> None:
    only_me()
    pushes: list[dict] = []
    ready, stop = asyncio.Event(), asyncio.Event()
    watcher = asyncio.create_task(watch(pushes, ready, stop))
    await asyncio.wait_for(ready.wait(), timeout=30)
    await asyncio.sleep(3)  # 註冊生效
    ids = await asyncio.to_thread(experiments)
    await asyncio.sleep(10)
    stop.set()
    await watcher

    stored = {m["id"]: m for m in recent()}
    print()
    for label, msg_id in ids.items():
        print(f"== {label} id={msg_id}")
        print(f"   read back: {brief(stored[msg_id]) if msg_id in stored else 'MISSING'}")
        for p in [p for p in pushes if str(p.get("id")) == msg_id]:
            print(f"   push {p.get('resourceType')}: {brief(p)}")


if __name__ == "__main__":
    asyncio.run(main())
