"""Generate brand images through OpenRouter's Image API.

Usage:  python gen.py jobs.json
The jobs file is a list of {"name", "model", "prompt", ...params}. Each job becomes
explore/design/brand/<name>.png (or .svg for vector models). The API key is read from the
perplexity-or MCP server entry in ~/.claude.json and is never printed.
"""
import base64
import json
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from urllib import error, request

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG = os.path.join(os.path.expanduser("~"), ".claude.json")


def api_key() -> str:
    with open(CONFIG, encoding="utf-8") as f:
        return json.load(f)["mcpServers"]["perplexity-or"]["env"]["PERPLEXITY_API_KEY"]


def data_url(path: str) -> str:
    ext = os.path.splitext(path)[1].lstrip(".").lower()
    media = {"jpg": "jpeg", "svg": "svg+xml"}.get(ext, ext)
    with open(os.path.join(HERE, path), "rb") as f:
        return f"data:image/{media};base64,{base64.b64encode(f.read()).decode()}"


def run(job: dict, key: str) -> tuple[str, float, str]:
    name = job.pop("name")
    refs = job.pop("refs", [])
    body = {"n": 1, "stream": False, **job}
    if refs:
        body["input_references"] = [{"type": "image_url", "image_url": {"url": data_url(r)}} for r in refs]
    req = request.Request(
        "https://openrouter.ai/api/v1/images",
        data=json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"},
    )
    started = time.time()
    try:
        res = json.load(request.urlopen(req, timeout=600))
    except error.HTTPError as e:
        return name, 0.0, f"HTTP {e.code}: {e.read()[:400]!r}"
    written = []
    for i, item in enumerate(res["data"]):
        media = item.get("media_type", "image/png")
        ext = {"image/svg+xml": "svg", "image/webp": "webp", "image/jpeg": "jpg"}.get(media, "png")
        suffix = "" if len(res["data"]) == 1 else f"-{i + 1}"
        path = os.path.join(HERE, f"{name}{suffix}.{ext}")
        with open(path, "wb") as f:
            f.write(base64.b64decode(item["b64_json"]))
        written.append(os.path.basename(path))
    cost = float(res.get("usage", {}).get("cost", 0) or 0)
    return name, cost, f"{', '.join(written)} in {time.time() - started:.0f}s"


def main() -> None:
    with open(sys.argv[1], encoding="utf-8") as f:
        jobs = json.load(f)
    key = api_key()
    total = 0.0
    with ThreadPoolExecutor(max_workers=8) as pool:
        for name, cost, note in pool.map(lambda j: run(j, key), jobs):
            total += cost
            print(f"{name}: {note} (${cost:.4f})")
    print(f"total ${total:.4f}")


if __name__ == "__main__":
    main()
