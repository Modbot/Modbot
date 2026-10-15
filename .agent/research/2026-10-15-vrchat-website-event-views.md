# vrchat.com event views, measured 2026-10-15

Method: Claude in Chrome, signed-in vrchat.com, read-only (only navigation tabs, Month view, Filters open/close, event popup open/close were clicked). Viewport 1920x911, devicePixelRatio 1. Numbers are `getBoundingClientRect` / `getComputedStyle` in CSS px. Font family on every element: `VRCCustom` (VRChat's own web font). Screenshots (kept locally in Documents/Modbot references/vrchat-website, not in the repo) are 1568x744 downscaled from the 1920 viewport (x0.817), so measure from the numbers here, not from the images.

Shared palette: panel/card `#252a30`, panel body `#181b1f`, page text `#f8f9fa`, white text `#ffffff`, muted text `#9c9c9c`, teal button `#064b5c`, link `#0e9bb1`, Featured tag `#b8860b`, category tag `#ffffff` bg with `#252a30` text, calendar grid line `#2d363f`.

## 1. Group page, Events tab
URL `.../group/grp_c660.../events` (tab "Events" between Overview and Store). Screenshot: [1-group-events-tab.jpg](file:///C:/Users/Zuelatak/Documents/Modbot%20references/vrchat-website/1-group-events-tab.jpg)

Layout: content column 1310 wide (home-content max 1350). Heading bar 1310x50, `#252a30`, radius 8 8 0 0, padding 6.4/14.4. Body panel `#181b1f`, radius 0 0 8 8, padding 20/14.4/6.4. Cards stacked in one column (a grid/list toggle button top right), gap 16. Filter row: two dropdowns, "Display: Upcoming", "Order: Newest First".

| Element | Size | Font | Colour | Radius / padding |
|---|---|---|---|---|
| Card | 1275x340 (text-heavy; height follows the description) | 16/24 | bg `#252a30` | r8, p4, no border |
| Picture | 355x200, natural 512x288 = **16:9**, object-fit cover | | | r6, 4px inset from card edge |
| Category tag ("Film & Media") | 120x24, over picture top-left, 16px in | 16/24 bold | `#252a30` on `#ffffff` | r6, p 0 8 |
| Featured tag | 88x24, below category tag (gap 4) | 16/24 bold | `#ffffff` on `#b8860b` | r6, p 0 8 |
| Title | 711x28 | 20/28 bold 700 | `#ffffff` | text starts 16px right of picture (x+371 in card) |
| Description | 711x192 (8 lines at 24) | 16/24 regular | `#f8f9fa` | 8px below title |
| Date/time ("Oct 15, 2026 at 4:30 PM") | 175x24 | 16/24 regular | `#9c9c9c` | preceded by a 16px repeat icon; bottom of card, y+300 |
| Access ("Public") | 63x24 | 16/24 | `#9c9c9c` | after a "\|" divider |
| Platform icons (3) | 24x24 circles, gap 4 | | borders `#1778ff` (Windows), `#2bcf5c` (Android), `#ffffff` (Apple) | 1px border, full round |
| View button | 178x38, top right, 16px from card edge | 16/24 regular | `#ffffff` on `#064b5c` | r4, p5, 2px border `#064b5c` |
| Section heading ("Events") | | 24/26.4 bold | `#f8f9fa` | in the 50px bar |
| Dropdown | 194x40 | value 16/24 `#ffffff`; label ("Display", "Order") 12.8/19.2 `rgba(255,255,255,.6)` | | bg `#064b5c`, r3, p 5/5/5/15 |

Not on this card: group name, Interested count (the list card shows neither). Not measured: grid-view layout (toggle not clicked).

## 2. Event page
URL `.../group/grp_c660.../calendar/cal_2c993ba6...` (event "Carpe D-Em: Seize The Mic Karaoke - with Mr.D!"). Screenshots: [top](file:///C:/Users/Zuelatak/Documents/Modbot%20references/vrchat-website/2-event-page-top.jpg), [details, scrolled](file:///C:/Users/Zuelatak/Documents/Modbot%20references/vrchat-website/2b-event-page-details.jpg)

Hero block 1310x530, picture fills it fully (img 1310x530, natural 1024x576 = **16:9**, cover, so it is cropped to about 2.47:1; the bottom 200px is covered by a `#111111` at 90% overlay, padding 20). Overlay content, left to right: tags, date, title, group line; "Add to Calendar" button top right.

| Element | Size | Font | Colour | Radius / padding |
|---|---|---|---|---|
| Hero (picture + overlay) | 1310x530; overlay 1310x200 at y+330 | | overlay `#111111`/0.9 | p20 |
| Category tag ("Other") | 63x24 at (20, 343) | 16/24 bold | `#252a30` on `#ffffff` | r6, p 0 8 |
| Featured tag | 88x24, gap 8 after category | 16/24 bold | `#ffffff` on `#b8860b` | r6, p 0 8 |
| Date ("Oct 16, 2026") | 113x28 | 20/28 weight 100 (thin) | `#ffffff` | |
| Title (h1) | 650x80 (2 lines) | 30/36 bold | `#ffffff` | |
| Group avatar | 24x24 | | border 2px `#1fd1ed` | full round |
| Group name ("MondoCat") | 80x22 | 16/24 regular | `#1fd1ed` (link) | |
| Access ("Public") | 63x26 | 16/24 | `#9c9c9c` | |
| Platform icons | 24x24 x3, gap 4 | | `#1778ff` / `#2bcf5c` / `#ffffff` | 1px border, round |
| Add to Calendar | 254x38 | 16/24 | `#ffffff` on `#333333` | r4, p5, border 2px `#333333` |
| Panels row | grid, 2 columns of 647 each, gap 16 | | | |
| Panel heading bar ("Details", "About This Event") | 647x58 | 24/26.4 bold | `#ffffff` on `#252a30` | r 8 8 0 0, p14.4 |
| Panel body | 647 wide (Details 543 high, About 672 high) | 16/24 | `#ffffff` on `#181b1f` | r 0 0 8 8, p20 (Details), p14.4 (About) |
| Sub-heading ("Date and Time", "Repeating Event", "Interested People", "Languages", "Tags") | 32 high, 16px icon at x+23 | 16/24 bold | `#ffffff` | rows ~68-96px apart |
| Date and time value | 248x24 | 16/24 | `#ffffff` | indented 43px |
| Export Event / View Event Series links | 22 high | 16/24 | `#0e9bb1` | |
| Interested count (a bare "3" under "Interested People") | 140x24 | 16/24 | `#ffffff` | |
| Language / tag chips | 32 high (e.g. "English" 79x32, "Karaoke" 87x32), gap 8 | 16/24 | `#ffffff` | bg `#181b1f`/0.8 (dark), r2, p 4 12 |
| About text | 612x624 | 16/24 | `#ffffff` | |
| "Organized by" card | 647x312, bg `#181b1f` | heading 16/24 bold | | r6, p20; inner row 607x88 `#252a30`, r4, p12 |
| Organizer avatar | 64x64 | | border 2px `#0e9bb1` | full round |
| Organizer name / member count / code | | 16/24 bold / 16/24 `#9c9c9c` | | |
| Join button | 56x38 | 16/24 | `#ffffff` on `#064b5c` | r4 |

Not on this page: an Interested button (the viewer is not the host; none rendered) and any count text beside it, so the only count is "Interested People: 3".

## 3. Calendar
URL `https://vrchat.com/home/calendar`. The default week (Oct 11-17) showed events only after scrolling to 5pm; Month view shows them directly, so Month was used. Screenshots: [month](file:///C:/Users/Zuelatak/Documents/Modbot%20references/vrchat-website/3-calendar-month.jpg), [week, top, empty](file:///C:/Users/Zuelatak/Documents/Modbot%20references/vrchat-website/3b-calendar-week-empty.jpg). The filter panel (View: Added/Featured/Groups; Access Type; Category; Day; Language; Groups) was opened and closed, nothing ticked; events were already showing without any filter, so no "discover" switch was needed. Tool is FullCalendar.

| Element | Size | Font | Colour | Radius / padding |
|---|---|---|---|---|
| Page heading ("Calendar") | | about 28 bold (not measured exactly) | `#ffffff` | |
| Calendar panel | 1310x1109 | | bg `#181b1f` | r4, p20 |
| Create Event button | 208x40 | 16/24 | `#ffffff` on `#064b5c` | r4, p5, border 2px |
| Filters button | 86x40 | 16/24 | `#6ae3f9` on `#064b5c` | r4 |
| Search box | right-aligned, about 520 wide | placeholder 16 | | border teal |
| Month title ("October 2026") | 178x31 | 28/30.8 regular | `#f8f9fa` | |
| Prev/Today/Next | 41x35 | 16/24 | `#ffffff` on `#333333` | r4, p 5/10/4 |
| Month/Week/Day toggle | 90x32 active | 16/22.4 | `#ffffff`; active bg `#064b5c`, inactive `#333333` | r4 |
| Weekday header | 181x40 | 16/24 bold | `#f8f9fa` | |
| Day cell | 181x171 | | border 1px `#2d363f` | r0 |
| Day number | 38x38 | 25.6/38.4 weight 300 | `#ffffff` | p4; today = filled `#1fd1ed`-like circle |
| Event row (month) | 176x26 | 14/20 | `#ffffff` | r8, p 2 0 2 4, 1px border at 50% |
| Event title in row | 154x20 | 14/20 bold | `#ffffff` | truncates with no ellipsis visible |
| Dot before title | 8x8 | | `#064b5c` | round (4px) |

Month rows show title only: no time, picture, group or tags.

## 4. Calendar popup (click an event)
Opened by clicking "Scary Movie 2 Watch Party" on Oct 13. Screenshot: [4-calendar-popup.jpg](file:///C:/Users/Zuelatak/Documents/Modbot%20references/vrchat-website/4-calendar-popup.jpg)

It is the same card component as view 1, stacked: picture on top, text below. Fixed-position wrapper 340x373, shadow `0 2px 6px rgba(0,0,0,.15)`, no padding of its own.

| Element | Size | Font | Colour | Radius / padding |
|---|---|---|---|---|
| Card | 340x373 | | bg `#252a30` | r8, p4 |
| Picture | 332x187, natural 512x288 = **16:9**, cover | | | r6 |
| Category tag ("Film & Media") | 120x24 at (16,16) | 16/24 bold | `#252a30` on `#ffffff` | r6, p 0 8 |
| Close button | 36x36 at (288,16), top right over picture | | `#333333`, 2px border | r4, p5, x icon 12x16 |
| Title | 308x28 | 20/28 bold | `#ffffff` | 16px side inset, y+171 |
| Group avatar | 32x32 | | bg placeholder `#6ae3f9` | full round (50%) |
| Group name | 158x24 | 16/24 regular | `#ffffff` | 4px right of avatar |
| Date/time | 175x24 | 16/24 | `#9c9c9c` | clock icon 16px at x+16 |
| Access ("Public") | 63x24 | 16/24 | `#9c9c9c` | |
| View button | 308x38 at (16,319) | 16/24 | `#ffffff` on `#064b5c` | r4, p5, border 2px |

Not seen: a Featured tag or an Interested count in the popup. The event I opened is not Featured and had no interest figure showing; a Featured event's popup was not opened, so I did not see where the tag sits there (on the list card it sits under the category tag). Platform icons were not shown in the popup.

## Could not reach / not done
- Interested count on list cards and in the popup: not shown in either.
- Featured tag on the popup: not opened on a featured event.
- Grid-view toggle on the Events tab: not clicked.
- Exact font size of the "Calendar" page heading, search box dimensions, popup on a repeat-group event: not measured.
- Hover states, mobile widths: not checked.
