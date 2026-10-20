"""個人版 Teams gateway 的 Python client。

    from teams_gateway import TeamsGatewayClient

    gw = TeamsGatewayClient("http://localhost:5199", api_key="tgw_...")
    for chat in gw.list_chats():
        print(chat.topic, chat.id)
    gw.send(chat_id, "hello")
    for msg in gw.events(chats=[chat_id]):   # SSE，斷線自動重連
        print(msg.from_name, msg.content)
"""

from __future__ import annotations

import json
import time
from collections.abc import Iterator
from dataclasses import dataclass

import httpx

__all__ = ["TeamsGatewayClient", "Conversation", "Message", "GatewayError"]


class GatewayError(RuntimeError):
    pass


@dataclass(frozen=True)
class Conversation:
    id: str
    topic: str | None
    thread_type: str | None
    last_message_time: str | None

    @classmethod
    def _parse(cls, d: dict) -> "Conversation":
        return cls(d["id"], d.get("topic"), d.get("threadType"), d.get("lastMessageTime"))


@dataclass(frozen=True)
class Message:
    id: str
    conversation_id: str
    from_mri: str | None
    from_name: str | None
    type: str | None
    content: str | None
    sequence_id: int
    time: str | None

    @classmethod
    def _parse(cls, d: dict) -> "Message":
        return cls(
            d["id"],
            d["conversationId"],
            d.get("from"),
            d.get("fromName"),
            d.get("type"),
            d.get("content"),
            d.get("sequenceId", 0),
            d.get("time"),
        )


class TeamsGatewayClient:
    def __init__(self, base_url: str, api_key: str, *, timeout: float = 30.0):
        self._http = httpx.Client(
            base_url=base_url.rstrip("/"),
            headers={"Authorization": f"Bearer {api_key}"},
            timeout=timeout,
        )

    def __enter__(self) -> "TeamsGatewayClient":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def close(self) -> None:
        self._http.close()

    def list_chats(self) -> list[Conversation]:
        return [Conversation._parse(c) for c in self._get("/v1/chats")]

    def read_messages(self, chat_id: str, *, limit: int = 30, cursor: str | None = None) -> tuple[list[Message], str | None]:
        """回 (訊息清單, 下一頁游標)。游標往更舊的方向翻。"""
        params = {"limit": limit}
        if cursor:
            params["cursor"] = cursor
        page = self._get(f"/v1/chats/{chat_id}/messages", params=params)
        return [Message._parse(m) for m in page["messages"]], page.get("nextCursor")

    def send(self, chat_id: str, text: str) -> str:
        """發話，回 clientMessageId。"""
        return self._post(f"/v1/chats/{chat_id}/messages", {"text": text})["clientMessageId"]

    def events(self, *, chats: list[str] | None = None, reconnect: bool = True) -> Iterator[Message]:
        """SSE 新訊息串流。斷線自動重連（backoff），用 Last-Event-ID 接續（SSE id == 訊息 id）。"""
        params = {"chats": ",".join(chats)} if chats else None
        last_id: str | None = None
        backoff = 1.0
        while True:
            try:
                for msg in self._stream(params, last_id):
                    last_id = msg.id
                    yield msg
                backoff = 1.0
                if not reconnect:
                    return
            except (httpx.HTTPError, GatewayError):
                if not reconnect:
                    raise
                time.sleep(backoff)
                backoff = min(backoff * 2, 30.0)

    def _stream(self, params: dict | None, last_id: str | None) -> Iterator[Message]:
        headers = {"Last-Event-ID": last_id} if last_id else {}
        with self._http.stream("GET", "/v1/events", params=params, headers=headers, timeout=None) as resp:
            self._raise(resp)
            for line in resp.iter_lines():
                if line.startswith("data:"):
                    yield Message._parse(json.loads(line[5:].strip()))

    def _get(self, path: str, params: dict | None = None):
        resp = self._http.get(path, params=params)
        self._raise(resp)
        return resp.json()

    def _post(self, path: str, body: dict):
        resp = self._http.post(path, json=body)
        self._raise(resp)
        return resp.json()

    @staticmethod
    def _raise(resp: httpx.Response) -> None:
        if resp.is_success:
            return
        if resp.status_code in (401, 403):
            raise GatewayError(f"API key 無效或沒權限（HTTP {resp.status_code}）")
        raise GatewayError(f"gateway 回 HTTP {resp.status_code}: {resp.text[:200]}")
