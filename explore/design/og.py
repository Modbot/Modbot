"""Photograph og.html at 1200x630 and write it where the site and the kit keep the share image.

    python build.py og && python og.py

The page loads its fonts from Google Fonts, so this needs the network. Run it whenever the words on
the share image change; a stale card says the old tagline in every Discord message that links the
site.
"""
import os
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

TARGETS = [
    os.path.join(ROOT, "src", "Modbot.Landing", "Web", "public", "og.png"),
    os.path.join(HERE, "brand", "og.png"),
]

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1200, "height": 630}, device_scale_factor=1)
    page.goto("file:///" + os.path.join(HERE, "og.html").replace(os.sep, "/"), wait_until="networkidle")
    page.wait_for_timeout(1200)
    shot = page.screenshot()
    browser.close()

for target in TARGETS:
    with open(target, "wb") as f:
        f.write(shot)
    print("wrote", target)
