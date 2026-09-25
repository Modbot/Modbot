# Console look design

- **Date:** 2026-09-25
- **Status:** Built in the moderator web app (`src/Modbot.Web`). §22 lists the places that do not
  follow it yet, found by searching the code on this date.
- **Covers:** how the moderator app draws its panels, strips, tables, toolbars, controls, forms,
  charts, shell and messages, and which shared part draws each of them
- **Builds on:** the brand design (`2026-09-16-brand-design.md`). Its colours, logo and faces are
  unchanged; §19 here narrows its §5 for the app.

---

## 1. What the look is, and why

Modbot is an operations console. Moderators leave it open all evening beside VRChat and Discord
and read it in short glances. The look is called **Console**, and it is laid out like a calm
instrument panel: square panels that share one hairline instead of floating apart on gaps and
shadows, each named on a thin strip, with numbers and ids set in mono.

It replaced a look that read as generated. The app was built on shadcn/ui's components with
their defaults left as they came: rounded cards with soft shadows and gaps between them, round
pill badges with round dots, grey-track segmented controls with a white chip, uppercase
letter-spaced labels over big numbers, and empty states written as a centred sentence in a tall
box. That is what a generated dashboard looks like, and the people this app is for notice it.
Group owners and head moderators decide whether to trust Modbot with their ban records and a
VRChat login, and they read hype and template polish as a red flag (brand design §1).

The owner asked for a look of its own, with the colours and the logo unchanged. So nothing here
adds a colour, changes a token's value, redraws the mark or adds a face. What changed is where
edges fall, what sits on a strip, which face each kind of text uses, and the shape of each part.

Three things follow from that and hold on every screen:

1. **No colour that is not a token.** Tokens and `color-mix()` of tokens only. No gradient, no
   glow, no blur, no shadow above `shadow-sm` (brand design §3 and §5). The one set of literal
   colours in the app is VRChat's own trust rank colours in `lib/trustRank.ts`, which are VRChat's
   data and not part of the look.
2. **No decoration.** No new icon, emoji or ornament, and no new words on screen to explain a
   design (`CLAUDE.md`, "UI text"). The mascot stays off every screen that shows a record, an
   error or an empty state (brand design §2).
3. **One way to draw each thing.** Two places that show the same kind of thing look the same,
   because they use the same part. A second hand-drawn copy of a part is how the old look drifted
   screen by screen, so a copy is the first thing to fix when one turns up.

---

## 2. The parts

Every screen is built from these. Paths are under `src/Modbot.Web/src`.

| Part | File | What it is |
|---|---|---|
| `--strip`, `bg-strip` | `index.css` | The band under a panel's label, a table's column names and a list's footer. A mix of `--muted` and `--card`; in dark, of `--border` and `--card`, because the dark muted is too close to the card to show. |
| `--panel-pad` | `index.css` | The inset inside a panel: 0.75rem at every density. |
| `--strip-h` | `index.css` | The height of a strip: an `xs` control, 0.125rem, and the hairline (`--control-h + 0.125rem + --hairline`). |
| `--text-tiny` | `index.css` | The line under a name. Set in every density block beside `--text-small`. |
| `color-scheme` | `index.css` | `light` on `:root`, `dark` in `.dark`, so date pickers and scrollbars follow the app's theme and not the system's. |
| `font-label` | `index.css` | The display face at body size, weight 580, width 92. Every panel label, sidebar group name, dialog title and dropdown group heading. |
| Panel grid lines | `index.css` | `[data-slot="panel-grid"]`: the rules that make panels share one hairline (§5). |
| `Card`, `CardHeader`, `CardTitle`, `CardAction`, `CardContent`, `CardFooter` | `components/ui/card.tsx` | A panel, its strip, its label, the one control on the strip's right, its padded body and its footer strip. |
| `PanelGrid`, `EmptyRow` | `components/PanelGrid.tsx` | Panels side by side sharing one hairline; the one-line empty state. |
| `Table`, `Th`, `Tr`, `Td` | `components/ui/data-table.tsx` | A table run to its panel's edges. |
| `Stat`, `StatStrip`, `Panel`, `Nothing`, `PageMessage`, `Toggle`, `RangePicker`, `CoverageNote` | `pages/analytics/shared.tsx` | Readings, a page panel with a label, a chart's place while it loads, a page-level message, a small view switch, the range control and the data-covered panel. `StatStrip` is used in the popups too. |
| `PopupFrame`, `Panel`, `Block`, `Empty`, `Footer`, `More`, `Field`, `Note`, `FactList` | `components/subject/shared.tsx` | The person, world and instance popups: the frame, a named section, an unnamed block, a one-line state, a section's footer strip, the control that opens a tab, a label over a value, a muted note, a list of facts. |
| `SettingsSection`, `SettingsCard` | `components/settings/SettingsCard.tsx` | A settings tab's twelve-column sheet and one panel on it. |
| `Field`, `NumberField`, `LongField`, `PasswordField`, `Switch`, `Hint`, `Outcome`, `Placeholder` | `components/settings/fields.tsx` | A labelled field of each kind, an on/off switch, a muted line, the result beside a Save button, a section's loading or failed state. Re-exports `Checkbox`, `Row` and `Fact`. |
| `Row`, `Fact` | `components/ui/fact-row.tsx` | A read-only fact on one line, or its label over its value. |
| `Notice` | `components/ui/notice.tsx` | A tinted band for something to read before the controls around it. |
| `Chip` | `components/ui/chip.tsx` | A button that stays pressed while its choice is on. |
| `SwitchBank` | `components/ui/switch-bank.tsx` | A choice of values where exactly one is picked. |
| `Checkbox` | `components/ui/checkbox.tsx` | A tick box and its label. |
| `Textarea`, `Input`, `Select` | `components/ui/textarea.tsx`, `input.tsx`, `select.tsx` | Fields and the app's dropdown. |
| `Badge`, `Button`, `Tabs`, `DialogContent`, `Tooltip`, `Kbd` | `components/ui/` | The rest of the controls. |
| `Freshness`, `Unread` | `components/Freshness.tsx` | The age of a swept list on its strip; a list that cannot be trusted yet. |
| `FilterBar` | `components/filters/FilterBar.tsx` | A list's filters, with the page's own search and sort at its right end. |
| `Pager` | `components/Pager.tsx` | The page numbers along a list's foot. |
| `ChartFrame`, `DailyBars`, `DailyLine`, `Legend`, `Heatmap` | `components/charts/` | Charts. |
| `ProfileHeader` | `components/ProfileHeader.tsx` | A VRChat person's banner, picture, name and badges. |
| `Sidebar`, `Topbar`, `NavSheet`, `BottomBar`, `Footer` | `components/Chrome.tsx` | The shell (§4). |
| `WizardHeader`, `WizardBody`, `WizardFooter`, `Field`, `Tickbox`, `ErrorText`, `Brand` | `pages/setup/WizardChrome.tsx` | The sign-in and setup cards (§13.5). |
| `keyNames` | `lib/shortcuts.ts` | How every key hint is spelled. |
| `formatDay`, `ago`, `howLong` | `lib/format.ts` | A day, an age, a length of time. |
| `dateTime`, `longDay`, `shortDay`, `minutes`, `percent` | `components/charts/format.ts` | An instant, a chart's days, durations and shares. |

### 2.1 Which of two look-alike parts to import

Some parts share a name. None of them delegates to the other.

- **`Panel`.** `pages/analytics/shared.tsx`'s `Panel` is a whole `Card`: a strip with the title
  and an optional right-hand control, then a padded `CardContent` (`flush` drops the padding). A
  page imports this one. `components/subject/shared.tsx`'s `Panel` is a `<section>` with the same
  strip and one hairline under it and no edge of its own, because it is one of several sections
  stacked in a popup column. It adds `warn`, which tints the strip. Anything drawn inside a
  `PopupFrame` imports this one (the popups themselves, `SubjectHistory`, `SubjectCaseFiles`,
  `ProfileVersions`, `DiscordSide`, `AccountSide`, `DiscordLinkCard`).
