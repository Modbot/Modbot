# Linear.app UI and UX findings

*2026-09-16.* What Linear (the issue tracker) does with layout, keyboard, filters, search and
detail panels, taken from its own docs and changelog where possible, and which of it fits Modbot.
Third-party sources are named as such; exact pixel values from reverse-engineered write-ups are
directional, not authoritative.

Sources: linear.app/docs (peek, select-issues, board-layout, filters, display-options, search,
custom-views, editor, editing-issues), linear.app/changelog (2021-03-25 keyboard shortcuts help,
2021-06-03 issue view layout), linear.app/now ("A calmer interface for a product in motion",
"How we redesigned the Linear UI"), keycombiner.com and fastshortcuts.com (third-party shortcut
lists), performance.dev ("How's Linear so fast?", third-party).

## 1. Layout and density

- **Content first, chrome dimmed.** The 2026 refresh dims the sidebar so the main area "takes
  precedence". Tabs are compact, icons were shrunk everywhere, coloured icon backgrounds removed.
  The stated aim is dense information without an overwhelming frame.
- **"Structure should be felt, not seen."** Dividers are low-contrast hairlines with rounded ends;
  surfaces differ by a small step in lightness rather than by borders.
- **Type.** Inter for body, Inter Display for headings. Body text is small (13px-ish per
  third-party measurement); rows are roughly 36px in the list.
- **Colour.** An LCH-based theme built from three inputs (base, accent, contrast). Dark mode is a
  warm grey, not blue-black. Light and dark are the same design, not an inversion.
- **Hover-revealed controls.** Row actions and secondary controls appear on hover; the resting row
  is text and a few property chips.
- **Peek.** `Space` opens a Quicklook-style preview of the highlighted item over the list; `↑`/`↓`
  move to the next item while the preview updates; `Esc` closes. Holding Space keeps it open only
  while held.
- **Detail page.** A centred main column with a readable max width, and a right-hand properties
  panel that widens as the window widens (2021 changelog). Title and description are edited
  inline.
- Third-party token write-ups describe a near-black canvas, a lavender accent, 1px hairline
  borders and a 4px spacing scale. Treat as approximate.

## 2. Keyboard model

There is no single official shortcuts page; the in-app sheet on `?` is the canonical list.
Confirmed from Linear's own docs:

| Key | Does |
|---|---|
| `Cmd/Ctrl+K` | Command menu: every action by name, scoped to the selection when there is one |
| `?` | Searchable shortcut sheet |
| `↑`/`↓`, `J`/`K` | Move the highlighted row |
| `X` | Select the highlighted issue; `Shift+↑/↓` extends; `Cmd/Ctrl+A` selects all; `Esc` clears |
| `Enter` / `O` | Open the highlighted item |
| `Space` | Peek |
| `F` | Open the filter menu; `Shift+F` clears the last filter; `Alt+Shift+F` clears all |
| `/` | Search |
| `Cmd/Ctrl+F` | Search within the current view |
| `Cmd/Ctrl+B` | Toggle list/board |
| `Shift+V` | Display options |
| `Alt+V` | Save the current filters as a view |
| `C` | New issue; `E` edit; `A` assign; `S` status; `P` priority; `L` label |

Go-to chords, `G` then a letter (from third-party lists, consistent with the docs): `G I` Inbox,
`G M` My issues, `G A` Active, `G B` Backlog, `G E` All issues, `G D` Board, `G C` Cycles,
`G P` Projects, `G S` Settings. Open chords, `O` then a letter: `O F` favourite, `O P` project,
`O U` user, `O T` team.

Rules that fall out of the docs:

- Single-letter shortcuts never fire while typing in a field.
- Chords have a short timeout; the first key shows nothing.
- The command menu is the fallback for everything without a key, and it searches by name.

## 3. Filter bar

From linear.app/docs/filters and display-options:

- **Flow.** Filter button or `F` → pick a **property** (assignee, status, label, …) → an
  **operator** → a **value picker**. Typing in the popover jumps straight to a property or value
  by name.
- **Operators depend on the property.** Single-value: *is / is not*. Multi-value: *is any of /
  is not*. Sets (labels): *includes any / all / neither / none*. Dates: *before / after*.
