# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13", "requests"]
# ///
"""Spike（ADR 0012）：Slack → Teams 發話會用到的 chatsvc 行為。只發到只有自己的測試群組。

    uv run http_refresh.py                 # 先換 skypetoken
    uv run send_variants.py [<thread id>]  # 沒給就用 .local/state.json 的 test_thread

一邊連 Trouter 收推播，一邊依序發：
  A   Text，clientmessageid 用 Slack ts 去掉小數點（16 碼）
  B   A 原封不動再送一次：同一個 clientmessageid 會不會去重
  C   RichText/Html，Slack rich_text 轉過去會用到的標籤
  D1  引用 A：blockquote + properties.qtdMsgs
  D2  引用 A：只有 blockquote
  D3  引用 A：只有 qtdMsgs
最後印出 POST 回應、推播、讀回來三邊各看到什麼。引用在 Teams 上長怎樣要開 app 看。
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
state["test_thread"] = tid
LOCAL_STATE.write_text(json.dumps(state, indent=1))


def only_me() -> None:
    r = requests.get(f"{CHATSVC}/threads/{tid}", headers=H, params={"view": "msnp24Equivalent"}, timeout=30)
    r.raise_for_status()
    members = [m.get("id") for m in r.json().get("members", [])]
    if members != [me]:
        raise SystemExit(f"測試群組的成員不是只有自己（{len(members)} 人），不發")
    print("test thread ok: only me,", r.json().get("properties", {}).get("topic"))


def slack_ts_id() -> str:
    time.sleep(0.002)  # 連發時微秒也要不同
    return f"{time.time():.6f}".replace(".", "")


def post(label: str, body: dict) -> dict:
    r = requests.post(f"{CHATSVC}/users/ME/conversations/{tid}/messages", headers=H, json=body, timeout=30)
    print(f"[{label}] POST {r.status_code} body={r.text[:200]!r} location={r.headers.get('Location', '')[-50:]!r}")
    try:
        return r.json()
    except ValueError:
        return {}


def recent(n: int = 20) -> list[dict]:
    r = requests.get(f"{CHATSVC}/users/ME/conversations/{tid}/messages", headers=H,
                     params={"view": VIEW, "pageSize": n, "startTime": 0}, timeout=30)
    r.raise_for_status()
    return r.json().get("messages", [])


def text(label: str, cmid: str) -> dict:
    return {"content": f"[spike {label}] plain text, clientmessageid {cmid}", "messagetype": "Text",
            "contenttype": "text", "clientmessageid": cmid, "imdisplayname": "teams-gateway"}


def html(label: str, cmid: str, content: str, properties: dict | None = None) -> dict:
    body = {"content": content, "messagetype": "RichText/Html", "contenttype": "text", "clientmessageid": cmid,
            "imdisplayname": "teams-gateway"}
    if properties is not None:
        body["properties"] = properties
    return body


def quote_block(quoted: dict) -> str:
    return (f'<blockquote itemscope="" itemtype="http://schema.skype.com/Reply" itemid="{quoted["id"]}">'
            f'<strong itemprop="mri" itemid="{me}">teams-gateway</strong>'
            f'<span itemprop="time" itemid="{quoted["id"]}"></span>'
            f'<p itemprop="preview">[spike A] plain text</p></blockquote>')


def qtd(quoted: dict) -> dict:
    return {"qtdMsgs": json.dumps([{"messageId": quoted["id"], "sender": me, "time": int(quoted["id"])}])}


FORMATTED = ('<p>[spike C] <b>bold</b> <i>italic</i> <s>strike</s> <code>code</code> '
             '<a href="https://example.com">link</a> 😄<br>second line</p>'
             '<ul><li>one</li><li>two</li></ul><ol><li>first</li></ol>'
             '<blockquote>plain quote</blockquote><pre>pre block</pre>'
             '<table><tr><th>h1</th><th>h2</th></tr><tr><td>a</td><td>b</td></tr></table><hr>')


def experiments() -> dict[str, tuple[str, dict]]:
    """label → (clientmessageid, POST 回應)"""
    sent: dict[str, tuple[str, dict]] = {}
    a = text("A", slack_ts_id())
    sent["A"] = (a["clientmessageid"], post("A", a))
    time.sleep(1)
    sent["B"] = (a["clientmessageid"], post("B (resend A)", a))
    cmid = slack_ts_id()
    sent["C"] = (cmid, post("C", html("C", cmid, FORMATTED)))

    time.sleep(2)
    quoted = next(m for m in recent() if m.get("clientmessageid") == a["clientmessageid"])
    cmid = slack_ts_id()
    sent["D1"] = (cmid, post("D1", html("D1", cmid, quote_block(quoted) + "<p>[spike D1] blockquote + qtdMsgs</p>", qtd(quoted))))
    cmid = slack_ts_id()
    sent["D2"] = (cmid, post("D2", html("D2", cmid, quote_block(quoted) + "<p>[spike D2] blockquote only</p>")))
    cmid = slack_ts_id()
    sent["D3"] = (cmid, post("D3", html("D3", cmid, "<p>[spike D3] qtdMsgs only</p>", qtd(quoted))))
    return sent


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
                body = json.loads(req["body"]) if req.get("body", "").startswith("{") else {}
                if body.get("resourceType") == "NewMessage":
                    pushes.append(body["resource"])
                await ws.send("3:::" + json.dumps({
                    "id": req["id"], "status": 200, "body": "",
                    "headers": {"trouter-request": req["headers"].get("trouter-request", ""), "trouter-client": json.dumps({"cd": 1})},
                }))


async def main() -> None:
    only_me()
    pushes: list[dict] = []
    ready, stop = asyncio.Event(), asyncio.Event()
    watcher = asyncio.create_task(watch(pushes, ready, stop))
    await asyncio.wait_for(ready.wait(), timeout=30)
    await asyncio.sleep(3)  # 註冊生效
    sent = await asyncio.to_thread(experiments)
    await asyncio.sleep(10)
    stop.set()
    await watcher

    stored = recent()
    print()
    for label, (cmid, resp) in sent.items():
        got = [m for m in stored if m.get("clientmessageid") == cmid]
        pushed = [p for p in pushes if p.get("clientmessageid") == cmid]
        print(f"== {label}: clientmessageid {cmid} ({len(cmid)} digits), POST OriginalArrivalTime={resp.get('OriginalArrivalTime')}")
        print(f"   read back: {len(got)} message(s) ids={[m['id'] for m in got]}; pushed: {len(pushed)} ids={[p.get('id') for p in pushed]}")
        for m in got[:1]:
            props = m.get("properties") or {}
            print(f"   messagetype={m.get('messagetype')} version={m.get('version')} properties.keys={sorted(props)}")
            if "qtdMsgs" in props:
                print(f"   qtdMsgs={props['qtdMsgs']}")
            print(f"   content={m.get('content')!r}")
        for p in pushed[:1]:
            print(f"   push messagetype={p.get('messagetype')} content same as read back: {p.get('content') == (got[0].get('content') if got else None)}")


if __name__ == "__main__":
    asyncio.run(main())
