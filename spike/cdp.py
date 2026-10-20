# /// script
# requires-python = ">=3.12"
# dependencies = ["websockets>=13"]
# ///
"""Spike 共用：用 profile 複本開 Chromium，透過 CDP 操作。

profile 一律用 spike/.local/profile（從 teams skill 的 pw-profile 複製），不碰原本那份。
"""

import asyncio
import itertools
import json
import shutil
import subprocess
import tempfile
from pathlib import Path

import websockets

HERE = Path(__file__).resolve().parent
LOCAL = HERE / ".local"
PROFILE = LOCAL / "profile"
SOURCE_PROFILE = Path.home() / ".claude/teams/pw-profile"
CHROME = (
    Path.home()
    / "Library/Caches/ms-playwright/chromium-1248/chrome-mac-arm64"
    / "Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing"
)
SKIP_DIRS = ["Cache", "Code Cache", "GPUCache", "CacheStorage", "ScriptCache", "DawnCache", "ShaderCache"]

SECRET_KEYS = ("authorization", "cookie", "set-cookie", "x-skypetoken", "authentication", "x-ms-client-request-id")


def copy_profile(refresh: bool = False) -> Path:
    if PROFILE.exists() and not refresh:
        return PROFILE
    LOCAL.mkdir(parents=True, exist_ok=True)
    excludes = [arg for d in SKIP_DIRS for arg in ("--exclude", d)]
    subprocess.run(["rsync", "-a", "--delete", *excludes, f"{SOURCE_PROFILE}/", f"{PROFILE}/"], check=True)
    return PROFILE


class Chrome:
    """開一個 Chromium，連上 browser 層的 CDP websocket。"""

    def __init__(self, headless: bool = True, profile: Path | None = None):
        self.headless = headless
        self.profile = profile or copy_profile()
        self.proc: subprocess.Popen | None = None
        self.ws = None
        self._ids = itertools.count(1)
        self._pending: dict[int, asyncio.Future] = {}
        self.events: asyncio.Queue = asyncio.Queue()
        self._reader: asyncio.Task | None = None

    async def __aenter__(self) -> "Chrome":
        port_file = self.profile / "DevToolsActivePort"
        port_file.unlink(missing_ok=True)
        args = [
            str(CHROME),
            f"--user-data-dir={self.profile}",
            "--use-mock-keychain",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "about:blank",
        ]
        if self.headless:
            args.insert(1, "--headless=new")
        self.proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            if port_file.exists() and port_file.read_text().strip():
                break
            await asyncio.sleep(0.1)
        port, path = port_file.read_text().split()
        self.ws = await websockets.connect(f"ws://127.0.0.1:{port}{path}", max_size=None)
        self._reader = asyncio.create_task(self._read())
        return self

    async def __aexit__(self, *exc) -> None:
        if self._reader:
            self._reader.cancel()
        if self.ws:
            await self.ws.close()
        if self.proc:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.proc.kill()

    async def _read(self) -> None:
        async for raw in self.ws:
            msg = json.loads(raw)
            if "id" in msg and msg["id"] in self._pending:
                self._pending.pop(msg["id"]).set_result(msg)
            else:
                await self.events.put(msg)

    async def send(self, method: str, params: dict | None = None, session: str | None = None) -> dict:
        msg_id = next(self._ids)
        msg = {"id": msg_id, "method": method, "params": params or {}}
        if session:
            msg["sessionId"] = session
        future = asyncio.get_running_loop().create_future()
        self._pending[msg_id] = future
        await self.ws.send(json.dumps(msg))
        reply = await asyncio.wait_for(future, timeout=60)
        if "error" in reply:
            raise RuntimeError(f"{method}: {reply['error']}")
        return reply.get("result", {})

    async def new_page(self) -> str:
        target = await self.send("Target.createTarget", {"url": "about:blank"})
        attached = await self.send("Target.attachToTarget", {"targetId": target["targetId"], "flatten": True})
        return attached["sessionId"]


def redact_headers(headers: dict) -> dict:
    return {k: ("<redacted>" if k.lower() in SECRET_KEYS else v) for k, v in (headers or {}).items()}
