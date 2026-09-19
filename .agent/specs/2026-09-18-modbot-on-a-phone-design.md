# Modbot on a phone design

**Date:** 2026-09-18
**Status:** built, except where §9 says otherwise

How the web app works on a screen 360 to 430 pixels wide, held in one hand, with no keyboard and
no mouse.

---

## 1. Why this is not optional

A VRChat moderator is not at a desk when they are needed. The instance fills up at midnight, the
report comes through Discord on a phone, and the question — *who is this person, have we banned
them before* — has an answer in Modbot and nowhere else.

Before this, the answer was unreachable. The app shell was a grid of two columns, `13.5rem` and
the rest. On a 390px screen the sidebar took 216px and the page got 174, and the whole document
scrolled sideways, so a moderator could open Modbot on their phone and not read one row of it.

---

## 2. One app, not two

**A responsive single app.** Four reasons, in the order they decided it.

**The app already has a density system that is exactly the right mechanism.** `index.css` carries
three densities from one token set, and the comment at the top of that file already argues the
case for VR: low pixels-per-degree, an imprecise pointer, targets past the fat-finger threshold.
A phone is the same argument with a different pointer. A separate mobile build would have to
restate all of it and then drift from it.

**The convention is already there.** Fifty files use `sm:`/`md:`/`lg:`; the subject popup already
stacked to one column at `md`; every table already sat in an `overflow-x-auto` box. The work was
not to introduce responsiveness — it was to finish it and fix the shell, which had none.

**Two versions means two versions of every future screen.** Modbot has 222 source files and is
built by several people over time. A second copy of the member list would be correct on the day it
was written and wrong a month later.

**A separate mobile version invites cutting.** The temptation in a phone-only build is to drop
columns, drop pages, drop the audit log. The rule in §5 is that nothing is dropped, and the
easiest way to keep that rule is to have only one thing to keep it in.

---

## 3. Where the line is

Two breakpoints do nearly all the work, and they mean different things.

| | |
|---|---|
| `lg` (1024px) | **The shell.** Below it there is no sidebar column: navigation is a sheet and there is a bar at the foot of the screen. |
| `md` (768px) | **The content.** Below it a table cannot fit its columns and the subject popup cannot fit two of them. |

They are deliberately different. Between 768 and 1024 — a tablet, a half-width window — the pages
are wide enough to lay out properly but too narrow to also give up a fixed column to navigation.

A third condition is not a width at all: `(pointer: coarse) and (max-width: 63.9375rem)` selects a
phone or tablet being touched, and only that raises the touch floor. A touch laptop has a coarse
pointer *and* a mouse, and an operator who chose Dense on it meant Dense.

---

## 4. The shell

**The sidebar becomes a sheet, drawn from the same component.** `Sidebar` gained a `className` and
a `footer` slot; `NavSheet` puts it in a left-anchored dialog. There is one list of pages, one set
of permission checks, and one place a page is added.

**A bar at the foot, not a bar at the top.** A phone held in one hand puts the top of a tall screen
out of a thumb's reach, and the top is where a desktop app puts its controls. Three of them:

| | |
|---|---|
| **Menu** | The pages, the status rows, and — because the top bar's controls cannot fit beside a title at this width — density, theme, your account and sign out at its foot. |
| **Search** | The command palette: people, Discord people and worlds by name, every page, and the actions this screen can take. |
| **Actions** | What this screen can do. See §6. |

**`dvh`, never `vh`.** A phone browser's own bars are inside `100vh`, so a `100vh` shell is taller
than the screen and its last row sits underneath them. Every full-height measurement in the app is
`dvh` now, including the ten centred landing pages a member is sent a link to.

**`viewport-fit=cover` and `env(safe-area-inset-bottom)`.** Without the first, the second is always
zero and the bar at the foot sits under an iPhone's home indicator.

---

## 5. Tables: scroll, never drop

**A table that drops its most useful column on a phone is worse than one that scrolls.** A members
row is a name, a Discord account, roles, two dates and the actions, and none of those is spare.
So the tables keep every column and scroll sideways, which they already did.

Two things were wrong with that.

**The scroll escaped its box.** Every wrapper is `overflow-x-auto`, which clips — but only what it
is the containing block for. The empty `<span class="sr-only">Actions</span>` in a header cell is
absolutely positioned, so with a `position: static` wrapper it escaped the scrolling box entirely
and landed a screen's width off the right of the page; the whole document then scrolled to reach a
one-pixel invisible label. The wrappers are `relative`, which `ui/table.tsx` already was and the
hand-written tables were not.

**Scrolling right lost the name.** Reading the "Last seen" column meant the name had gone off the
left and the row was anonymous. The first column stays put instead: `data-pin-first` on the box
that scrolls, `position: sticky` on each row's first cell, below `md` only.

Marked only where the first column is what *names* the row — Members, both ban tables, Discord
members, repeat offenders, worlds, the instance table, My Team and the rate-limit buckets. Not the
audit log, whose first column is a chevron: pinning that would cost a third of the screen and say
nothing.

The pinned cell repeats the row's own background, including its hover and selected states, because
a see-through cell would show the row sliding underneath it. It is capped at `min(14rem, 48vw)`: a
VRChat id is forty characters with nothing to break at, so uncapped the name column claimed the
whole screen and pinned the very thing the moderator was scrolling past. Capped, it clips to an
ellipsis the way a long name already does at any width, and the row opens the person, where the id
is written out in full.

Header cells and date cells are `whitespace-nowrap`. "Sep 17, 2026" over two lines and "Last seen
by Modbot" over four doubled the height of every row in a list of 352; a shorter row is worth more
than a narrower table.

---

