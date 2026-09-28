# Modbot on a phone — review and design proposal

- **Date:** 2026-09-28
- **Build looked at:** `origin/staging` at `7b08cda0`, running on localhost:8080 (the test group). Pages
  that were empty there (Live, Stats) were also looked at, read-only, on a demo stack on 8090 that
  another session already had running.
- **How:** Claude in Chrome. The window cannot be made narrower than 540 px, so each page was loaded in
  a same-origin frame sized **390×844**, **360×800** and **844×390**. Chrome on a PC reports a mouse,
  so the phone's `(pointer: coarse)` rule (`index.css:217`) was copied into the frame to get the phone's
  44 px controls and type sizes. Sideways scroll, tap-target and position numbers were measured in the
  page with JavaScript.
- **Not seen:** a real on-screen keyboard. It was imitated by shrinking the frame to 480 px tall.
  Flags, Reviews and Requests had **no rows** in either group, so their row layouts come from the code.
- **Earlier reviews read:** `2026-09-25-ux-review`, `2026-09-26-analytics-ux-review`,
  `2026-09-26-instances-page-review`, `2026-09-27-{discord-page,settings,site-adversarial,vrchat-page,worlds-page}-review`.
  Nothing below repeats a finding those reviews got fixed.

---

## 1. Problems, ranked

"How bad" is for a moderator on a phone: **High** = the job can't be done or the main thing is
hidden. **Med** = it works but costs scrolling or guessing. **Low** = cosmetic, or a rare page.

