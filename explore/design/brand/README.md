# Modbot brand kit (draft, September 2026)

Everything in this folder was made for the landing page redesign in `explore/design/`. The
rules are written up in `.agent/specs/2026-09-16-brand-design.md`, and the icon set is copied
into each web project's `public/` folder. This folder is where a new asset is generated and
checked before it is copied over.

## Files

| File | What it is | Where it goes |
|---|---|---|
| `reference-mascot.png` | The owner's earlier Midjourney mascot, used as the image reference for every generation below | reference only |
| `mascot-refined-flat.png` / `-512.png` | Full mascot on its cloud, transparent, 1024 and 512 px | hero, OG image, 404 page |
| `mascot-refined-cloud.png` / `-512.png` | Same, with sparkles and a heart | merch, stickers, Discord emoji |
| `mascot-refined-icon.png` / `-512.png` | Head only, front-facing, gradient | app icon source, Discord avatar |
| `mascot-refined-mark.png` / `-512.png` | Head only, three flat colours | favicon source |
| `mascot-nano-flat.png` | Nano Banana Pro's take, opaque white background | comparison only |
| `vector-icon.svg` | Head only, gradient, as vector (Recraft, from the icon PNG) | master vector for the icon |
| `vector-mark.svg` | Head only, flat indigo, as vector (Recraft, from the mark PNG) | favicon, nav mark, footer mark |
| `mark-indigo.svg`, `mark-black.svg`, `mark-white.svg` | The flat mark recoloured for light, print and dark surfaces | single-colour uses |
| `apple-touch-icon.png` | 180 px, opaque lavender background | `public/apple-touch-icon.png` |
| `icon-192.png`, `icon-512.png` | Transparent, purpose `any` | web app manifest |
| `icon-512-maskable.png` | Lavender background, head inside the 80 % safe circle | manifest, purpose `maskable` |
| `discord-avatar-1024.png` | Head on soft off-white, survives Discord's circle crop | bot avatar |
| `og.png` | 1200 x 630 share image, rendered from `../og.template.html` | `public/og.png` |
| `gen.py`, `jobs-*.json` | The generation script and the exact prompts used | rerun to iterate |
| `contrast.py` | WCAG 2 contrast for any pair of hex colours | check new tokens |

Both SVGs came back with a white background path and C2PA metadata; both were removed. Alpha
on every PNG tops out at 254, which renders as opaque.

## Tokens

The violet stays the app's own: `#5b4bd6` on light, `#6d5cf0` on dark. The mascot is the only
place a gradient appears. Neutrals lean lavender so the page and the mark share one temperature.

| Token | Light | Dark | Checked |
|---|---|---|---|
| ground | `#f7f6fb` | `#0f0e15` | text 17.5:1 / 15:1 |
| card | `#ffffff` | `#17151f` | |
| text | `#111113` | `#e6e6eb` | |
| muted text | `#5c6475` | `#a5a3b8` | 5.5:1 / 7.6:1 |
| primary | `#5b4bd6` | `#6d5cf0` | white on it 6.1:1 / 4.7:1 |
| link | `#5b4bd6` | `#a78bfa` | 5.7:1 / 6.9:1 (the dark fill fails as text, so links get their own tint) |
| accent | `#edebff` | `#2a2547` | |
| border | `#e4e2ee` | `#2a2738` | |
| danger / ok / warn / info | `#dc2626` `#15803d` `#b45309` `#0369a1` | `#f87171` `#4ade80` `#fbbf24` `#38bdf8` | all pass 4.5:1 as text on their ground |

Info is sky, so it stays apart from the violet.

## Type

- Display: **Bricolage Grotesque**, weight 640, width 96, optical size automatic, tracking -0.02 em.
  Used for the wordmark, the hero and every h2.
- Body: **IBM Plex Sans**, as the app already uses. Tables and counts keep `tabular-nums`.
- Mono: **IBM Plex Mono** for ids, timestamps and the one setting.

All three are OFL and on Google Fonts. The iteration file links Google Fonts; the real site bundles
through `@fontsource`, so add `@fontsource-variable/bricolage-grotesque` when this ships.

## Mascot rules

1. Two levels: the head-only mark for the nav, favicon, app icon and Discord avatar; the full
   mascot on its cloud for the hero, the share image and the 404 page.
2. Never inside a flat blurple circle or square, which is how the Discord moderation bots draw theirs.
3. One calm expression. No sad, angry or shocked variants, and never beside a ban record,
   evidence, an error or an empty state.
4. The gradient lives in the artwork. Buttons, borders and backgrounds stay flat violet.
5. Keep the single round ear on one side and the cloud in the full silhouette. Those are what
   separate it from BMO, the Booth "TV Head" avatars and the Discord bot robots.
6. At most a quarter of the hero's width, beside a product view.
7. Minimum size 24 px for the mark; clear space of one head-width around it.

## Landing page

`../landing.template.html` is the source. `python ../build.py` inlines the assets into
`../landing.html`, a single self-contained file. The built file and `../og.html` are not
committed, since each is a few hundred kilobytes of base64 and both are one command away. `python ../shot.py <name> [w] [h] [light|dark] [full|top]`
renders it headless into `.playwright-mcp/` (the shared Playwright MCP browser is used by other
agents, so screenshots go through this script). Add `?theme=dark` to the URL to force a theme.

Every claim on the page traces to `src/Modbot.Landing/Web/src/App.tsx`, `README.md`,
`docs/discord-bot.md`, `docs/security.md` or the foundation spec, with two exceptions from the
research: VRChat's ban list has no search and no reason on record (Canny requests), and VRChat
offers no support for third-party use of its API (VRChat's own statement).

## Spend

Image generation through OpenRouter for this kit: about 1 USD (five mascot candidates at
0.38 USD, two vector conversions at 0.60 USD, one smoke test at 0.01 USD).
