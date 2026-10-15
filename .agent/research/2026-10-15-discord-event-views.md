# Discord event and post views (measured 2026-10-15)

Measured in Discord's web app, dark theme, 100% zoom. Sizes in CSS px. Colours here are the
theme's lightness with its tint taken out (a custom tint was on); the mock-up uses these.
No server, event or person is recorded here: only sizes and counts.

## Events list (a server's "N Events" window)

| Part | Size |
|---|---|
| Window | 604 wide, 12 corners |
| Card | 554 x ~315 with a cover, 8 corners, 1px border, 16 padding |
| Cover | full card width x **120 high**, cropped (not the 2.5 : 1 shape) |
| Day and time | 14/18, weight 600, calendar icon; "Today at 5:00 PM", "Tomorrow at…", "Sat Oct 17th · 6:00 PM" |
| Title | 20/24, weight 500, **one line, cut with "…"** |
| Description | 14/18, weight 400, **4 lines then "…"** (72 high) |
| Location | 14/18, pin icon, one line |
| Buttons | "…", Copy Link, Interested: 32 high, 8 corners, rgba(151,151,159,0.12) |

## One event opened

| Part | Size |
|---|---|
| Content | 630 wide (window padding 24) |
| Cover | **630 x 240**, 8 corners |
| Heading | event title, 20/24 weight 600, close button |
| Tabs | "Event Info", "N Interested" |
| Rows | when (with end time), title 20/24 w600, server, location, "N people are interested", "Created by …" |
| Description | 14/18, every word |
| Buttons | 40 high, 8 corners |

## Message card (embed)

| Part | Size |
|---|---|
| Card | at most 516 wide, 4 corners, 4px colour bar on the left, padding 8 16 16 12 |
| Author line | 14, weight 600 |
| Title | 16/22, weight 600 |
| Field name / value | 14/18 weight 600 / 14/18 weight 400 |
| Footer | 12, icon 20 |

The "Link in chat" view (an event link pasted in a channel) is not measured yet; it is drawn from
Discord's invite card.

## What real events look like (39 upcoming events across 7 servers)

| Measure | Value |
|---|---|
| Title length | median 27, 75% under 42, longest 59; 8% over 50 (cut in the list) |
| Description length | median 109, 75% under 246, longest 895 |
| Over ~300 characters (cut by the 4-line limit) | about 1 in 4 |
| Over 500 | 10% |
| Empty description | 3% |
| With a cover picture | 51% |
| Title with brackets, e.g. "[VRChat, Group Public]" | 18% |
| Title with an emoji | 23% |
| Description with a link | 21% |
