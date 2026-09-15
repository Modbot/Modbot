"""Headless screenshots of the built landing page, in a private browser (the MCP browser is shared).
Usage: python shot.py <out-prefix> [width] [height] [theme] [full]
"""
import sys
from playwright.sync_api import sync_playwright

prefix = sys.argv[1]
width = int(sys.argv[2]) if len(sys.argv) > 2 else 1440
height = int(sys.argv[3]) if len(sys.argv) > 3 else 900
theme = sys.argv[4] if len(sys.argv) > 4 else "light"
full = (sys.argv[5] if len(sys.argv) > 5 else "full") == "full"
url = f"http://127.0.0.1:8765/explore/design/landing.html?theme={theme}"
with sync_playwright() as p:
    b = p.chromium.launch()
    pg = b.new_page(viewport={"width": width, "height": height}, device_scale_factor=1)
    pg.goto(url, wait_until="networkidle")
    pg.wait_for_timeout(1200)
    out = f"../../.playwright-mcp/{prefix}.png"
    pg.screenshot(path=out, full_page=full)
    print("saved", out, pg.evaluate("document.body.scrollHeight"))
    b.close()