- **A page-level message.** `Empty` in `pages/Members.tsx` is a `Card` holding an `EmptyRow`, and
  takes `tone`; the list pages, Health, Logs, Roles and Users import it. `PageMessage` in
  `pages/analytics/shared.tsx` is the same thing without `tone`; the analytics pages, Chat,
  Calendar, Live and Giveaways import it. `Empty` in `components/subject/shared.tsx` is an
  `EmptyRow` ruled off like a popup section, for a popup.
- **`Footer`.** `Footer` in `components/Chrome.tsx` is the foot of every page (Docs and Credits).
  `Footer` in `components/subject/shared.tsx` is the strip along the foot of a popup section.
- **A label over a value.** `Fact` (`ui/fact-row.tsx`) sets the value in `font-medium` and cuts it
  with an ellipsis, for a few facts side by side on a page or in settings. `Field` in
  `subject/shared.tsx` wraps the value and is for the left column of a popup. A label on one line
  with its value on the right is `Row`.
- **A labelled input.** `Field` in `settings/fields.tsx` is a label over an `Input`. `Field` in
  `setup/WizardChrome.tsx` is a label over any control, joined by `htmlFor`, for the sign-in and
  setup cards.
- **A tick box.** `Checkbox` everywhere; `Tickbox` in `WizardChrome.tsx` is `Checkbox` under the
  name the sign-in pages take their other parts by.

---

## 3. The page

### 3.1 The shell

`App.tsx` lays the app out as a grid: from `lg` a `13.5rem` sidebar and the rest; below `lg` one
column, with the sidebar in a sheet and a bar at the foot (§4). The right column is `main`, which
scrolls. At its top sit the two app banners (§10.6) and the top bar, which is `sticky`. Under the
top bar the page is drawn in a block padded `p-4` (16px), `p-5` (20px) from `lg`: that padding is
the gap between the top bar and the first panel, and the page's margin on every side. The page
foot (`Footer`) closes `main`: Docs and Credits on the right, muted small text, a hairline above.

### 3.2 Width

A page runs the full width of `main`. Three pages cap themselves, because their content reads
badly any wider: Settings at `max-w-[112rem]`, Credits at `max-w-5xl`, a case file at
`max-w-4xl`. Inside a panel, a paragraph is capped at `max-w-3xl` and a list of `Row`s sits in a
`max-w-lg` block, so a label and its value are never a screen apart.

### 3.3 Blocks

A page is either a stack of blocks or one sheet.

- **A stack** (the list pages, Calendar, Giveaways, Reviews, Flags, Logs) is
  `flex flex-col gap-3`: the toolbar, then the panel. `gap-3` is the one gap between unrelated
  blocks.
- **A sheet** (the analytics pages, Health, Roles, Credits, a case file) is one
  `PanelGrid className="grid-cols-1"` holding every panel, so the whole page reads as one
  instrument (§5).

### 3.4 Page actions

Controls that act on the whole page sit in one row above the panels,
`flex flex-wrap items-center gap-2` (Calendar and Giveaways use `gap-3`). What changes the view
comes first, on the left: a `SwitchBank` of views (Month and Agenda, a giveaway filter, Waiting
and Closed), then the month stepper. A `flex-1` spacer follows, and the page's one action sits at
the right end: New event, New giveaway, Add someone, New role, Refresh. A row that holds only the
action is `flex justify-end`. An action that makes something is the `default` variant; one that
reads the page again (Refresh) is `outline`. Status about the page as a whole, such as Reviews'
"Detection last ran `10m ago`" or Live's stream state, sits at the right end of the same row in
muted small text.

An action on the page that fails says so in one line of `text-destructive` small text under that
row (Calendar, Giveaways, Flags).

### 3.5 The title

The page title is in the top bar only, in `font-display` at `--text-base` plus 3px. Nothing
between the title and the panels has a heading of its own: the strip names the panel. No heading
anywhere is uppercase or letter-spaced, and every label is sentence case.

---

## 4. Sidebar, top bar and bottom bar

### 4.1 Sidebar

1. A rail on `bg-background` with a hairline on its right: the same surface as the page, so the
   panels are the only raised things. It scrolls on its own.
2. At the top, the group this Modbot manages: its banner when VRChat has one (`aspect-[3/1]`,
   `rounded-sm`, inset `mx-3`), its icon (`size-7`, `rounded-sm`) and its name in the display
   face. With no group, Modbot's own mark (`size-7`) and the word "Modbot". With a group, Modbot's
   mark moves to the foot at `size-5` beside the word in muted small display text.
3. Under it, the search button: a control high, an input's edge on `bg-card`, a search icon,
   "Search", and `Kbd mod+k` on the right.
4. Entries run to the rail's edges and are a control high. The active one is `bg-card` and
   `font-medium`, with a 2px `bg-primary` bar on its left edge. No filled pill and no accent fill.
   The rest are muted and take `bg-card/60` on hover.
5. Each entry with a go-to chord shows it on the right in mono at `--text-tiny`, in
   `text-muted-foreground/60`, spelled by `keyNames` with the keys side by side and no "then"
   (§15). Hidden below `lg`.
6. Group names are `font-label` at `--text-small` in muted text, sentence case, followed by a
   hairline that runs to the rail's right edge.
7. A waiting count (open reviews) is a mono number on `bg-primary`, `rounded-sm`, at
   `--text-tiny`.
8. The status rows at the foot sit under a full-width hairline and lead with `size-1.5` squares.

### 4.2 Top bar

`sticky` at the top of `main`, `bg-background`, a hairline under it, `px-4 py-2.5` (`px-5` from
`lg`). From left to right: the title (it truncates), the demo marker when the deployment is a demo,
then from `lg` only, at the right end with `gap-3`: the density `SwitchBank` (default size), the
theme button (`ghost`, `sm`, icon only), your account (`ghost`, `sm`, an icon and your username
capped at `10rem`) and sign out (`ghost`, `sm`, icon only). Below `lg` those four move into the
navigation sheet, because beside a title on a phone they leave no room for it.

### 4.3 Bottom bar and navigation sheet

Below `lg` only. The bar is fixed to the foot of the screen on `bg-background`, with a hairline on
top and three equal buttons divided by hairlines: Menu, Search and Actions. Each is an icon at
`size-5` over its label at `--text-tiny`, at least a control high, and fills `bg-muted` while
pressed. It pads itself by the phone's safe area, and `main` keeps `3.25rem` plus the safe area
free at its foot so the last row is never under the bar.

Menu opens `NavSheet`: the same `Sidebar` in a sheet from the left, `17rem` wide and never more
than 85% of the screen, `bg-background` with a hairline on its right, over a dimmed page. Its foot,
under a hairline, holds the density bank, the theme button, your account and Sign out, each as a
`ghost` `sm` button with its label.

---

## 5. Panels and the sheet

1. A panel is `Card`: square corners, one hairline edge, `bg-card`, no shadow and no inset of its
   own. Never add `rounded-*`, `shadow-*` or `py-6` to a card. Spacing belongs to what is inside:
   the strip, the content, the footer.
2. Panels that sit next to each other go in a `PanelGrid`, never a `grid gap-4`. The columns are
   the caller's classes on the `PanelGrid` (`lg:grid-cols-2`, `grid-cols-12`). Any child works.
   Never write `data-slot="panel-grid"` by hand.