| # | Page | Problem | How bad | Size |
|---|---|---|---|---|
| 1 | Audit log | The **What happened** column starts at x = 389 px on a 390 px screen. A phone shows the time and the source of every entry, and none of the entries. | High | M |
| 2 | Calendar | Tapping an event opens its details **half off the left edge** (left = −175 px). The title and the left half of Edit / Cancel event / Delete are cut off. New-event popover is built the same way. | High | S |
| 3 | Bans, Requests | Row buttons sit in the last column of a sideways table: **Unban / Case file** start at x = 556 px; **Approve / Reject** are the 4th column after a pinned name, Asked and History. | High | M |
| 4 | Person popup | Profile fills the first screen. The tab row sits at y ≈ 620–680 of 844, and a picked tab shows about 50 px of content. The tab body is a **scroll area inside a scrolling popup**. The 6th tab (Flags) is off the right edge. The name is shown twice. | High | M |
| 5 | Person popup, landscape | At 844×390 the popup switches to the two-column desk layout (it switches at `md`, by width). The panes are 249 px and 204 px tall, and **Kick / Ban sit at y = 596, below the fold** of the left pane. The pinned foot bar is hidden. | Med | S |
| 6 | Discord members, People (member views), Team, Instances | Tables of 6–12 columns. A phone shows 2–4 of them: Team's *Actions per moderator* shows Moderator, Total, Instance kicks and Warns out of 12. | Med | M–L |
| 7 | Live | Each open instance has a **242 px world picture**. With 4 instances that is 968 px of pictures, over a screen, and the page is 4.2 screens long. | Med | S |
| 8 | Actions sheet, Search | Both open as **centered dialogs**, away from the thumb that tapped the bottom bar. Search sits at 12 vh with a 70 vh max height, so a long result list runs under the keyboard (from the code, not seen). | Med | M |
| 9 | Calendar, Week | 7 columns of about 38 px: day labels overlap ("\| 28 ' 29 \ 30") and events read "M…". Schedule is the default on a phone, so this is only when Week is picked. | Med | S–M |
| 10 | Stats › Activity | **8.6 screens** long. Nine tiles with orphan rows (3+2, 3+1) fill the first half screen before any chart. | Med | M |
| 11 | Discord page | The header's tab row **scrolls sideways** and cuts "Bans" to "Ban". The VRChat page's row wraps (`GroupHeader.tsx:127` has `wrap`; `ServerHeader.tsx:131` doesn't). | Low | S |
| 12 | Settings › People and roles | The account sheet (`Users.tsx:369`, `z-30`) sits under the bottom bar (also `z-30`, later in the page). The bar covers its bottom 53 px. Nothing is hidden today, but a longer sheet would lose its last button. | Low | S |
| 13 | Search | A world result shows its whole `wrld_…` id and squeezes the world's name to one letter ("M"). | Low | S |
| 14 | Audit log, People | The filter chips and **Clear** take two rows (about 110 px) above the list. | Low | S |
| 15 | Many | Small plain `<button>`s the phone CSS doesn't reach: filter-chip ✕ (~24 px wide), Copy id (20 px tall), chat "More" (~22 px), RankedList sort buttons, JsonView toggles. | Low | S |
| 16 | Modbot's log | The word "Information" takes 80 px, and each message is cut after ~20 characters. | Low | S |

From the code only, not seen: the RankedList row (`charts/RankedList.tsx:35-52`) has fixed widths
of 144 + 64 + 112 px. With a note (Discord's busiest channels), its bar would shrink to nothing on a
360 px phone. The demo's channels had no notes, so this couldn't be checked.

---

## 2. The big picture

### Five rules for the phone

```
1. One column, one scroll.
   Never a scroll area inside a scroll area.
2. Nothing important to the right.
   A row shows its name, its facts
   and its buttons without a swipe.
3. Actions at the bottom.
   What the thumb taps is in the
   bottom third of the screen.
4. Sheets come up from the bottom.
   Dialogs, menus and pickers rise
   from the bar the thumb just used.
5. Pictures shrink, facts don't.
   A banner or world picture is a
   strip on a phone, never a screen.
```

### Navigation: keep the bottom bar

The bar works: Menu · Search · Now · Actions, each at least 44 px, with safe-area padding. Actions
only shows on pages that have their own actions. The one real choice is **Menu**:

```
 A) today: drawer from    B) sheet from the bottom
    the left                 grid of pages
┌──────────────┬──────┐  ┌────────────────────────┐
│ [banner]   ✕ │      │  │ ...page, dimmed...     │
│ 🔍 Search     │      │  ├────────────────────────┤
│ Now          │      │  │  ───                   │
│ Chat         │      │  │ Now    People  Live    │
│ Stats        │      │  │ Flags³ Reviews Requests│
│ ─Community── │      │  │ Bans   Audit   Calendar│
│ Requests     │      │  │ VRChat Discord Stats   │
│ People       │      │  │ Chat   Giveaways Log   │
│ …17 rows,    │      │  │ Settings               │
│ 1.7 screens  │      │  │ ● all working   ☾  👤  │
└──────────────┴──────┘  └────────────────────────┘
```

| | A) Keep the drawer | B) Bottom-sheet grid |
|---|---|---|
| Reach | Top items are out of thumb reach | Everything is in the bottom half |
| Fits | 1.7 screens, scrolls | One screen, no scroll |
| Group headings | Kept | Lost, or shown as small row labels |
| Cost | Nothing | New component; the drawer stays for tablets |

**Recommendation: A for now.** It works, and the pages that hide their content matter more. B is a
nice-to-have (chip 9).

### Tables become two-line rows

People already does this (`People.tsx:519-568`, `data-layout="wide|narrow"`, `index.css:368-388`)
and it is the best screen on a phone. Make it the one pattern for every list a moderator works in:

```
BEFORE (Bans, 390 px)          AFTER
┌─────────────┬──────────┬┄┄   ┌──────────────────────────┐
│ Person      │Banned on │Se┆   │ (●) Frostotter   [Unban] │
├─────────────┼──────────┼┄┄   │     User · May 16 · 📁 1  │
│(●)Frostotter│ May 16   │Ma┆   ├──────────────────────────┤
│(●)Staticnewt│ May 11   │Ma┆   │ (●) Staticnewt_vr [Unban]│
│(●)Lanternmin│ Feb 14   │Fe┆   │     May 11                │
└─────────────┴──────────┴┄┄   └──────────────────────────┘
  Unban, Case file: x = 556 px   line 1: name … button
  (a swipe to the right)         line 2: the facts, muted
```