## 6. The keyboard has a tap path

The keys are a real part of this app. `o` on the audit log opens the person a row is about, and
nothing else on that screen does; `j`, `k` and Enter are how a list is read; `f` is the filter bar.
A phone has none of them.

It did not need a second design. Every shortcut is registered with a **label** and a **group**
(`lib/shortcuts.ts`), and the sheet on `?` was already reading that registry rather than a fixed
table. So:

- **Each row in the sheet runs its shortcut** and closes, the same order the command palette uses.
  On a keyboard this is a bonus; on a phone it is the only way.
- **The bar's third control opens that sheet**, titled *Actions* and with the "Go to" group left
  out — going to another page is what Menu is for.
- **The palette lists the same actions** under "On this page", which it already did.

Nothing a key does is reachable only by pressing it. Because the list is read from the registry, a
page that adds a key gets a tap path for it without anyone remembering to add one.

---

## 7. Touch targets

The numbers come from the density tokens, beside VR's, with their reasons written next to them.

| | Phone | VR | Why they differ |
|---|---|---|---|
| `--control-h` | 44px | 48px | A finger touches the thing it is aiming at. A laser pointer wobbles over a distance, so VR needs more; 48 on a phone would cost a row of the list. |
| `--row-h` | 46px | 56px | A list row is a tap target too. |
| `--text-base` | 14px | 18px | 13px is a scanning size for a mouse. Headset text below 16px is mush; a phone's is not. |
| `--hairline` | 1px | 2px | A phone screen is high-DPI. A 1px border does not vanish on one. |

**The floor is applied in one place, not in each component.** A button has eight sizes, and beside
it are an input, a dropdown, a tab and a dozen hand-written toggles; a phone wants all of them at
one height, not thirteen opinions about it. So `index.css` sets `min-height: var(--control-h)` on
`[data-slot="button"]`, `[data-slot="input"]`, `[data-slot="select-trigger"]`, `[role="option"]`,
`textarea` and text inputs, and `min-width` as well on icon buttons, which have no label to widen
them. `min-height` rather than `height`, so a control that is already taller is left alone.

A tick box is 16px of glass, so it grows a little and the label row it sits on takes the floor —
the row is what a finger aims at.

**Fields are 16px on a phone.** Safari zooms the page when a field with smaller text takes focus,
and never zooms back out.

---

## 8. Hover

There is no hover on a touch screen, so anything that only appears on hover has to have another
way in. A sweep of the whole tree found:

**No hover-only controls.** The three hover-reveals in the app — the copy button on a code block,
the delete button on a conversation row, the action bar on a chat turn — already pair
`group-hover` with `[@media(hover:none)]:opacity-100` and a focus fallback. Nothing to do.

**`title` attributes carrying supplementary detail**, about thirty-five of them: an id behind a
name, an exact timestamp behind a relative one, a full location behind a truncated one. These are
left as they are, deliberately. In every case the fact behind the `title` is also on the record the
row opens, which is one tap away — the popup states a person's id, an instance's location and a
fact's exact time in full. A moderator is never stuck; they take one more tap.

**Chart tooltips** work on touch: recharts handles `touchmove`, so dragging along a chart reads out
its values. A tap without a drag does not, which is the usual behaviour of a chart on a phone.

**`title` on a disabled control**, saying why it is disabled, is the one case with no answer — on a
phone it is a dead button with no reason. It is listed in §9 rather than fixed here, because the
fix is visible explanatory text and CLAUDE.md forbids that without being asked.

---

## 9. What was left

- **`pages/People.tsx` and `components/filters/FilterBar.tsx`** were being changed by another
  contributor and were not touched. Two things there: `People`'s table wrapper is the last one
  without `relative` (harmless today, because that table has no `sr-only` header cell to escape,
  and a trap for whoever adds one), and `FilterBar`'s `<span className="flex-1" />` spacer becomes
  a full-width blank row when the bar wraps, which pushes the search box into the middle of a line
  on a phone.
- **`components/moderation/ModerationActions.tsx`**, **`UserProfileCard.tsx`**,
  **`ProfileBadges.tsx`** and **`ProfileHeader.tsx`**, same reason. Nothing looked wrong on a phone
  in any of them.
- **The staff-account drawer** (`pages/Users.tsx`) is a full-screen overlay on a phone with no
  backdrop and no swipe out — only its Close button. It is an administrator's screen, rarely
  reached from a phone.
- **Reason text on a disabled control** (§8), in Users, Roles, the case file, giveaways, the
  calendar, members and the evidence gallery.
- **Chart heights** are fixed pixels with no phone variant. 160px reads acceptably at 360px wide
  and changing it means changing the label-collision thresholds that are computed against it.
- **The audit log's first column is not pinned**, so scrolling its detail rows sideways loses the
  time. Its sentences wrap rather than scroll in practice, so it has not come up.

---

## 10. Where the code is

| | |
|---|---|
| `src/Modbot.Web/src/index.css` | The phone density block, the touch floor, and the pinned-column rules. Beside VR's, with the same kind of comment. |
| `src/Modbot.Web/src/App.tsx` | The shell grid, the sheet and the bar at the foot. |
| `src/Modbot.Web/src/components/Chrome.tsx` | `Sidebar`, `NavSheet`, `BottomBar`, `Topbar`. |
| `src/Modbot.Web/src/components/ShortcutSheet.tsx` | The keyboard's tap path. |
| `src/Modbot.Web/src/components/ui/` | `dialog` (fits the screen and scrolls), `tabs` (the row scrolls), `card` (tighter padding), `select` (named for the touch floor). |
| `src/Modbot.Web/src/components/subject/shared.tsx` | The popup as the whole screen on a phone. |
