# Modbot — Brand Design

- **Date:** 2026-09-16
- **Status:** Applied to the landing page, the web app, my.modbot.co and Modbot Cloud
- **Covers:** the logo, colour tokens, type, corner radii and the rules every screen follows
- **Source material:** `explore/design/brand/` (the generated assets, the prompts, and
  `README.md` with the contrast figures) and `explore/design/landing.template.html` (the page the
  landing site is built from)

---

## 1. What the brand is for

Modbot holds a community's ban records and a VRChat login. The people deciding whether to run it
are group owners and head moderators who write evidence-heavy Canny posts and read hype as a red
flag. The brand therefore does two things at once: the mascot says a serious tool can still be
friendly to a volunteer team, and everything around the mascot stays flat, plain and quiet so the
product reads as trustworthy.

## 2. The logo

The mark is a small retro television drawn as a robot head: a rounded-square casing in a violet
gradient, a white screen face with two dark oval eyes, pink blush and a small smile, two antennae,
and one round ear on its right side. It comes from the owner's earlier concept
(`explore/design/brand/reference-mascot.png`).

Two levels:

| Level | File | Where |
|---|---|---|
| Head only, gradient | `icon-512.png`, `icon-192.png`, `apple-touch-icon.png` | the wordmark in every header and sidebar, favicons, app icons, the Discord bot avatar |
| Head only, flat indigo | `favicon.svg` (`vector-mark.svg` in the kit) | the SVG favicon and any single-colour use |
| Full mascot on its cloud | `mascot.png` (landing only) | the landing hero, the share image, the 404 page |
| Windows icon | `src/Modbot.Companion.App/Assets/Modbot.ico` (and `icon-256.png` for the window's brand row) | the companion's exe, window, tray and installer |
| Discord avatar | `src/Modbot.Discord/Assets/avatar.png` | set by the bot itself when it still has Discord's default avatar; embeds put `/icon-192.png` from the public address beside their footer |

Rules:

1. In a header or sidebar the mark is an `<img>` of `icon-512.png` at 28 px (`size-7`), next to
   the word "Modbot" set in the display face. Never the old violet "M" square.
2. Never place the head inside a flat blue or blurple circle or square. That is how Discord's
   moderation bots draw theirs.
3. One calm expression. No sad, angry or shocked variants. The mascot never appears beside a ban
   record, evidence, an audit log, an error or an empty state.
4. The gradient lives in the artwork only. No CSS gradient anywhere in the interface: buttons,
   borders and backgrounds are flat.
5. Not recoloured, rotated, stretched or redrawn. Minimum 24 px. Clear space of one head-width.
6. At most a quarter of a hero's width, beside a product view.

The AI-generated PNGs are concept art. Before the mark is registered or called "the logo" in
`TRADEMARK.md`, a person redraws the vector from the concept; see `explore/design/brand/README.md`.

## 3. Colour

The violet stays what the app already used, and it is flat everywhere. Neutrals lean lavender so
the interface and the mark share one temperature. Every pair below was checked with
`explore/design/brand/contrast.py`.

| Token | Light | Dark | Role |
|---|---|---|---|
| `--background` | `#f7f6fb` | `#0f0e15` | page |
| `--card` / `--popover` | `#ffffff` | `#17151f` | cards, popovers, the sidebar |
| `--foreground` | `#111113` | `#e6e6eb` | text |
| `--secondary` / `--muted` | `#f1eff8` | `#1e1b29` | quiet surfaces |
| `--muted-foreground` | `#5c6475` | `#a5a3b8` | secondary text (5.5:1 / 7.6:1) |
| `--primary` | `#5b4bd6` | `#6d5cf0` | buttons, focus, active states (white on it 6.1:1 / 4.7:1) |
| `--link` | `#5b4bd6` | `#a78bfa` | link text; the dark fill fails as text on near-black, so links get their own tint |
| `--accent` / `--accent-foreground` | `#edebff` / `#4a3cc2` | `#2a2547` / `#b6acff` | selected rows, the active nav entry |
| `--border` | `#e4e2ee` | `#2a2738` | hairlines |
| `--input` | `#cfcbe0` | `#3a3650` | input borders |
| `--ring` | `#5b4bd6` | `#8f82ff` | focus ring |
| `--destructive` | `#dc2626` | `#f0526a` | ban, delete, errors |
| `--ok` | `#15803d` | `#4ade80` | synced, online |
| `--warn` | `#b45309` | `#fbbf24` | warnings, rate-limit stops |
| `--info` | `#0369a1` | `#38bdf8` | notices; sky, so it stays apart from the violet |

The companion and the SteamVR overlay carry the dark set by value in
`src/Modbot.Overlay/DesignTokens.cs`; `DesignTokenDriftTests` fails when it drifts from the web
stylesheet. Discord embeds keep their semantic colours (green open, red banned) and use the violet
`0x5B4BD6` for a reply that is neither, never Discord's blurple.

The five chart series colours and the density tokens (`--row-h`, `--control-h`, `--text-base`,
`--text-small`, `--hairline`) are unchanged: they were validated as a set and the brand does not
touch them.

No hex value appears in a component. A colour that is not a token is a bug.

## 4. Type

| Role | Face | Where |
|---|---|---|
| Display | **Bricolage Grotesque** (variable, `@fontsource-variable/bricolage-grotesque`) | the wordmark, page titles, landing headlines. Weight 640, `font-variation-settings: "wdth" 96`, `font-optical-sizing: auto`, tracking -0.02 em at hero sizes and -0.01 em at title sizes |
| Body | **IBM Plex Sans** | everything else, as before; `tabular-nums` everywhere |
| Mono | **IBM Plex Mono** | ids, timestamps, log lines, settings values |

Fonts are bundled through `@fontsource`. Nothing is fetched from a font service, in any project.
The `--font-display` token exposes the display face; the utility class `display` (landing) or
`font-display` (apps) applies it.

Non-Latin display names fall through to a system face:
`"IBM Plex Sans", "Hiragino Sans", "Yu Gothic UI", "Meiryo", "Malgun Gothic", system-ui, sans-serif`.

## 5. Shape

| Element | Radius | Tailwind |
|---|---|---|
| Buttons, inputs, tabs, menu rows, badges with text | `--radius` (0.375 rem) | `rounded-md` |
| Cards, panels, dialogs, popups, code blocks, product windows | 0.75 rem | `rounded-xl` |
| Pills, dots, avatars, counters | full | `rounded-full` |

No `rounded-lg` or `rounded-2xl` on new work. Borders are one hairline (`var(--hairline)`), and
cards on the landing page carry one soft shadow (`--shadow`); inside the apps, cards carry
`shadow-sm` at most. Nothing glows.

## 6. Writing on a screen

The project's existing rules hold: controls, not explanations (`CLAUDE.md`); plain words a
sixteen-year-old moderator uses; no superlatives, no counters, no "coming soon". State labels are
fragments ("Ticket invalid"), body text is full sentences, and nothing is ever written with an em
dash.

## 7. Where the assets live

| Project | Public files |
|---|---|
| `src/Modbot.Landing/Web/public` | `favicon.svg`, `icon-192.png`, `icon-512.png`, `apple-touch-icon.png`, `og.png`, `mascot.png` |
| `src/Modbot.Web/public` | `favicon.svg`, `icon-192.png`, `icon-512.png`, `apple-touch-icon.png` |
| `src/Modbot.My.Web/public` | same |
| `src/Modbot.Cloud/Web/public` | same |
| `docs/public` | same, plus `og.png`; the docs site maps these tokens onto Fumadocs' `--color-fd-*` variables in `docs/app/global.css` |

There is one share image. `og.png` is 1200 x 630, rendered from `explore/design/og.template.html`,
and it is the picture in every link preview the project draws. The landing page and the docs site
serve their own copy; my.modbot.co, Modbot Cloud and the moderator app point at
`https://modbot.co/og.png` instead of carrying a copy each, so the picture is the same one
everywhere and a self-hosted Modbot needs no public address of its own to have a preview at all.

The kit, the prompts and the generation script are in `explore/design/brand/`.
