"""Save each banner on banners.html as its own PNG, at the size it is laid out.

Usage:  python build.py banners && python banners.py

Each .banner element is shot on its own, so the file is exactly 1024 x 300 with no page
background around it and nothing to crop afterwards. The id is the file name.

They land in brand/banners/ rather than brand/, which is the checked brand kit: these are made
from it, not part of it.
"""
import os

from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = os.path.join(HERE, "banners.html")
OUT = os.path.join(HERE, "brand", "banners")

# Twice the laid-out size. Discord and the rest downscale a banner to fit, and a 2x file stays
# sharp on a high-density screen where a 1x one does not.
SCALE = 2

def main() -> None:
    if not os.path.exists(PAGE):
        raise SystemExit("banners.html is missing; run: python build.py banners")

    os.makedirs(OUT, exist_ok=True)

    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(
            viewport={"width": 1180, "height": 900}, device_scale_factor=SCALE)
        page.goto(PAGE.replace(os.sep, "/") if PAGE.startswith("/") else "file:///" + PAGE.replace(os.sep, "/"))

        # The mascot is a data URI and the fonts come from Google while iterating; both have to be
        # there before anything is saved, or a banner is shot with fallback type in it.
        page.wait_for_load_state("networkidle")
        page.evaluate("document.fonts.ready")
        page.wait_for_timeout(600)

        saved = []
        for element in page.query_selector_all(".banner"):
            name = element.get_attribute("id")
            target = os.path.join(OUT, f"{name}.png")
            element.screenshot(path=target)
            saved.append(name)

        browser.close()

    print(f"wrote {len(saved)} banners to {OUT} at {SCALE}x:")
    for name in saved:
        print("  " + name)


if __name__ == "__main__":
    main()
