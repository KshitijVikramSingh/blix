"""Fetch (or verify) the lab's HDRIs from Poly Haven, as listed in sources/hdris.json.

    uv run python tools/fetch_hdris.py            # download what is missing, verify every md5
    uv run python tools/fetch_hdris.py --verify   # only check what is there

The manifest banks what is known (which asset, from where, its checksum, what it is for); the bytes stay a fetch, and data/ is
gitignored. The assets are CC0 (https://polyhaven.com/license). Plain urllib, no dependencies.
"""
from __future__ import annotations

import hashlib
import json
import sys
import urllib.request
from pathlib import Path

LAB = Path(__file__).resolve().parents[1]
MANIFEST = LAB / "sources" / "hdris.json"
DEST = LAB / "data" / "hdri"


def md5(path: Path) -> str:
    h = hashlib.md5()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main(verify_only: bool = False) -> int:
    items = json.loads(MANIFEST.read_text())["hdris"]
    DEST.mkdir(parents=True, exist_ok=True)
    bad = 0
    for it in items:
        path = DEST / f"{it['id']}_2k.hdr"
        if not path.exists() and not verify_only:
            print(f"fetching {it['id']} ({it['bytes'] / 1e6:.1f} MB)", flush=True)
            req = urllib.request.Request(it["url"], headers={"User-Agent": "blix-tonemap-lab"})
            with urllib.request.urlopen(req, timeout=120) as r, open(path, "wb") as f:
                f.write(r.read())
        if not path.exists():
            print(f"missing  {it['id']}")
            bad += 1
        elif md5(path) != it["md5"]:
            print(f"BAD md5  {it['id']}")
            bad += 1
        else:
            print(f"ok       {it['id']}  ({it['why']})")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main("--verify" in sys.argv))
