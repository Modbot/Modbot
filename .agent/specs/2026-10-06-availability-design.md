# Modbot — Availability

- **Date:** 2026-10-06
- **Status:** Built
- **Covers:** The Availability page: each staff member's weekly free hours, the team's heatmap, the
  two permissions, how a week is stored and moved between time zones
- **Depends on:** accounts and access design (roles, the permission bitfield), calendar design (the
  time zone database the server already uses)
- **Narrows / reverses:** nothing recorded. It adds two permissions and two tables.

---

## 1. What this is

Staff with a Modbot login say which hours of the week they are free, and the team's hours are shown
together so a moderator can find a time: a meeting, an event that needs staff, a time somebody is
likely to be around.

Two tabs on one page:

- **Mine**: a person's own week, painted on a grid and saved with a button.
- **Team**: a heatmap of how many are free in each hour, who they are, the best stretch of each day,
  and filters.

Nothing here reaches VRChat or Discord, and nothing here acts. It is read by people.

---

## 2. Decisions

| # | Decision | Why |
|---|---|---|
| 1 | **A week that repeats**, not dates | "Tuesdays 18:00 to 22:00" is how volunteers describe themselves, and a repeating week is a one-minute job done once. A date-by-date calendar is a chore nobody keeps up, and an out-of-date one is worse than none. |
| 2 | **One-hour cells**, Monday 0 to Sunday 6, hours 0 to 23 | Event times are whole hours in practice, and 168 cells is the most a person can ever have to paint. Half hours would double the grid for a gain nobody asked for. |
| 3 | **Two states, Free and If needed** | "Could do it if I had to" is what people actually want to say, and without it they under-report. It is counted as free only when the person looking asks for that. No row at all means not free, so a person's week is only the hours they marked. |
| 4 | **Two permissions**: Enter availability (bit 55), See availability (bit 56) | Entering your own times and seeing everyone's are different powers, and a team's weekly habits are about named people. Neither implies the other. Neither is in a built-in role, like the other permissions that reach beyond day-to-day moderation. Administrator holds both. |
| 5 | **"The team" is whoever may enter times** | Everyone whose roles include Enter availability (or Administrator) and whose account is neither disabled nor deleted. It is worked out from the roles each time rather than kept as a list, so taking the permission away takes a person off the heatmap at once. A person who has not saved yet is on it with no hours, so "3 of 8" counts them. |
| 6 | **Each person's week is stored in their own time zone** | See §4. |
| 7 | **The server refuses by permission, whatever the sidebar shows** | Mine needs Enter. Team needs See. A person with only Enter never receives anyone else's times, and one with only See is not on the team. A save only ever touches the caller's own rows: the account is the session's, never the body's. |
| 8 | **Replace, not patch** | Saving sends the whole week (at most 168 hours) and replaces what was stored, in one transaction. There is no partial state to reason about. |
| 9 | **No facts, no audit entries** | A person's own free hours are not moderation history. Nothing in the audit log or the event stream changes when somebody saves. |
| 10 | **Names are the account's username** | The same name the Users page and the Calendar's staff picker show. |

---

## 3. Storage

| Table | Holds |
|---|---|
| `staff_availability` | One row per person per marked hour: user (cascades with the account), day 0 to 6 (Monday 0), hour 0 to 23, state `free` or `ifNeeded`. The key is (user, day, hour), so an hour cannot be marked twice. |
| `staff_availability_zone` | One row per person: the IANA zone their hours are in, and when they last saved (from `IModbotClock`). |

The zone is its own small table, so the busy `modbot_user` table is not altered.

---

## 4. Time zones

**Why own-zone storage.** A habit is a habit of somebody's own clock. If a week were stored as UTC
instants, "Tuesday 18:00" would move by an hour for the whole of a person's winter every time their
country changed its clocks, and the person would have to notice and repaint it twice a year. Stored as
the hour on their own clock it does not move, and whoever is looking converts it for the dates they
are looking at.

**The way across** is one small module (`availabilityZones.ts`) with no date library, only `Intl`:

1. The viewer's grid is one real week, seven dates from the Monday of the week the page opened in,
   by the viewer's chosen zone (the browser's to begin with).
2. Each hour of that grid is an instant. The hour the viewer's clocks skip has none (the hour is left
   empty); the hour they repeat has two (either being free counts, free winning over if needed).
3. The person's hour that fills a viewer's hour is the one that **starts within a half hour either
   side** of it: the person's hour start rounded to the nearest viewer hour, half rounding up.

Consequences:

- A change of clocks in either zone moves a person on the days after it and not before.
- Every hour a person marks is exactly one hour on the viewer's grid, including for a zone on the
  half or quarter hour. Smearing it over two hours would double-count, and counting only whole
  covered hours would lose any lone hour. The cost is that such a person is shown up to half an hour
  off, which an hour grid cannot say anything finer about.
- A person's hour can land on the other side of midnight (Monday 02:00 in Auckland is Sunday
  afternoon in London), so the day can change.

The server never converts. It stores and returns each person's week and zone as they are; the zone is
checked against the zone database the Calendar already uses.

---

## 5. The pages

**Mine.** Time zone, a choice of **Free**, **If needed** or **Erase**, the week, and **Save**. No
automatic save: **Not saved** and **Saved** say where it stands. A mouse paints by dragging. A finger
taps a cell, or holds for a moment and then drags, the calendar's press handling, so a quick swipe
still scrolls the page. Pressing a cell that already holds the chosen paint clears it, and so does the
rest of the stroke. Under a phone's width the grid is turned, hours down and days across, so seven
columns are each wide enough to hit and the page never scrolls sideways. The keyboard works: arrow
keys and Enter or Space.

**Team.** The heatmap has days down and hours across (turned on a phone). Each hour is shaded in four
steps by the share of the people shown who are free in it, with the count in the cell, so the shade is
never the only signal. The scale is relative to the people shown. Hover or tap shows who is free, with
**(if needed)** after those who are. **Best times** is the longest run of the day's best hours, as
**Tue 18:00-22:00 · 5 of 8 free**, best day first.

Filters, all client-side: role, people, day (every day, weekdays, weekend), hours (all, daytime
06:00-18:00, evening 18:00-midnight, night midnight-06:00), the fewest free at once (any, 2, 3, 4), and
whether if needed counts as free. The viewer's time zone is chosen at the top.

The UI text is labels, headings, the plain empty states and errors, as CLAUDE.md asks.

---

## 6. Out

- **Specific dates.** Away next Thursday is not expressible. A second layer of exceptions would be
  the date-by-date chore decision 1 avoids, and can be added on top later without changing this.
- **Calendar sync**, in either direction. Nothing is read from or written to Google Calendar or the
  Modbot Calendar, and the Calendar does not use availability to invite or schedule anyone.
- **Notifications and reminders** to fill in a week.
- **A history** of past weeks.
- **A week switcher.** The heatmap shows the week the page opened in. Looking at next week, where a
  change of clocks may fall, is a control that was not asked for.

---

## 7. Permissions

| Flag | Bit | Opens |
|---|---|---|
| `EnterAvailability` | 55 | The Mine tab, `GET` and `PUT /api/availability/mine` |
| `ViewAvailability` | 56 | The Team tab, `GET /api/availability` |

The sidebar entry shows for either (`needsAny`); which tabs there are is the page's own check.