- **Chips.** One chip per property, drawn as `[icon] property · operator · values`. Clicking the
  operator switches it; clicking the values opens the picker to add or remove. A second value on
  the same property widens the operator (*is* becomes *is any of*) — OR within a property, AND
  across properties.
- **Advanced filters** allow nested AND/OR groups. Natural-language filtering ("issues assigned
  to me") maps to chips.
- **Clear.** `Shift+F` clears the last chip, `Alt+Shift+F` clears all; a clear control sits at
  the end of the chip row.
- **Persisted in the URL.** Copying the address reproduces the filtered view.
- **Saved views.** Any filtered list can be saved (`Alt+V`), starred into the sidebar, shared
  with a team, and keeps its display options with it.
- **Display options** are separate from filters: grouping, ordering, which properties a row
  shows, and toggles such as *show completed*. *Set as default* stores them for the workspace.

## 4. Search

`/` opens search over issues, projects, documents and people. With nothing typed it shows recent
searches and recently viewed items. Quotes match exact phrases; results cap at 500. Inside the
command menu, a letter prefix scopes the search (issues, projects, users, …).

## 5. Detail panels

- Peek shows the main fields of the item without leaving the list.
- The full page has activity and comments in the main column; description version history is
  reachable from the overflow menu.
- Copy actions are ID, branch name and URL (`Cmd/Ctrl+.`, `Cmd/Ctrl+Shift+.`,
  `Cmd/Ctrl+Shift+,`). **No "copy as JSON" action exists in Linear**; that is a Modbot addition.
- A collapsed "show more" on activity could not be confirmed from Linear's own docs.

## 6. Speed (brief, third-party)

Local-first sync: mutations apply to the browser's own store first, then sync; field-level
observables so one change re-renders one row; route chunks preloaded; animations limited to
transform and opacity at 100–150ms. The exact figures are the secondary source's.

## What applies to Modbot, and what does not

**Applies**

- The keyboard model, nearly whole: `Ctrl/Cmd+K` command palette, `?` sheet, `g` then a letter
  to go to a page, `j`/`k` and arrows to move in a list, `Enter` to open, `Esc` to close, `f` for
  the filter bar, `/` for search, and the rule that letters never fire inside a field. Modbot's
  pages are lists a moderator scans, which is the case these keys were built for.
- The filter bar shape: add → property → operator → values, one chip per property, AND across
  chips, OR within a chip, kept in the URL, with a clear control. The audit log, the two member
  lists and the ban list all have server-side filters already; they need the control, not new
  data.
- Counts beside values where the server already knows them (roles on a member list, recent
  actors on the audit log).
- Remembering the last-used filters per page. Linear does this per view; Modbot can do it per
  page in localStorage without a saved-views feature.
- Peek-style detail: Modbot's popup already opens over the page and keeps the page mounted
  (spec 10.2). `j`/`k` while a popup is open moving to the next row is the same idea as Linear's
  peek arrows.
- Hover-revealed row actions, low-contrast hairlines, small body text: Modbot's dense density
  already follows this (`index.css`); nothing to change, only to keep.
- A right-hand properties column on a detail view: Modbot's popup has identity on the left and
  tabs on the right, which is the same split mirrored. Widening the popup is in keeping with
  Linear letting the properties panel grow with the window.

**Does not apply**

- Saved custom views, workspace defaults, and Slack-wired views. Modbot is a single-group
  appliance with a handful of moderators; per-page memory in the browser is enough.
- Display options as a separate menu (grouping, swimlanes, ordering by many keys). Modbot's lists
  have one or two useful sorts; a sort control inside the filter bar is enough.
- Selection (`x`, `Shift+↑/↓`, `Cmd+A`) and bulk actions. Modbot's "act on many at once"
  permission exists but the actions are not built; selection can come with them.
- Issue-editing keys (`c`, `e`, `a`, `s`, `p`, `l`). Facts are immutable and nothing on a list
  row is editable in place.
- Local-first sync and offline mutations. Modbot's lists are server-paged reads over a
  partitioned fact table; a browser-side copy of the audit log is the wrong shape.
- Natural-language filters and nested AND/OR groups. Chat already answers questions in words;
  the filter bar stays flat.
- LCH theme rebuild. Modbot's tokens are already validated for both themes and VR; changing the
  palette is not what was asked for.