- **Line 1:** picture, name, one badge and the row's own button (or two), on the right.
- **Line 2:** the other columns as one muted line, joined with `·`, in column order.
- The whole row still opens the person (it does today, 46 px tall).
- Wide screens keep the table exactly as it is.

Options for **analytics tables** (Team: 12 columns, Instances: 5):

| | A) Two-line rows | B) Short table + tap to expand |
|---|---|---|
| What shows | Name + 2–3 numbers per row | 3 columns; tapping a row opens the rest under it |
| Good for | Instances, Worlds (already done) | Team's 12 counts |
| Cost | Pick the numbers per table | One shared "expand" row |

**Recommendation:** A for Instances and Discord members, B for Team.

### Wide charts

These already work: `ResponsiveContainer`, the heatmap thins its labels, and stat tiles are 3
across. Two changes:

- **Tiles:** a strip with an orphan (3+1, 3+2) becomes 2 across, or drops its least-used tile on a
  phone. Don't shrink the text further; the labels are already 11 px.
- **Long pages:** Stats › Activity (8.6 screens) gets a row of section jumps under the range picker.
  Labels only, no captions:

```
┌──────────────────────────┐
│ 7d  [30d]  90d  All      │
│ Instances · Heatmap ·    │
│ Worlds · Discord         │  ← each jumps to its heading
├──────────────────────────┤
```

### Dialogs: sheets from the bottom

Today every dialog is centered (`ui/dialog.tsx:48-52`), and its buttons scroll away with the body
(`ConfirmDialog.tsx:101`, `ModerationActions.tsx:246-300`). Only the person popup pins a foot.

```
BEFORE (Actions)               AFTER
┌──────────────────────────┐   ┌──────────────────────────┐
│ People                   │   │ People                   │
│ ░░░░░░░░░░░░░░░░░░░░░░░░ │   │ ░░░░░░░░░░░░░░░░░░░░░░░░ │
│ ┌──────────────────────┐ │   │ ░░░░░░░░░░░░░░░░░░░░░░░░ │
│ │ Actions            ✕ │ │   │ ░░░░░░░░░░░░░░░░░░░░░░░░ │
│ │ Add a filter         │ │   │ ┌──────── ─── ─────────┐ │
│ │ ✓ Most recently seen │ │   │ │ Actions            ✕ │ │
│ │   Newest joiner      │ │   │ │ Add a filter         │ │
│ │   By name            │ │   │ │ ✓ Most recently seen │ │
│ └──────────────────────┘ │   │ │   Newest joiner      │ │
│ ░░░░░░░░░░░░░░░░░░░░░░░░ │   │ │   By name            │ │
├──────────────────────────┤   ├─┴──────────────────────┴─┤
│ Menu  Search  Now Actions│   │ Menu  Search  Now Actions│
└──────────────────────────┘   └──────────────────────────┘
  opens mid-screen               rises from the thumb
```

- **One change in `DialogContent`:** below `md` on a phone, anchor to the bottom, full width, rounded
  top corners, `max-h: 90dvh`, body scrolls, and **the button row is pinned at the foot** (the `foot`
  slot `dialog.tsx:83` already has).
- **Full screen stays full screen:** the person popup and the event editor.
- **Popovers become sheets on a phone:** the calendar's event details and New event (#2), and the
  filter picker.

The keyboard is the real choice here:

| | A) `interactive-widget=resizes-content` in the viewport tag | B) Track `visualViewport` height in a CSS variable |
|---|---|---|
| Android Chrome | Page shrinks above the keyboard; `dvh` follows | Works |
| iOS Safari | Ignored | Works |
| Cost | One line | A small hook, and every sheet uses the variable |

