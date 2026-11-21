# /// script
# requires-python = ">=3.12"
# dependencies = ["httpx>=0.28"]
# ///
"""Spike：不用帳號、照沒登入的瀏覽器下載個人版 OneDrive「知道連結的人都能看」的分享檔（流程是 onedrive_anonymous.py 錄的）：

1. POST api-badgerp.svc.ms/v1.0/token {"appId": ...}                          → 匿名的 Badger token
2. POST my.microsoftpersonalcontent.com/_api/v2.0/shares/{shareId}/driveitem  （Prefer: autoredeem，兌換分享）
3. GET  .../_api/v2.1/drives/{driveId}/items/{itemId}/content                 → 原檔

檔案存在 spike/.local/，token 不印出來。

    uv run spike/onedrive_download.py <shareId u!...>
"""

import sys
from pathlib import Path

import httpx

LOCAL = Path(__file__).resolve().parent / ".local"

BADGER = "https://api-badgerp.svc.ms/v1.0/token"
# OneDrive 網頁檢視器匿名讀分享用的 app id（抓自網頁版，不是 secret）
APP_ID = "073204aa-c1e0-4e66-a200-e5815a0aa93d"
API = "https://my.microsoftpersonalcontent.com/_api"


def main(share_id: str) -> None:
    with httpx.Client(timeout=30, headers={"Origin": "https://onedrive.live.com", "Referer": "https://onedrive.live.com/"}) as http:
        token = http.post(BADGER, json={"appId": APP_ID}).raise_for_status().json()["token"]
        auth = {"Authorization": f"Badger {token}"}

        item = http.post(
            f"{API}/v2.0/shares/{share_id}/driveitem",
            params={"$select": "id,name,size,file,image,parentReference"},
            headers={**auth, "Prefer": "autoredeem"},
        )
        print("redeem:", item.status_code)
        item.raise_for_status()
        meta = item.json()
        drive = meta["parentReference"]["driveId"]
        print("item:", {k: meta.get(k) for k in ("name", "size", "image")}, "mime:", (meta.get("file") or {}).get("mimeType"))

        content = http.get(f"{API}/v2.1/drives/{drive}/items/{meta['id']}/content", headers=auth, follow_redirects=True)
        print("content:", content.status_code, content.headers.get("content-type"), len(content.content), "bytes")
        content.raise_for_status()
        out = LOCAL / f"onedrive_{meta['name']}"
        out.write_bytes(content.content)
        print("saved:", out)


if __name__ == "__main__":
    main(sys.argv[1])