3. How the grid draws one line: every child loses its own border and radius and draws an outline
   one hairline wide; the grid's gap is one hairline and its margin one hairline, so the outlines
   on either side of a gap land on the same pixels. An empty cell at the end of a short row draws
   nothing. A `PanelGrid` inside a `PanelGrid` is a row of the sheet, not a panel: it draws no
   outline of its own and its cells draw the lines.
4. A page made of several panels is one `PanelGrid className="grid-cols-1"`. A row of two inside
   it is a nested `PanelGrid className="lg:grid-cols-2"`. Controls that act on the whole page (the
   range picker, the filter bar) sit above the sheet, never in it.
5. The two panels of a row are about the same height. A short table beside a long one leaves a
   tall empty half, so the short one takes a full-width row of its own (My Server's "New members
   still here", with "Member health" and "Top contributors" paired below it).
6. Never separate the panels of one sheet with a gap, and never put a gap other than `gap-3`
   between unrelated blocks.

### 5.1 Lines between things

1. Never set a border width inline (`style={{ borderBottomWidth }}`). On a `PanelGrid` child it
   beats the grid's rule, and on a repeated row it beats `last:border-0`; both draw a second line.
   Use the classes: `border-(length:--hairline)`, `border-b-(length:--hairline)`.
2. Lines between the items of a list are `divide-y-(--hairline) divide-border` (or `divide-x-`).
   `divide-(length:...)` sets a colour, not a width, and draws near-black lines.
3. **A grid drawn by hand** (the calendar's month) gives each cell its right and bottom edge only,
   each with its own width (`border-r-(length:--hairline) border-b-(length:--hairline)`), and
   drops them on the last column and the last row (`[&:nth-child(7n)]:border-r-0`,
   `[&:nth-last-child(-n+7)]:border-b-0`), where the card's own edge is.
   `border-(length:...)` would set the width on all four sides, so two neighbours would draw two
   lines side by side. The weekday names above the days are a strip: `bg-strip`, `--strip-h`
   high, a hairline under each.
4. Two parts draw their lines as a gap instead of a border: the `SwitchBank`, whose segments sit
   one hairline apart on a `bg-border` box, and the panel grid. A wrapped row of either is still
   divided by one line.

---

## 6. Strips

1. A panel is named by `CardHeader` and `CardTitle`: a strip of `bg-strip`, at least `--strip-h`
   high, with a hairline under it and `--panel-pad` either side. The title is `font-label`. One
   control or a short status goes on the right in `CardAction`. The analytics `Panel`, the popups'
   `Panel`, `SettingsCard` and `WizardHeader` all draw their strip this way; never write a second
   one.
2. Everything on a strip is sized to keep it `--strip-h` high. A button there is `size="xs"`, or
   `icon-xs` with no label; a `Select` is `size="sm"`; a `SwitchBank` is `size="sm"`. All three are
   `--control-h` less 0.375rem. That holds on `CardHeader`, `CardAction`, `CardFooter`, the
   analytics and popup `Panel` heads and a `SettingsCard`'s `action` and `footer`. Three places are
   not strips and keep their own sizes: the sign-in and setup cards' `WizardFooter`, which holds
   the page's own action at the default size; a `Notice`'s action, which is `sm`; and a button in
   a dialog's title row, which is `sm` because that row holds a title, a subtitle and a close
   button a control high (the model picker's Refresh).
3. A panel over a list has a strip above the column names when there is something to say about
   the list: its age on the left (`Freshness`), and the count on the right in mono (Members,
   Bans).
4. A list's footer (the pager, "Load more", how many are shown) is a strip too: `CardFooter`,
   which is `bg-strip` with a hairline on top and at least `--strip-h` high. Text alone on it is
   muted small text (the popups' `Footer`). `Pager` draws the same strip with the same classes.
   The current page in the pager is `bg-accent text-accent-foreground`, in mono like the other
   numbers.
5. A warning about the data in a panel tints that panel's strip `bg-warn/10` and says it after a
   filled `bg-warn` square (§10.4).

---

## 7. Tables

1. A table inside a panel is `Table` with `Th`, `Tr` and `Td`, whether it sits on a page, in a
   popup or in a component such as `InstanceTable`. It runs to the panel's edges: `<Card>` then the
   table directly, no `CardContent` around it. For the analytics `Panel`, the popups' `Panel` and
   `SettingsCard`, pass `flush`.
2. Column names sit on the strip: `bg-strip`, muted, `font-normal`, sentence case, `py-2` around
   one line of `--text-small` and `--panel-pad` either side. `Th` and every hand-written `<th>` are
   that one height.
3. A row is at least `--row-h` high, with one hairline between rows (`Tr`, and the same classes on
   every hand-written `<tr>`). A cell adds no padding above or below it, in `Td` as in a
   hand-written `<td>`, so a row grows only for a cell that holds more than a row's height, and
   grows by the same amount on every table. A name over an id is such a cell: every list whose
   first column is a person measures about 37px a row at dense, not 34.
4. Cells do not wrap (`Td` is `whitespace-nowrap`), so a narrow screen scrolls the table sideways
   and every row stays one height. A cell that holds a sentence, or a name that may be a sentence,
   passes `whitespace-normal` and a minimum width in rem (the audit log's "What happened" is
   `min-w-[20rem]`), so on a phone the table scrolls sideways before the sentence is squeezed to a
   word or two a line.
5. A number compared down a column is right-aligned and in mono, and so is its column name.
6. On a phone the first column of a `Table pinFirst` (or a hand-written box marked
   `data-pin-first`) stays put while the rest scrolls, and is capped at `min(14rem, 48vw)`
   (`index.css`). The id under a name there keeps `truncate`: it does not ride the sideways
   scroll, and without the ellipsis it paints over the next column. The popup the row opens writes
   the id out in full (§14.1). An id in any other column rides the scroll whole and is never cut.
7. The marks after a name in that column (18+, the trust rank, "representing") go in `Marks`
   (`pages/Members.tsx`), and the name gets `max-md:max-w-full max-md:shrink-0`. On a phone the
   name keeps its line and truncates, and a mark that does not fit whole is hidden, never wrapped
   under the name or cut in half, so every row is one height.
8. A row that opens something takes `hover:bg-muted/40`, and the row the keyboard is on takes
   `data-[selected]:bg-accent/60`.

The heights, measured in the running app:

| | Dense | Comfortable | Phone | VR |
|---|---|---|---|---|
| Column names | 34px | 36px | 36px | 41px |
| A row (`--row-h`) | 34px | 44px | 46px | 56px |

A phone is not a fourth density. The phone's values come from a media query,
`(pointer: coarse) and (max-width: 63.9375rem)`, that replaces the dense and comfortable values
on a touched phone or tablet and leaves VR alone. So "Phone" above is what dense and comfortable
both become on a phone.

---

## 8. Toolbars

The row of controls above a list decides what the list shows. It is one of two things.

1. **`FilterBar`**, on every list that has filter properties (Members, People, Discord members,
   the audit log). Its chips come first, then its Filter button (with `Kbd f`), then a `ghost`
   Clear once there is a chip. Its right end is one group, given as the bar's children, in this
   order: a button that belongs to this page (Members' "Joined ... ×" window, the audit log's
   "`3` new"), the search box, then the sort or status `Select`. From `md` up the group sits at the
   right end of the bar's line; below `md` it takes a line of its own, so a phone reads the bar as
   two rows, the filters and then the search, and the search box gives up its width to fill what
   the sort leaves. A bar with nothing at its right end has no second row.
2. **Plain controls**, on a list with no filter properties. The row is
   `flex flex-wrap items-center gap-2 md:justify-end` and holds the same things in the same order
   as the filter bar's right end (Bans: search, then status).

In both, the search box is `Input className="w-56"` with a placeholder that says what it
searches ("Search by name or id") and an `aria-label`, and `/` selects it. A control that changes
how the whole page is shown, such as the analytics range (`RangePicker`: the range bank, a spacer,
then the two days it covers in mono) or Calendar's Month and Agenda, sits on the left of the row
above the sheet, never at the right end.

---

## 9. Readings

1. A reading is `Stat`: its label top-left in muted small text, the value bottom-right in mono at
   1.75 times `--text-base`, and any note on the value's baseline to its left. A note with no room
   beside the value goes on its own line above it. Neither the note nor the label is ever cut with
   an ellipsis; the label wraps to a second line. It is at least two rows high. Never a card with
   an uppercase label over a number.
2. Readings always come in a `StatStrip`: a `PanelGrid` two across, and four from `xl`.
   A page with six readings gives it `md:grid-cols-3 xl:grid-cols-6` and fills the row. A strip of
   three inside a popup or a panel gives `md:grid-cols-3 xl:grid-cols-3`.
3. A strip inside a panel runs flush: `Panel flush`, `StatStrip className="m-0"`, and the rest of
   the panel's content in a `p-(--panel-pad)` block under it.

---

## 10. Empty, loading, failed and warned

### 10.1 Empty

An empty list or panel is `EmptyRow`: one left-aligned line of muted small text, a row and a half
high, led by a hollow square. The hollow square is the "no signal" mark, the unfilled twin of the
status square (§11.1), drawn as a bordered span and not an icon. It sits where the first row would
have been, inside the panel. Never a centred sentence in a tall box, never `py-10 text-center`.

A panel whose whole content is the empty row is `flush` while it is empty
(`flush={rows.length === 0}`), so the line sits under the strip with no inset around it. That is
the same in a popup, on an analytics page and in settings.

The words say what is empty, as a statement: "Nobody matches", "No bans listed", "Nothing
waiting." They never explain what would fill it.

### 10.2 Loading

Loading is the same row saying "Loading…". A page that has nothing to show until it loads returns
a page-level message (§2.1). A chart that is loading keeps its place with `Nothing`, which is the
empty row held at the chart's height, so the panel does not jump when the data comes. A picture
that is loading is a `bg-muted` block that pulses (evidence, `h-40`).

### 10.3 Failed

A page-level message is a `Card` holding an `EmptyRow`. When it says a list could not be read,
the row is `tone="danger"`: the square fills in the destructive colour and the words are plain
foreground text, so a failure never reads as an empty list. On a list page that is
`Empty tone="danger"` (`pages/Members.tsx`), in settings `Placeholder tone="danger"`, and inside
a panel `EmptyRow tone="danger"`. §22.2 lists the places that still show a failure the old way.

The result of an action (a Save, a test) is not a page message. In settings it is `Outcome` beside
the button: "Saved." in `text-ok`, or the problem in `text-destructive`, at small size. On the
sign-in and setup cards and in the Roles form it is `ErrorText`, one line of destructive small text
with `role="alert"`.

### 10.4 A warning about the data

A warning about the data in a panel tints that panel's strip `bg-warn/10` and says it after a
filled `bg-warn` square. Not a separate rounded yellow box. The members and ban lists do it with
`Freshness`, which turns into `Unread` (the warn square and the sentence, in `font-medium`) until
the first full sweep; Discord members uses `Unread` the same way. A popup section says it with
`Panel warn` and `Unread` on the right of its strip (Membership's "Member list not read yet.").

### 10.5 Notices

A notice (a lock, a warning, a result somebody has to read before the controls around it make
sense) is `Notice`: a band tinted 10% of its tone over the card, with no edge of its own, led by a
filled square in the tone's colour, the line in `font-medium` and any detail under it in muted
small text. The tones are `ok`, `warn`, `danger` and `neutral` (the strip's colour). Never a
bordered box inside a panel. When the notice is all a panel has to say, it goes straight under the
strip with no `CardContent` around it (Account's "Sign out everywhere").

### 10.6 The two app banners

Two messages concern the whole app and sit above the top bar, full width, a strip high:
`SignInWaitBanner` (VRChat's sign-in wait) and `WaitingAlertsBanner` (a critical notification that
reached nobody). They are tinted `bg-destructive/10` with a `border-destructive/40` hairline
under them and destructive text, and each leads with a filled square. Nothing else
uses this band.

---

## 11. Status, badges, chips, switch banks and tabs

### 11.1 Status

Status is a small filled square (`size-1.5` or `size-2`, no rounding) in the tone's colour,
followed by the state in plain words. Never a round dot. The square is `aria-hidden`; the words
carry the meaning. `SourceBadge` and `TrustRankBadge` put the same square inside an `outline`
badge, in the source's series colour or VRChat's rank colour.

### 11.2 Badges

1. `Badge` is `rounded-sm` with a hairline edge, `px-1.5`, at `--text-small`. Never
   `rounded-full`.
2. `secondary` (roles, labels) is `bg-strip` with a border edge. `outline` is the edge alone in
   muted text. `destructive`, `ok` and `warn` are tinted, not solid: `border-X/40 bg-X/10
   text-X`. `ok` is the 18+ mark; `warn` is Nuisance.
3. Each variant sets its own edge colour (`default`, `ghost` and `link` set `border-transparent`)
   and the shared base sets none, because a bare `badgeVariants()` call is not merged and two edge
   colours would leave the tone's edge to CSS order.
4. Never a hand-drawn tinted span. A control that looks like a badge takes `badgeVariants`.
5. A short machine value in a badge (`18+`, a count) is `font-mono`.

### 11.3 Chips

Choices that are each on or off by themselves (the model filters, a route's roles, a case file's
reasons, the call log's "Flagged only") are `Chip`s: a control high, `rounded-sm`, an input edge,
`bg-accent text-accent-foreground` while on and muted `bg-card` while off. Never an outline
`Button` with `aria-pressed` and an accent class of its own.

### 11.4 Switch banks

A choice where exactly one value is picked (density, a range, Day, Week and Month, a provider, a
store, a list's views) is a `SwitchBank`, however many values it has: one `rounded-sm` bordered
box, segments divided by hairlines, the active segment `bg-accent text-accent-foreground`, the rest
muted with `hover:bg-muted`. Never a grey track with a white chip, and never a hand-written copy.
`size="sm"` inside a strip; the analytics `Toggle` is a `sm` bank. States that filter one list
(Reviews' Waiting and Closed; Flags' Open, Dismissed and Confirmed, and its languages) are a bank,
not tabs. A count inside a segment is mono. When the values do not fit on one line the bank wraps
inside its own box: the hairlines stay one line wide and each row's segments stretch to the box's
width. Never a sideways scroll, and never a switch to `Chip`s because the bank got long.

### 11.5 Tabs

Tabs that switch between different lists or sections (Settings, Bans' "Ban list" and "People
acted on more than once", Credits, the API tab, the popups) are `Tabs`: underlines, no gap between
tabs, `px-3`, at least a control high, a 2px `bg-primary` bar at the foot of the active tab with
square ends, and a hairline under the row. The row scrolls sideways on a narrow screen instead of
wrapping. A page with tabs passes `className="gap-3"` so the tab row and what is under it are
`gap-3` apart. Counts on tabs are mono.

### 11.6 Disclosures

A `details` is led by a muted `ChevronRight` at `size-3.5` that turns down when it is open
(`group-open:rotate-90`), the same chevron the audit log's rows, `JsonView` and the chat's tool
steps turn with `rotate-90` on their `aria-expanded` buttons. The words are muted and turn
`foreground` on hover; never accent or primary text, and never the browser's triangle: the
`summary` is `list-none` with `[&::-webkit-details-marker]:hidden` (Account's "Link a different
account").

---

## 12. Buttons

1. Every button is `rounded-sm`. Focus is a 2px `outline-ring` outline, offset 1px, with no ring
   glow. That includes buttons a part draws for itself, such as a dialog's close button.
2. `default` is solid primary, at most one per panel. `outline` is `bg-card` with an input edge.
   `secondary` is `bg-strip` with a border edge; there is no grey-filled button. `ghost` is muted
   text that fills `bg-muted` on hover, never the accent. `destructive` is solid destructive.
3. Heights come from `--control-h`: `default` is a control high, `sm` 0.125rem less, `xs` 0.375rem
   less, `lg` 0.25rem more, and each `icon-*` size is the square of its height. A caller never
   passes `h-*` or `size-*` to a button, a field or a dropdown.
4. The current item in a run of buttons (the page in a pager) is `bg-accent
   text-accent-foreground`.

### 12.1 Destructive actions

Four shapes are in use, and each matches how much the action takes away:

1. **On a row of a list** (a Discord route), the action starts as a `ghost` "Delete". Pressing it
   swaps it in place for a `destructive` "Delete" and a `ghost` Cancel, both `xs`
   (`ChannelsCard`).
2. **Typed to confirm** (purging a person, deleting an account), the `destructive` button stays
   disabled until the typed id or name matches. In a `SettingsCard` it is the footer's `xs` button;
   at the foot of a form it is `sm` at the right end.
3. **In a dialog that exists to confirm** (ban, deny a request, delete a conversation), the
   `destructive` button is the dialog's action: `sm`, after an `outline` Cancel, at the right end.
   An action in the same dialog that is not destructive (unban, approve) is `default`.
4. **In a panel's footer** (withdrawing a case file), `destructive` `xs` first, then a `ghost`
   Cancel, then the problem if there is one.

---

## 13. Fields and forms

### 13.1 Fields

1. `Input` and the `Select` trigger are a control high, `rounded-sm`, `bg-card`, with a hairline
   `border-input` edge and no shadow. Focus turns the edge `border-ring` and adds a 1px ring,
   nothing wider. A `Select` on a strip is `size="sm"`.
2. A field for more than one line is `Textarea`, drawn like `Input`. Never a hand-written textarea
   class string. The chat composer is the one exception: its bordered wrapper is the field, and the
   textarea inside it has no edge.
3. A value a moderator types that is an id, an address or a key is `font-mono` in its field.
4. On a phone every field is 16px text, because Safari zooms the page into a field with smaller
   text and never zooms back out (`index.css`).

### 13.2 Dropdowns

A dropdown's trigger ends in `ChevronDown` at `size-3.5 opacity-60`, whether its list can be
searched (`Picker`, the Discord channel and role lists) or not (`Select`); never `ChevronsUpDown`.
Both lists are `rounded-sm` popovers with `shadow-sm`, tick the saved value with a `Check` in a
column of its own, and fill the row under the pointer or the arrow keys with `bg-accent`. A group
heading in the list is `font-label` in muted text. The search box at the top of the list is what
sets the searchable one apart, not the trigger.

### 13.3 On, off and one of several

1. A single on/off setting is the settings `Switch`: a `rounded-sm` track two thirds of
   `--control-h` high on a row a control high, `bg-primary` when on and `bg-input` when off, with
   its label in `font-medium` beside it. At VR the row is the 48px target.
2. A tick box is for choices listed together that are each on or off (the services Health alerts
   watch, who is emailed, a role's permissions, the roles given to an account, "Keep me signed
   in"), and it is always `Checkbox`: a `rounded-sm` box half `--control-h` wide with a hairline
   `border-input` edge on `bg-card`, filled `bg-primary` with a `primary-foreground` tick when on,
   and the 2px ring outline on focus. A box over a group that is partly ticked passes `mixed` and
   draws a bar in place of the tick. The box sits on the label's first line, so a label that wraps
   keeps its box beside its start. A reason the box cannot be ticked goes in `title`. Never a bare
   `<input type="checkbox">`.
3. On a phone, `index.css` sets every tick box to `1.125rem` and gives the label it sits in a
   control's height, with box and label centred in it together. Measured, the box is 15px dense,
   18px on a phone and 24px in VR.
4. One of several values is a `SwitchBank` (§11.4). §22.4 lists the four radio groups that are
   still the browser's own.

### 13.4 Day fields

A day field is `Input type="date"` in `font-mono`, as wide as the whole date at every density
(`w-[calc(10ch+3.5rem)]`: `ch` follows the mono face, so VR does not cut the year). Empty and
unfocused, its `mm/dd/yyyy` is `text-muted-foreground`, the colour of a placeholder. The browser's
calendar button stays, since in Chromium it is the only way to open the calendar with the mouse,
but it is drawn like a dropdown's chevron: `size-3.5`, `opacity-60`, `ms-1.5` before it. The
highlight on the part being typed is the browser's own and takes no author styles. The Logs
toolbar's `dayBox` is the one written so far.

### 13.5 Laying out a form

1. **A field** is its label over its control: a `label` that is `flex flex-col gap-1`, the label's
   words muted at `--text-small` (`Field`, `NumberField`, `LongField` and `PasswordField` in
   `settings/fields.tsx`). The sign-in cards' `Field` puts the label `mb-1` above any control and
   can add a muted hint after a middle dot.
2. **Fields one under another** in a panel are `gap-3` apart (the body of a `SettingsCard`). On
   the sign-in and setup cards the body is `space-y-4`.
3. **Fields side by side** are `grid gap-3 sm:grid-cols-2` (or `sm:grid-cols-3`), one column on a
   phone.
4. **Widths.** A field fills its column (`Input` is `w-full`). The caller caps a field only when
   its value is short: a number `w-24` or `w-28`, a list's search `w-56`, a day field as in §13.4.
5. **The footer.** In settings, the card's `footer` strip holds Save first on the left, `xs`,
   `default` variant, then `Outcome`; a second action there (Test, Send, Add) is `outline` `xs`.
   A form inside a panel (Roles, a case file, a Discord route) puts its primary action first,
   `sm`, then a `ghost` Cancel, then the problem, all on the left. A dialog that confirms one
   action puts an `outline` Cancel and then the action at the right end (§12.1). §22.6 names the
   dialogs that do it the other way round.
6. **Save** is always the `default` variant, and it is disabled until there is something to save
   where the form can tell.

### 13.6 Sign-in and setup cards

The screens outside the app shell (sign in, forgot and reset password, join, connect, pair, link
your accounts, setup) are one `Card` centred on `bg-background` with `p-6` around it, under
`Brand`: Modbot's mark at `size-7` and the word in the display face, centred, with the group's name
under it on setup. The card is capped between `420px` and `520px` by the page. It is built from
`WizardChrome`:

1. `WizardHeader`: the card's strip, with the title as an `h2` in `CardTitle` and the step's name
   (the eyebrow, such as "Sign in") on the right in muted small text. An optional muted paragraph
   follows under the strip.
2. `WizardBody`: the fields, `space-y-4`, inset by `--panel-pad`.
3. `WizardFooter`: the footer strip. The way out (Forgot password?, Skip this step, Back to
   Modbot, Cancel) is on the left as `ghost` (or `secondary` on Connect); a `flex-1` spacer; then
   Back as `outline`, and the card's action on the right as `default`. These keep the default size:
   they are the page's own action, not controls on a strip.
4. Setup puts `StepIndicator` above the strip: one 3px bar per step, `bg-primary` for the steps
   done and `bg-secondary` for the rest, one pixel apart.

---

## 14. Dialogs, popups, sheets and tooltips

1. A dialog is `DialogContent`: `rounded-sm`, `shadow-sm` at most, `bg-card` with a hairline edge,
   at most `520px` wide unless the caller widens it, and never taller than the screen less
   `1.5rem`: the body scrolls instead. Its title row is a strip (`bg-strip`, `px-4 py-2`, a hairline
   under it) holding an optional control before the title, the title in `font-label` at
   `--text-base` plus 1px, an optional subtitle in muted small text, any actions, and the close
   button, a control high and square. The body is `px-4 py-4`.
2. Overlays dim (`bg-foreground/30`, `dark:bg-background/70`) and never blur. A dialog opened from
   another dialog (Ban over a person popup) dims the one behind it: overlay and dialog share one
   layer, so the later one covers the earlier. Never give a dialog a layer of its own above its
   overlay's.
3. The subtitle under a dialog's title (a subject's id, a world's location) wraps
   (`[overflow-wrap:anywhere]`) and is never cut with an ellipsis: it is where a phone reads the id
   a pinned table column cut short.
4. Popovers (a dropdown's list, the filter bar's editors) are `rounded-sm` on `bg-popover` with a
   hairline edge and `shadow-sm`.
5. A tooltip is `rounded-sm`, `bg-foreground` with `text-background`, at `--text-small`.

### 14.1 Popups

The person, world and instance popups use `PopupFrame`. On a phone it is the whole screen, edge to
edge, with no corners and no edge. From `md` it is a dialog `1rem` in from every side of the window,
at most `100rem` wide, with a `22rem` left column (identity) and the tabs on the right, a hairline
between them. Every block in the left column draws the hairline under itself (`Block`, `Panel`,
`Empty`), so the column draws only the one beside it. The popup's subtitle writes the subject's id
out in full.

### 14.2 Side sheets

Three panels slide over the page from an edge: the navigation sheet from the left (§4.3), the chat's
conversation list from the left (`17rem`), and the Users page's account details from the right
(`26rem`, `bg-card`, a hairline on its left, `shadow-sm`). Each is square and full height.

---

## 15. Key hints

1. A key hint is `Kbd`: a `rounded-sm` box per key with a hairline edge on `bg-card`, in muted mono
   at `--text-tiny`, sized from that text (`1.5em` high and at least as wide, `px-1`) and never
   from `--control-h`, so a hint inside a button or a row never sets its height. Measured, a key is
   18.5px high at dense, 20px at comfortable and 28px in VR.
2. A chord's keys are joined by the word "then" in muted text: `G then M`.
3. Every key is spelled by `keyNames` in `lib/shortcuts.ts`: letters in capitals (`G`), `Ctrl K`
   (or `⌘ K` on a Mac), `Esc`, `Enter`, arrows as arrows. So a key never reads `g` in one place and
   `G` in another.
4. `Kbd` hides itself below `lg`, because a phone has no keyboard; the row or button it sits in
   stays.
5. It appears in the sidebar's search button (`mod+k`), the filter bar's Filter button (`f`), the
   command palette (each action's keys, and `Esc`), and the keyboard sheet.
6. The sidebar's go-to chords are the one place a chord is not drawn with `Kbd`. They are the
   `keyNames` spelling side by side, with no boxes and no "then", in `text-muted-foreground/60`,
   because a VR row with a long page name has no room for boxes (§4.1).

---

## 16. Charts

1. A chart sits in `ChartFrame`, inside its panel's padding, the full width of the panel's content
   and a fixed height from `chartHeight` in `components/charts/theme.ts`: 104px small, 160px
   regular, 220px tall. Recharts measures its parent, and a parent with no height measures as zero.
2. The Y axis is `width="auto"`, never a pixel width. Tick text follows `--text-small`, which grows
   with the density, so a fixed width that fits dense cuts "126 GB" to ".26 GB" in VR or wraps a
   label such as "under a minute" into the X axis. Recharts measures the drawn ticks and measures
   again when the density changes.
3. Axis text is muted at `--text-small`; grid lines are horizontal only in `--chart-grid`, one
   hairline wide; there are no axis lines and no tick marks. Bars are at most 28px wide with their
   top corners rounded 3px, square when stacked. Lines are 2px with no dots.
4. The series colours are `--series-1` to `--series-5` in a fixed order, never cycled, so a series
   keeps its colour when a filter removes its neighbours.
5. **A legend** is drawn only when a chart has more than one series, and only when there is a
   plot. It is the chart's `legend` prop, which `ChartFrame` draws above the plot, `gap-2` apart:
   never a `Legend` the page draws above the chart, so the legend goes when the plot goes. A legend
   swatch is a `size-2.5` circle, and a tooltip's a `size-2` circle.
6. **An empty chart** is its empty row alone, with no plot height held open and no legend. The
   row counts the panel's inset in its height, so an empty chart panel is as high as any other
   empty panel. The empty text is a short statement ("Nothing recorded in this range.").
7. A chart that is loading or failed keeps its height with `Nothing` (§10.2).
8. A choice between two views of one chart is the analytics `Toggle` on the panel's strip.

---

## 17. Text

### 17.1 Faces and sizes

| Face | Where |
|---|---|
| `font-display` | the page title, the wordmark, the group's name in the sidebar |
| `font-label` | panel labels, sidebar group names, dialog titles, dropdown group headings, the permission groups in a role |
| body (IBM Plex Sans) | everything else, with `tabular-nums` on the whole page |
| `font-mono` | ids, timestamps, counters, compared numbers, sizes, versions, addresses |

Body text is `--text-base`. Tables, strips, labels, badges, notes and fields' labels are
`--text-small`. The line under a name (an id, a region, "bot", "representing") is `--text-tiny`,
and a plain name on its own line under another is `--text-small`. `--text-tiny` is 11px dense,
12px comfortable and on a phone, and 16px in VR, where nothing goes under the 16px floor. Never a
fixed `0.6875rem` or `0.75rem` on a caption, and never a fallback such as `var(--text-tiny, 11px)`.

### 17.2 What is mono

Numbers that are compared down a column are right-aligned and mono. Ids, timestamps and counters
are mono wherever they appear. Names and sentences never are. The caller says which a value is
(`mono` on `Row` and `Fact`); the face is never guessed from what the value contains, so a
username with a digit in it stays in the body face. In a sentence only the machine part is mono:
"Opened `3h ago`", "Detection last ran `10m ago`", Health's "last completed a pass `8h ago`".

### 17.3 Dates and times

| What | How | Looks like |
|---|---|---|
| A day | `formatDay` (`lib/format.ts`), in the viewer's time zone | Sep 25, 2026 |
| A day of a chart or a range | `longDay`, or `shortDay` on an axis (`components/charts/format.ts`), in UTC like the data | Sep 25, 2026; Sep 25 |
| An instant in a list | `dateTime` (`components/charts/format.ts`) | Sep 25, 03:41 PM |
| When a fact happened | `FactTime` (`components/facts.tsx`): the time, or a window written with a tilde, the full date in `title` | 03:41 PM, ~03:30 PM–03:45 PM |
| An age | `ago`, against the `now` the server sent, never the browser's clock | 3h ago, just now |
| How long | `howLong`, `duration`, `minutes` | 300d, 12 minutes |
| A range of days | the two days, each in mono, joined by a spaced en dash | Sep 23, 2026 – Sep 25, 2026 |

Every one of them is mono.

### 17.4 Nothing to show

- A table cell or a reading with no value shows the long dash (U+2014), muted when the cell's own
  text is not already muted.
- A time that never happened says "Never" (or "never" inside a sentence).
- A list with no items inside a cell says so in words ("No roles", "No roles yet").
- A thing Modbot has not read yet says that ("a world Modbot has not read yet"), not "unknown".

### 17.5 Links

1. A name or an id that opens something in Modbot (`SubjectLink`, `PersonLink`, `AccountLink`,
   `WorldLink`, `InstanceLink`) is a button in the text's own colour, `font-medium`, underlined on
   hover. An unnamed one shows its id in mono.
2. A link out of Modbot inside a sentence or a caption (a download, a picture, a source in a chat
   answer) is underlined in the text's own colour (`underline underline-offset-2`).
3. `text-link` (the `--link` token, which has its own dark value because the dark primary fails as
   text) is for a link that stands on its own as a control: `Button variant="link"`, `Badge
   variant="link"`, a citation in a chat answer, Settings' "Use `address`".
4. `text-primary` is never used for text.
5. The page foot's links are muted and turn foreground and underlined on hover.

### 17.6 Lists of facts

A list of read-only facts is `Row`s on every page (Health's VRChat and Logs panels, a review's
evidence, Credits), never a `<dl>` grid or a run of "Label: value" pairs. `Row`'s value is any
node, so a link goes straight in. The list sits in a `max-w-lg` block whose text colour is the
values' (the labels stay muted), and a value that needs a tone takes it from a wrapper round its
`Row` (Health's next sign-in, in `text-destructive`). `mono` is set by the caller (§17.2).

---

## 18. Pictures

1. A picture across the top of a block (a world's image, a person's banner) runs to the block's
   edges with a hairline under it, and is drawn only when there is one. With none the block starts
   at its next row; never an empty tinted box where the picture would be. A world's or instance's
   image is `aspect-[4/3]` across the top of the popup's left column; a banner is `aspect-[3/1]`, at
   most `max-h-48`, and runs out over the block's padding.
2. A profile picture over a banner's edge is cut out by a ring of the panel's colour
   (`ring-4 ring-card`); with no banner under it, it has no ring (`ProfileHeader`).
3. A person's picture is round (`rounded-full`), `object-cover`, on `bg-muted`: `size-7` in a list
   row, `size-6` for a linked Discord account beside a name, `size-5` inline, `size-16` in
   `ProfileHeader` and `size-20` in a profile's versions. With no picture, an empty `bg-muted`
   circle of the same size is drawn, so the names in a column still line up.
4. A group's icon is round beside the group a person represents and in setup's group list, and
   `rounded-sm` in the sidebar's heading, where it sits beside the group's banner.
5. A world's thumbnail in a table row is `size-8`, `object-cover`, and drawn only when there is
   one.
6. Evidence is shown whole (`object-contain`, at most `max-h-80`) and opens full size in a new tab.

---

## 19. Shape and density

### 19.1 Corners

- `rounded-none` for panels, strips, table cells, sidebar entries, status squares, sheets and a
  popup on a phone.
- `rounded-sm` (`--radius` less 2px) for every control: buttons, fields, dropdowns, badges, chips,
  switch banks, tick boxes, the switch's track, `Kbd`, tooltips, popovers and dialogs.
- `rounded-full` only for pictures of people and groups and for chart legend and tooltip
  swatches.
- Never `rounded-md`, `rounded-lg` or `rounded-xl`.

This narrows the brand spec's §5 on purpose, for the app only. There, buttons and inputs are
`rounded-md` and cards, panels and dialogs `rounded-xl`. Here controls go down to `rounded-sm` and
panels to square, so that the controls read as parts mounted on a flat panel, and the panels, which
share their edges, have no corners to meet at. The landing page and the docs site keep §5 as it is.

### 19.2 Density

No hardcoded heights. Strips use `--strip-h`, rows `--row-h`, controls `--control-h`, borders
`--hairline`, which is 2px in VR, and the grid and every strip follow it. On a phone every button,
field, dropdown and switch-bank segment is at least `--control-h` (44px) high, `sm` and `xs`
included; that floor lives in `index.css`, never in a component.

| | Dense | Comfortable | Phone | VR |
|---|---|---|---|---|
| `--control-h` | 30px | 36px | 44px | 48px |
| `--row-h` | 34px | 44px | 46px | 56px |
| `--strip-h` | 33px | 39px | 47px | 52px |
| `--text-base` | 13px | 14px | 14px | 18px |
| `--text-small` | 12px | 13px | 13px | 16px |
| `--text-tiny` | 11px | 12px | 12px | 16px |
| `--hairline` | 1px | 1px | 1px | 2px |
| An empty row | 51px | 66px | 69px | 84px |

---

## 20. A new page

A list page, top to bottom. Every name here is a part from §2; nothing on it is drawn by hand.

```tsx
export function Things() {
  // ...load the list, the filters and the page (see Members.tsx)

  if (error) return <Empty tone="danger">{error}</Empty>
  if (!list) return <Empty>Loading…</Empty>

  return (
    <div className="flex flex-col gap-3">
      <FilterBar properties={properties} chips={chips} onChange={setChips}>
        <Input ref={searchBox} value={typed} onChange={...} placeholder="Search by name or id" className="w-56" aria-label="Search things" />
        <Select value={sort} onChange={...} aria-label="Sort">...</Select>
      </FilterBar>

      <Card>
        <CardHeader>
          <CardTitle>Things</CardTitle>
          <CardAction>
            <span className="font-mono text-muted-foreground" style={{ fontSize: 'var(--text-small)' }}>
              {list.total.toLocaleString()}
            </span>
          </CardAction>
        </CardHeader>

        {list.rows.length === 0 ? (
          <EmptyRow>Nothing listed</EmptyRow>
        ) : (
          <Table pinFirst head={<><Th>Name</Th><Th className="text-right">Count</Th><Th>Last seen</Th></>}>
            {list.rows.map((row) => (
              <Tr key={row.id}>
                <Td>{row.name}</Td>
                <Td className="text-right font-mono">{row.count.toLocaleString()}</Td>
                <Td className="font-mono text-muted-foreground">{ago(row.seenAt, list.now)}</Td>
              </Tr>
            ))}
          </Table>
        )}

        <Pager at={at} pages={pages} />
      </Card>
    </div>
  )
}
```

A page of panels is one sheet instead:

```tsx
<div className="flex flex-col gap-3">
  <RangePicker range={range} onChange={setRange} from={data.from} to={data.to} />
  <PanelGrid className="grid-cols-1">
    <StatStrip>
      <Stat label="Joined" value={compactNumber(data.joined)} />
      ...
    </StatStrip>
    <PanelGrid className="lg:grid-cols-2">
      <Panel title="Joined and left per day">
        <DailyBars from={data.from} to={data.to} series={series} legend={legend} />
      </Panel>
      <Panel title="Top worlds" flush={rows.length === 0}>
        {rows.length === 0 ? <EmptyRow>No data yet.</EmptyRow> : <Table head={...}>...</Table>}
      </Panel>
    </PanelGrid>
  </PanelGrid>
</div>
```

A settings tab is a `SettingsSection` of `SettingsCard`s, each `span={6}` unless it holds a chart
or a wide table (`span={12}`), with its Save in `footer`. A new page also gets a title in the top
bar, an entry in `lib/nav.ts` and, if it has one, a go-to chord in `GO_TO_KEYS`.

Before a page is done, look at it four ways: 1440px wide in light and in dark, 390px wide on a
touch phone, and at VR density in dark. The mistakes this look is prone to show up in exactly
those: a doubled line where two edges meet, a box inside a box, a control of the wrong height, text
cut short at 390px or at VR.

---

## 21. What this replaced, part by part

For anyone reading an old screenshot or an old branch:

| Old | Now |
|---|---|
| `rounded-xl` card with `shadow-sm` and `py-6`, in a `grid gap-4` | square `Card` in a `PanelGrid` |
| uppercase, letter-spaced label over a big number | `Stat` |
| centred muted sentence in a tall box | `EmptyRow` |
| round pill badge with a round dot | `Badge` with a square |
| grey track with a white chip | `SwitchBank` or `Tabs` |
| rounded yellow warning box | tinted strip, or `Notice` |
| filled pill for the active sidebar entry | a 2px bar on the left edge |
| `rounded-md` buttons and fields at fixed `h-8`/`h-9` | `rounded-sm`, heights from `--control-h` |
| `components/ui/table.tsx` | `components/ui/data-table.tsx` |

---

## 22. What does not follow yet

Found by searching `src/Modbot.Web/src` on 2026-09-25. Each is a place where the code does
something this spec says it does not; the fix is to make the code follow the rule, not the other
way round.

### 22.1 Hand-written tables

Twenty `<table>`s in fifteen files draw their own column names and rows instead of using `Table`,
`Th`, `Tr` and `Td` (§7.1). Most copy the classes exactly; some keep their own local
`headClass` and `cellClass`:

- `pages/Members.tsx`, `People.tsx`, `Bans.tsx`, `DiscordMembers.tsx`, `Requests.tsx`,
  `RepeatOffenders.tsx`, `AuditLog.tsx`, `Giveaways.tsx`, `Health.tsx` (two)
- `components/audit/EntryDetail.tsx`, `components/insights/InsightBody.tsx` (two)
- `components/settings/IntegrationsSection.tsx`, `settings/api/ApiKeysPanel.tsx`,
  `settings/ai/AiMcpSettings.tsx`, `settings/ai/AiLimitsSettings.tsx` (four)

`components/Markdown.tsx`'s table draws a table inside a chat answer and is not a list.

Of those, these also break the heights in §7:

- `AuditLog.tsx`: the cells are `align-top` with `py-1` and `py-1.5`, so its rows are 39 to 51px
  at dense instead of growing only for their content.
- `ApiKeysPanel.tsx`: the column names are `h-(--row-h)`, a row high instead of `py-2`, and the
  cells are `py-1.5`.
- `Giveaways.tsx` (the draw's entrants): the column names are `h-(--strip-h)` and the cells `px-2`.
- `EntryDetail.tsx` and `InsightBody.tsx`: `py-1` rows with no `--row-h`; `InsightBody`'s column
  names have no strip and no side padding.

Two more reach their panel's edges with negative `--panel-pad` margins instead of `flush` (§7.1):
`IntegrationsSection.tsx`'s table and `BanReasonsCard.tsx`'s list.

### 22.2 Failures that read as empty

§10.3 says a failed load is a filled destructive square. These still use the hollow square:

- `Placeholder` with no `tone` in thirteen settings sections: `AiAlertsSettings`,
  `AiBaseSettings`, `AiChatSettings`, `AiInsightsSettings`, `AiLimitsSettings`,
  `AiMcpSettings`, `AutoModSection`, `WebhooksPanel`, `ApiKeysPanel`, `EvidenceSection`,
  `DataSection`, `SyncSection`, `VRChatProxySection`.
- An `EmptyRow` with no `tone` showing an error: `pages/AuditLog.tsx`, `Flags.tsx`,
  `CaseFile.tsx`, `RepeatOffenders.tsx`, `Empty` in `Logs.tsx`, and in settings
  `BanReasonsCard`, `PublicInstancesCard`, `discord/LinkingCard`, `discord/SyncCard`,
  `automod/TestSetDialog`.
- `PageMessage` has no `tone` at all, so every analytics page, Chat, Calendar, Live and Giveaways
  show a failed load the same as "Loading…"; `Nothing` has none either (`MemberCountChart`,
  `InstanceActivityChart`).
- A third way, in the popups: `EmptyRow className="text-destructive"` or `Empty
  className="text-destructive"`, which turns the words and the hollow square red.
  `SubjectHistory`, `subject/WorldPopup` (two), `subject/InstancePopup`, `subject/PersonPopup`
  (five), `subject/PersonNotes`, `subject/AccountSide`, `subject/ProfileVersions`,
  `subject/DiscordSide` (three).

### 22.3 Legends the page draws

§16.5 says the legend is the chart's `legend` prop. `pages/analytics/Instances.tsx` ("Opened and
closed per day") and `components/settings/ai/AiLimitsSettings.tsx` ("Daily spend") draw a
`Legend` above the chart themselves, so it stays when the chart is empty. `analytics/Worlds.tsx`
("Visitors per day, busiest worlds") does the same behind its own empty check.

Three more draw their legend by hand, as `size-2.5 rounded-full` spans, instead of `Legend`:
`pages/analytics/MemberCountChart.tsx`, `pages/analytics/InstanceActivityChart.tsx` and
`components/settings/MachineUsageCard.tsx`.

### 22.4 Browser controls

- Four groups of radio buttons are the browser's own (`<input type="radio">`), where §13.3 says
  one of several values is a `SwitchBank`: `pages/setup/GroupStep.tsx` (with
  `accent-[var(--primary)]`), `settings/automod/RuleFields.tsx`, `automod/TopicDialog.tsx`,
  `automod/RuleScopeFields.tsx`.
- Five `<details>` are not drawn as §11.6 says. Four still show the browser's triangle:
  `components/factSentence.tsx`, `insights/InsightBody.tsx`, `settings/api/EventsPanel.tsx`,
  `pages/setup/ConnectionStep.tsx`. `components/audit/EntryDetail.tsx`'s "Same decision" rows show
  neither the triangle nor the chevron.
- Three day fields are the browser's default box (§13.4): the filter bar's date editor
  (`filters/FilterBar.tsx`), `calendar/CalendarEventForm.tsx` and
  `settings/api/ApiKeysPanel.tsx`. The classes belong in one place (`Input` for `type="date"`)
  rather than a copy of `dayBox` in each.

### 22.5 Lists of facts

A list of facts is `Row`s (§17.6), never a `<dl>` grid. Two still are:
`components/audit/EntryDetail.tsx` (`grid grid-cols-[auto_1fr]`) and `CoverageNote` in
`pages/analytics/shared.tsx`.

### 22.6 Buttons and actions

- The page's one action is `sm` on Users (Add someone), Roles (New role) and Requests (Refresh),
  and the default size on Calendar (New event), Giveaways (New giveaway) and Logs (Refresh).
- Dialog footers put their buttons in two orders. `Requests`, `ModerationActions` and
  `chat/Conversations` put an outline Cancel first and the action last, at the right end.
  `automod/TopicDialog`, `automod/TermListDialog` and `AiBaseSettings`' confirmation put the action
  first and an outline Cancel after it, on the left.
- Roles' "Delete role" is a `ghost` button with `text-destructive`; every other destructive action
  is `variant="destructive"` (§12.1).

### 22.7 Toolbars

The Logs toolbar is plain controls on the left, with its search box `w-64`, where §8 puts a
`w-56` search box at the right end of the row from `md` up.

### 22.8 Sizes and faces

- Fixed text sizes: `subject/WorldPopup.tsx` (`font-display text-lg`), `pages/setup/GroupStep.tsx`
  (`text-[0.625rem]`), `chat/Answer.tsx` (`text-[0.95rem]`), `chat/Composer.tsx`
  (`md:text-sm`). The wordmark's `text-[0.9375rem]` in `Chrome.tsx` and `WizardChrome.tsx` is the
  brand's and stays.
- `charts/Heatmap.tsx` draws its cells `1.25rem` high a fixed `1px` apart, instead of following the
  density and the hairline.
- An age on a strip is in the body face where §17.2 makes it mono: `Freshness`'s "Last synced 3h
  ago" and Discord members' "Read 3h ago".
- The count on the Members and Bans strips sets its noun in mono too ("`1,204 people`"), where
  only the number is the machine part.

### 22.9 Pictures and badges

- A world's thumbnail is `rounded-sm` in `analytics/Worlds.tsx` and square in
  `components/InstanceTable.tsx`.
- "VRChat staff" in `ProfileBadges.tsx` is a `Badge` given the `info` tint by class, because
  `Badge` has no `info` variant.

### 22.10 Counts in a switch bank

Reviews writes its count in brackets ("Waiting `(3)`") and Flags without ("Open `3`").