**Recommendation:** A and B together. Check both on a real phone before relying on them: this
review couldn't open a real keyboard.

### Sticky actions

| Where | Today | Proposal |
|---|---|---|
| Person popup | Note · Kick · Ban pinned at the foot ✓ | Keep. Also pin it in landscape (#5) |
| Confirm / ban / kick | Buttons at the end of the body | Pinned foot (the sheet change above) |
| Requests, Bans, Flags, Reviews | Buttons in a far column | On the row, line 1, right side |
| Calendar event | In a popover, half off-screen | Sheet with the buttons pinned |

---

## 3. The worst pages, before and after

### #1 Audit log

```
BEFORE (390 px)                AFTER
┌──────────────────────────┐   ┌──────────────────────────┐
│ Audit log                │   │ Audit log                │
├──────────────────────────┤   ├──────────────────────────┤
│[Source is any of Com…][✕]│   │ [Filter ²]      [Clear]  │
│[About is ~Zuel…][✕][Filt]│   ├──────────────────────────┤
│ Clear                    │   │ Zuelatak banned          │
├──────────────────────────┤   │ FenyaKrautCutie          │
│   When          Source   │   │ ■ Manual · Sep 27, 2:57AM│
│ › Sep 27, 2:57 AM ■Manual│   ├──────────────────────────┤
│ › Sep 27, ~2:57 A ■Sync  │   │ FenyaKrautCutie joined   │
│ › Sep 26, ~2:22 P ■Sync  │   │ ■ Sync · Sep 27, ~2:57AM │
│ › Sep 25, 8:22 PM ■Compan│   ├──────────────────────────┤
│   (What happened: x=389, │   │ 3 people arrived in …    │
│    off the screen)       │   │ ■ Companion · Sep 25     │
└──────────────────────────┘   └──────────────────────────┘
```

- The sentence is line 1, because it's what the page is for. Source and time are line 2, muted.
- Tapping the row still expands the entry's details below it (the `›` today).
- On a phone the filter chips fold into **Filter ²** (the number = filters on). Tapping it opens the
  filter sheet with the chips inside.
- Evidence: `AuditLog.tsx:391-398` (4 columns, no `pinFirst`), `:526` (`min-w-[20rem]` sentence cell).

### #2 Calendar event

```
BEFORE (tap an event)          AFTER
┌──────────────────────────┐   ┌──────────────────────────┐
│ Mon, Sep 28              │   │ Mon, Sep 28              │
│┄┄┄┄┄┄┄┄┐ ▮ Made on VRCh…  │   │ 9:48 PM ▮ Made on VRCh…  │
│      ✕ │                 │   │ ░░░░░░░░░░░░░░░░░░░░░░░░ │
│· 9:48 PM – 11:48         │   ├──────────  ───  ─────────┤
│        │                 │   │ Made on VRChat test    ✕ │
│ancel event│ Delete│      │   │ Scheduled · VRChat       │
│┄┄┄┄┄┄┄┄┘                 │   │ Mon, Sep 28 · 9:48–11:48 │
│  left edge at −175 px    │   │ Discord event: Published │
│                          │   │ [Edit] [Copy] [Cancel ev]│
└──────────────────────────┘   └──────────────────────────┘
```

- Cause: `EventDetails.tsx:241-249` and `QuickCreate.tsx:59-64` place the popover `side="right"` of
  the tap point. When neither side has 352 px, Radix flips it but can't slide it sideways, so it
  hangs off the edge.
- Fix: on a phone, open the existing dialog branch (`EventDetails.tsx:218`, used when there's no
  `spot`) as a bottom sheet. Do the same for New event.
- Week view (#9): on a phone, show **3 days** instead of 7, or hide the day names and keep only the
  dates.

### #3 Bans and Requests

```
REQUESTS, BEFORE (from code)   AFTER
┌─────────────┬──────┬┄┄┄┄┄   ┌──────────────────────────┐
│ Person      │Asked │Hist┆   │ (●) kit-stoat            │
├─────────────┼──────┼┄┄┄┄┄   │ Sep 27 · banned before   │
│(●)kit-stoat │Sep 27│Ban…┆   │        [Reject] [Approve]│
│(●)Driftswif │Sep 27│ —  ┆   ├──────────────────────────┤
└─────────────┴──────┴┄┄┄┄┄   │ (●) Driftswift01         │
  Approve / Reject: 4th        │ Sep 27                   │
  column, past the edge        │        [Reject] [Approve]│
                               └──────────────────────────┘
```

- Requests uses a third line for its two buttons: two 44 px buttons and a name don't fit on one line
  at 360 px.
- Evidence: `Requests.tsx:176-236` (the buttons in the last `<Td>`), and Bans' Actions column at
  x = 556 px (measured).

### #4 Person popup

```
BEFORE (390 px, a member)      AFTER
┌──────────────────────────┐   ┌──────────────────────────┐
│ FenyaKrautCutie  ⋯     ✕ │   │(●) FenyaKrautCutie  ⋯  ✕ │
│ ■Trusted  PC  usr_da80…⧉ │   │    ■Trusted 18+ VRC+ PC  │
│[Ban lifted][1 action…]   │   │ [Ban lifted] [Case files]│
├──────────────────────────┤   ├──────────────────────────┤
│ ████ banner 130 px ████  │   │Overview Activity Notes Ca│← sticky
│  (●)                     │   ├──────────────────────────┤
│ FenyaKrautCutie alco/…   │   │ Membership               │
│ ■Trusted VRC+ English PC │   │ Member since Sep 27      │
│ Gonzo's Place Staff      │   │ Banned Sep 27, lifted    │
│ Last refreshed 11s ago   │   │ Profile                  │
│ ☐ Not linked to Discord  │   │ Bio …                    │
│ Membership               │   │ (one scroll for all)     │
│ Member since Sep 27 …    │   │                          │
│Overview Activity Notes P→│   │                          │
│ Profile   (scroll inside)│   │                          │
├──────────────────────────┤   ├──────────────────────────┤
│ [Note]         [Kick][Ban]   │ [Note]         [Kick][Ban]
└──────────────────────────┘   └──────────────────────────┘
  tabs at y≈680 of 844           tabs at y≈130
```

- The name, picture and badges go in the header once. The second copy in the left column is hidden
  on a phone.
- The banner is a strip of about 48 px, or dropped on a phone.
- Tabs stick under the header, and there is one scroll: `PopupFrame` (`subject/shared.tsx:283-305`)
  loses the inner `overflow-auto` of the tab body on a phone.
- "Not linked", Membership and the profile block move into **Overview**, so the first screen is
  tabs + content.
- Tabs that don't fit wrap to a second row, as the VRChat page's do (#4: Flags is off the edge today).
- **Landscape (#5):** pick the layout by *phone* (coarse pointer, `max-height` under ~500 px), not
  only by width, so a phone on its side keeps the one-column popup with the pinned foot.

### #7 Live

```
BEFORE                         AFTER
┌──────────────────────────┐   ┌──────────────────────────┐
│ ┌──────────────────────┐ │   │ ┌──┐ Neon Yard     7/60  │
│ │                      │ │   │ └──┘ Group · 🇪🇺          │
│ │   world picture      │ │   ├──────────────────────────┤
│ │   242 px             │ │   │ kit-stoat ■Companion 9:13│
│ │ Neon Yard 7/60 Group │ │   │ Driftswift01 ■Known  9:15│
│ └──────────────────────┘ │   │ …                        │
│ Here now               7 │   ├──────────────────────────┤
│ kit-stoat  ■Comp… 09:13  │   │ ┌──┐ The Long Porch 14/40│
│ Driftswift01 ■Kno 09:15  │   │ └──┘ Group · 🇺🇸          │
└──────────────────────────┘   └──────────────────────────┘
  4 instances = 968 px of        a 48 px thumbnail
  pictures (4.2 screens)         (about 1.9 screens)
```

- Evidence: `Live.tsx:254-269` stacks `InstanceTile` full width below `sm`, and `InstanceCards.tsx:90`
  sets `aspect-[4/3]`.
- Tapping the header row still opens the instance's popup, as the tile does now.

---

## 4. Already good on a phone

- **No page scrolls sideways as a whole**, at 360 or 390 px: Now, People, Calendar, Live, VRChat,
  Discord, Bans, Audit log all measured `scrollWidth = width`.
- **Bottom bar:** Menu · Search · Now (with its count) · Actions. Every button is at least 44 px,
  with safe-area padding, and Actions shows only where a page has actions (`Chrome.tsx:392-416`).
- **Phone sizes:** controls are 44 px, and inputs are 16 px so iOS doesn't zoom in on focus
  (`index.css:217-271`).
- **People:** two-line rows on the "all" view (`People.tsx:519`). Whole rows tap, 46 px tall.
- **Person popup:** full screen with **Note · Kick · Ban pinned at the foot**.
- **Ban dialog:** fits, and at 480 px tall (keyboard-sized) it scrolls with Cancel and Ban still
  reachable.
- **Calendar** opens on Schedule on a phone, and Month hides times below `sm`.
- **Settings** is a list you tap into, with a back button below `lg`.
- **VRChat page:** the header stacks, and the tab row wraps to three rows so none are hidden.
- **Charts** resize, the heatmap fits 24 columns at 390 px, and stat tiles are 3 across.
- **Stats › Worlds** and **Chat** have their own phone layouts: a narrow list, and conversations in a
  drawer.
- **Search** (the command palette) finds people and worlds with pictures and a Member mark.

---

## 5. Suggested chips and order

| Chip | Fixes | Files (main) | Waits for |
|---|---|---|---|
| **A** Two-line rows as a shared pattern + Audit log | 1, 14 | `ui/data-table.tsx`, `index.css`, `AuditLog.tsx` | — |
| **B** Rows with their buttons: Bans, Requests, Discord members, People member views | 3, 6 (lists) | `Bans.tsx`, `Requests.tsx`, `DiscordMembers.tsx`, `People.tsx` | A |
| **C** Analytics tables on a phone: Team, Instances | 6 (analytics) | `MyTeam.tsx`, `InstanceTable.tsx` | A |
| **D** Calendar on a phone: event sheet, New event, Week | 2, 9 | `EventDetails.tsx`, `QuickCreate.tsx`, `TimeGrid.tsx`, `Calendar.tsx` | — |
| **E** Person popup: one scroll, sticky tabs, landscape, Copy id size | 4, 5, 15 (Copy id) | `subject/shared.tsx`, `PersonPopup.tsx`, `subject/*` | — |
| **F** Sheets from the bottom + keyboard | 8, 12, 13 | `ui/dialog.tsx`, `ShortcutSheet.tsx`, `CommandPalette.tsx`, `ConfirmDialog.tsx`, `moderation/ModerationActions.tsx`, `Users.tsx` | E (both change how `DialogContent` is sized) |
| **G** Live's compact instances | 7 | `Live.tsx`, `InstanceCards.tsx` | — |
| **H** Small fixes: Discord tab row wraps, small tap targets, log level | 11, 15, 16 | `ServerHeader.tsx`, `FilterBar.tsx`, `Conversations.tsx`, `RankedList.tsx`, `JsonView.tsx`, `Logs.tsx` | A (FilterBar changes near the Audit log's) |
| **I** Stats › Activity shorter: tile strips, section jumps | 10 | `pages/analytics/*`, `index.css` tiles block | A (`index.css`) |
| **J** Menu as a bottom-sheet grid (optional) | — | `Chrome.tsx`, `App.tsx` | F |

A, D, E and G can run at the same time.
