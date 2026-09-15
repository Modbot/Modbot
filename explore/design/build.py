"""Build the self-contained landing page: explore/design/landing.template.html -> landing.html.

Every {{asset:<file>}} in the template becomes a data URI for explore/design/brand/<file>, so the
output is one file with no external requests except the Google Fonts link used while iterating
(the real site bundles fonts through @fontsource, see src/Modbot.Landing/Web/src/index.css).
"""
import base64
import mimetypes
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
BRAND = os.path.join(HERE, "brand")


def data_uri(name: str) -> str:
    path = os.path.join(BRAND, name)
    mime = mimetypes.guess_type(path)[0] or "application/octet-stream"
    if name.endswith(".svg"):
        mime = "image/svg+xml"
    with open(path, "rb") as f:
        return f"data:{mime};base64,{base64.b64encode(f.read()).decode()}"


def main() -> None:
    with open(os.path.join(HERE, "landing.template.html"), encoding="utf-8") as f:
        html = f.read()
    used = set()

    def swap(m: re.Match) -> str:
        used.add(m.group(1))
        return data_uri(m.group(1))

    out = re.sub(r"\{\{asset:([^}]+)\}\}", swap, html)
    target = os.path.join(HERE, "landing.html")
    with open(target, "w", encoding="utf-8") as f:
        f.write(out)
    print(f"wrote {target}: {len(out) / 1024:.0f} KB, assets: {', '.join(sorted(used))}")


if __name__ == "__main__":
    main()
