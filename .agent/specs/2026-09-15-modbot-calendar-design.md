# Modbot Calendar

- **Date:** 2026-09-15
- **Status:** Built
- **Covers:** planning events in Modbot, publishing them to VRChat's calendar, Discord and a
  channel, opening the instance on time, and a calendar feed; reading VRChat's calendar back in
  (§12, added 2026-09-27); where an event goes, its preview, places not set up and the cancel
  post (§14, added 2026-10-01); cancelling or changing one date of a repeating event (§2.2,
  added 2026-10-01)
- **Depends on:** foundation §4.1 (gate), §4.3 (rate limits), §4.4 (clock), §5.9 (facts);
  M6 (instances, `PlaceStore`, instance cards); Discord event routes (channel picker)

---

## 1. What it is

A group plans its events in Modbot once. Modbot then puts each event where members look:

1. **VRChat's own group calendar.**
2. **A Discord server event**, with the join link as its location once the instance is open.
3. **A post in a Discord channel**, in the same style as the instance cards, with a Join button
   once the instance is open.
4. **A calendar feed** (an `.ics` link) that anyone can add to Google Calendar, Outlook or Apple
   Calendar.

And a few minutes before an event starts, Modbot **opens the group instance** by itself, so the
join link is ready when people arrive.

**This narrows M6 §6.** M6 listed "scheduled or recurring automatic launches" as a non-goal,
because an instance opening with nobody from the team there is a moderation problem. The
maintainer asked for it. It is per event, off unless ticked, and opens only a group instance with
the access the event names — never public by default, and never for an event nobody scheduled.

## 2. The event

| Field | Notes |
|---|---|
| Title, description | Title up to 100 characters (Discord's limit for an event name), description up to 1000. An event going to VRChat needs a description: VRChat answers an empty one with a 400 (seen 2026-09-25). A draft may stay empty until it is published. |
| Start, end, time zone | Stored as the first start and end (UTC instants) plus an IANA time zone. The time zone is what keeps a weekly 20:00 event at 20:00 through daylight-saving changes. |
| Repeat | `none`, `daily`, `weekly` on chosen days, or `monthly` on the same day of the month. An optional last date. **The rule is stored, not the occurrences.** A monthly event on the 31st skips months without one, the same as iCalendar. |
| World | Picked from worlds Modbot knows, or typed as an id. Never checked for shape (foundation §3.1.1). Or picked from a world list, date by date (world lists design, added 2026-10-01): `WorldId` is then the current date's pick. |
| Instance access, region | `members`, `plus` or `public`; `us`, `use`, `eu` or `jp`. |
| Image | A picture link for Discord, and optionally a VRChat file id for VRChat's calendar. Modbot does not upload files to VRChat — that endpoint has no rate limit set. |
| VRChat calendar fields | Category, languages, platforms, tags, who can see it (`group` or `public`), and whether VRChat notifies group members. Exactly the fields `CreateCalendarEventRequest` has that make sense to set. |
| Where it goes | VRChat calendar, Discord event, channel post (with the channel), open the instance and how many minutes early (default 10). A new event starts with VRChat calendar and Discord event ticked (changed 2026-09-27: with only VRChat ticked, events reached one side unless someone remembered the second box). The channel post starts off, since it needs a channel picked. In the form these are one row of chips, with the calendar feed shown and always on (§14). |

### 2.1 States

| State | Meaning |
|---|---|
| `draft` | Saved, published nowhere, never opened. |
| `scheduled` | The next occurrence has not started. |
| `open` | The next occurrence has started — or its instance opening time has come — and has not ended. |
| `finished` | No occurrences are left. |
| `cancelled` | A person cancelled it. Removed from every place it was published; the feed shows it as cancelled for 30 days (§6). |

A repeating event goes `scheduled → open → scheduled` for each occurrence and only becomes
`finished` after the last one. The event row keeps the start of the **current occurrence**, so
every place knows which one it is about.

Deleting an event is a cancel that also hides it from the calendar page. The row stays, because
the facts about it point at it.

### 2.2 One date on its own (added 2026-10-01)

Until 2026-10-01 a repeating event was its rule and nothing else, so one date could not be skipped
or moved: a drag moved every date, and the page said so (§11). The maintainer asked for Google
Calendar's "This date / All dates". This narrows §2's "the rule is stored, not the occurrences":
the rule is still stored, plus **the dates changed on their own**.

- **`calendar_date_change`**, one row per changed date: the event, **`planned_starts_at`** (when the
  rule says that date starts), whether it is **cancelled**, and otherwise its own start and end
  (null keeps the planned time), title and description (null uses the event's). Loaded with the
  event on every read (EF `AutoInclude`), because every place an event goes has to follow them and
  a place that forgot to ask would silently be wrong.
- **A date is known by its planned start everywhere**, the way iCalendar's `RECURRENCE-ID` names the
  date an override replaces. A moved date keeps its Discord event, its post and its VRChat id; only
  what they say changes.
- **The list of dates** (`CalendarRepeat.Between`) leaves a cancelled date out and puts a moved one
  at its new time, in order. The timeline (§2.1), the opener (§4), the feed (§6), Past events (§13)
  and the page all read it, so none of them has a rule of its own for this. `PlannedBetween` is the
  rule alone, for the few places that mean the rule (VRChat's "after N times", checking a date
  exists). `CalendarEvent.OccurrenceStartsAt` stays the date's **actual** start, which is what an
  opening is keyed by.
- **Changing the whole event afterwards** (an edit, an "All dates" drag, or a change read from
  VRChat) moves each changed date to the rule's date **on the same day** in the event's zone, and
  drops one on a day the rule no longer has. By day rather than exact time, so moving a weekly series
  an hour later keeps the Sunday cancelled in it cancelled. A date that is over is left as it ran.
- **The API:** `PUT /api/calendar/events/{id}/dates` (times, title, description of one date) and
  `POST /api/calendar/events/{id}/dates/cancel`. Only a repeating event that is not cancelled or
  finished, only a date the rule has, only one that has not ended, and never onto another date's
  start (two dates starting together could not be told apart by an opening or the page). A date
  that has opened keeps its start (its Discord event has started, and Discord cannot move a started
  event); its end can still change. A date put back exactly as planned loses its row. Facts `modbot.calendar.date.change` (before and after)
  and `modbot.calendar.date.cancel` (§8).
- **A cancelled date cannot be brought back from the page.** Not asked for; it can be added later.
- **The page:** Edit, Cancel and a drag on a repeating event ask **This date / All dates**. A
  cancelled date is drawn struck through; an opened moved date says where it was moved from.

## 3. Where it is published, and how changes flow

Each event has one row per place (`calendar_event_place`) holding the outside id (VRChat's event
id, Discord's event id, the channel message id), a state, and the last error:

| State | Shown as |
|---|---|
| `waiting` | Waiting — queued, or held by a rate limit |
| `published` | Published |
| `failed` | Failed, with VRChat's or Discord's words |
| `removed` | Removed after a cancel or when the place was turned off |

Each place remembers a **fingerprint** of what it last wrote. A place is written only when what it
should say differs from that fingerprint. That one rule covers edits, the instance opening, the
event finishing and cancelling: each changes what the place should say.

### 3.1 VRChat's calendar

- **Several quick edits become one update.** An event is not written to VRChat until it has gone
  unchanged for 20 seconds, and then its latest version is sent. With one write a minute allowed,
  a moderator fixing a typo three times costs one write, not three.
- A repeating event is sent as **one VRChat series** with VRChat's own recurrence (daily, weekly
  on days, monthly; interval 1; the event's time zone; an end date when there is one), not one
  VRChat event per occurrence.
- **One date changed on its own (§2.2)** is sent to that date, not the series. VRChat lists a
  series' dates as `occurrenceKind: occurrence` rows with an `id` of their own and the series' id as
  `seriesId` (the SDK's `CalendarEvent`); a cancelled date is deleted by that id and a moved or
  reworded one updated by it (no `recurrence`, the series' other settings, the date's times and
  words). These are the delete and update calls the series already uses, on the same
  `calendar.write` budget; the id is found by reading the date's month with `GetGroupCalendarEvents`
  on `calendar.read` -- every page, up to the calendar page's own three, by the reader's own paging
  -- plus the neighbouring month when the date is within 14 hours of the edge and the month it was
  moved to, and kept. No new endpoint. The id must be a dated row of **this** series and never the
  series' own id, because deleting that would take every date off VRChat. **A date not found is
  shown as failed on that date**, cancelled or not, and not looked for again until the date changes:
  taking "not found" as "nothing to take off" would mark a cancel sent while the date could still be
  on VRChat (a later page, a month edge). Dates wait for their series: nothing is sent for a date
  while the series itself has a write waiting, and after every create or update of the series each
  date still to come is looked for and sent again, since whether VRChat keeps a changed date through
  a series update is not known -- except a cancelled date before where the series now starts, which
  the series no longer holds. A finished event's dates are still sent: an event finishes when its
  last date is cancelled, and that date's delete has to go out. Settling (20 s), one write a pass,
  and the rules for refusals and no answers are the series' own.
- Cancelling, deleting, or unticking VRChat deletes the event on VRChat. A finished event is left
  there: it is history on VRChat's side as well.
- A write VRChat refuses is not sent again until the event changes. A write that got no answer
  (timeout, VRChat's own 5xx) is tried again after 15 minutes. What VRChat said
  (`error.message` in its reply) is what the event's VRChat status shows, not the bare HTTP reason.
- **A create with no answer may still have made the event.** VRChat has answered a create with a 500
  (seen 2026-09-25), and a 500 says nothing about whether it saved. Before that create is sent again,
  and before an event never confirmed on VRChat is let go, the group's calendar is read once for
  the month it starts in (`GetGroupCalendarEvents`, `calendar.read`). An event there with the same
  title and start, not owned by another Modbot event, is taken as this one: updated to what the
  event says now, or deleted if it is no longer wanted. If the read fails, nothing is written and
  the look is made again later. Only one page is read, so a group with more events than that in one
  month could still end up with a copy.
- **Changes made on VRChat's own site are read back** when someone opens the calendar (§12). Until
  2026-09-27 they were not, and Modbot was the only source: the next edit in Modbot overwrote them.
  That was narrowed because groups plan events on vrchat.com and in the game as well, and the
  VRChat page's Overview said "No upcoming events" while VRChat showed one.
- VRChat's own per-event `.ics` download (`GetGroupCalendarEventICS`) is **not exposed**. It needs a
  signed-in VRChat session, so a link to it is useless to members, and proxying it would spend the
  strict calendar budget every time somebody's calendar app refreshes. Modbot's own feed (§6)
  answers the same need without asking VRChat anything.

### 3.2 The Discord event

- An **external** event in the server from settings, with the occurrence's start and end, the
  description, and the picture link as its cover.
- **Location:** the join link while the instance Modbot opened for the occurrence is open (§4);
  otherwise the world's name, or "VRChat" with no world. Discord allows 100 characters there and
  VRChat's launch links are around 180, so the location is Modbot's own short address,
  `{public address}/api/calendar/join/{event id}`, which redirects to the open instance and answers
  404 otherwise. Without a public address, or when that is too long too, the location is the
  world's name and the link goes at the top of the description.
- The short address is used **only while there is a join link behind it** (changed 2026-10-01).
  Before that it was used whenever the event was open, so an event whose instance Modbot did not
  open -- opening it automatically turned off, or the opening refused -- showed a location that
  answered 404.
- Started when the occurrence opens, ended (completed) when it finishes, ended (cancelled) when
  the event is cancelled. Discord cannot move an event backwards, so **each occurrence of a
  repeating event gets its own Discord event.**
- The place remembers **the planned start** of the date it is about (§2.2). A date cancelled on its
  own moves the event on, so its Discord event is ended and the next date gets one; a date moved or
  reworded keeps its Discord event, which is updated. The channel post follows the same rule, and a
  cancelled date's post ends as "Cancelled". Posts and Discord events made before 2026-10-01 hold
  the actual start, which is the planned one for every date that has no change.
- Discord refuses an event that starts in the past, so an event created after its start is given
  a start one minute from now.
- The bot needs **Manage Events**. Health's Discord card shows it as missing when an event wants a
  Discord event and the bot does not hold it.

### 3.3 The channel post

- An embed like the instance cards: the group's name, the title, the time as Discord timestamps (so
  every reader sees their own time), the world, who can join, the region, and the event picture or
  the world's. The world is a linked name rather than an id, and the world's picture is sent with
  the message (Discord embeds design 2026-09-17); the event's own picture is linked as it is,
  because it is on a host that serves anybody.
- **The world links to its page on vrchat.com** (`https://vrchat.com/home/world/{id}`, changed
  2026-10-01). It linked to the world in Modbot, as the moderators' cards do, but this post is for
  members, and Modbot answered every member who clicked it with its sign-in page. The Discord event
  and the feed name the world without a link, so nothing else member-facing pointed into Modbot for
  a world.
- A Join button once the instance is open. Rewritten when the event changes or opens.
- Ends as "Finished" or "Cancelled", without the button. A repeating event gets a new post for
  each occurrence, the way a notice board would.
- Unticking the channel post deletes the post. Mentions are off, as on every message the bot sends.
- The channel picker marks a channel missing View Channel, Send Messages or Embed Links.

## 4. Opening the instance

- At **start minus N minutes** (N per event, default 10), Modbot creates a group instance through
  the gate with `InstancesApi.CreateInstance`: the event's world, access and region, owned by the
  managed group from settings.
- The room is recorded through `PlaceStore` exactly as a sighting is, so Live, the instance
  cards and the Discord announcer pick it up with no extra wiring.
- Its join link then goes onto the Discord event and the channel post.
- **Once per occurrence, across restarts.** A row in `calendar_opening`, keyed by event and
  occurrence start, is written **before** the request is sent — the same rule sign-ins follow
  (foundation §4.1.2). A crash between the two cannot open a second instance.
- **A failure is not tried again for that occurrence.** It is shown on the event and on Health, and
  the next occurrence gets its own attempt. An automatic retry loop against a write VRChat just
  refused is the thing §4.3 exists to prevent.
- An occurrence is still opened if Modbot comes up late, as long as the occurrence has not ended.
- A date cancelled on its own is never opened, and a moved one opens before its new start (§2.2):
  the opener asks the same list of dates as everything else.
- The instance is not linked to the VRChat calendar event (`calendarEntryId`): whether VRChat
  wants the series id or an occurrence id there has not been checked against the real API.
- **`instancePersistenceEnabled` is sent as `false`** (added 2026-10-01). The SDK writes `null` for
  it when it is left out, and VRChat refuses that with a 400, "instancePersistenceEnabled must be a
  boolean: 'null'", so until then every opening failed. `playerPersistenceEnabled` still goes as
  `null`; the refusal named only the instance setting.

**To review (2026-10-01).** The maintainer wants a check of whether a bot opening group instances
on a timer is within VRChat's Terms of Service before this feature is promoted any further. It
stays as built until then; nothing in the user docs speaks to it.

## 5. Rate limits

| Class | Lane | Rate | Source |
|---|---|---|---|
| `calendar.write` | `calendar` | **1 per 60 s**, shared by create, update and delete | **Not measured.** The maintainer asked for "a very lax rate limit by default" because VRChat's calendar limit is strict. Must be confirmed. |
| `calendar.read` | `calendar.read` | **1 per 10 s** | **Not measured**, same reason. Used for the look before a create that got no answer is sent again (§3.1), and for reading the calendar back when someone opens a page or presses Refresh (§12.1). Never on a timer. |
| `instances.create` | `instances.create` | **1 per 5 s** | Measured by the maintainer. |

All three are resource-scoped to the group where it applies, count against the global backstop,
and follow §4.3.1 unchanged: **a 429 cold-stops that bucket**, the place shows "Waiting", nothing
is retried until the bucket's own wait is over, and then the one probe the limiter allows is the
next queued write. Nothing in the calendar ever retries a 429 immediately. World names use the
existing `worlds.read` budget; nothing else new is called.

## 6. The calendar feed

- `GET /api/calendar/feed/{token}.ics` — no sign-in, `text/calendar`.
- The token is 32 random bytes. Only its SHA-256 is used to find it; it is also stored encrypted so
  the calendar page can show the link again to people who may manage the calendar.
- **Regenerate** makes a new token and the old link stops working at once. Since 2026-10-01 the
  page's **New link** asks first, in a dialog that says what happens: "Make a new feed link?", "The
  old link stops working. Everyone who added it to their calendar stops getting updates and has to
  add the new link.", **Make new link** (red) and **Cancel**. That explanation is in the UI because
  the maintainer asked for it on 2026-10-01, the one exception to the repository's rule against
  explanatory text. Before, one click broke every subscriber's link with nothing in the way. The
  first **Make link**, with no link yet to break, does not ask.
- Contains every `scheduled` and `open` event, one `VEVENT` each, repeats as `RRULE`; and, since
  2026-10-01, every `finished` event for **30 days** after its last date ended and every `cancelled`
  one for 30 days after the cancel (`CalendarFeedWriter.KeepEndedFor`), the cancelled ones with
  `STATUS:CANCELLED`. Until then the feed held live events only, so a cancel or the end of an event
  removed it from every subscriber's calendar without a word, and a group's history went with it.
  Thirty days is long enough for every calendar program to have read the feed many times, and short
  enough that the feed does not grow with every event a group has ever run. A deleted event still
  leaves at once: deleting is for mistakes.
  - `UID` is `{event id}@modbot`, stable for the life of the event; `SEQUENCE` is its version.
  - `DTSTART`/`DTEND` carry `TZID` with a `VTIMEZONE` built from the time zone database for the
    years the event covers; a UTC event uses `Z` times and no `VTIMEZONE`.
  - `UNTIL` is in UTC, as RFC 5545 requires when `DTSTART` has a `TZID`.
  - Text is escaped (`\\`, `\;`, `\,`, `\n`) and lines are folded at 75 octets with CRLF.
  - The location is the world's name. The join link is left out: the feed is public to anyone with
    the link, and the link changes every occurrence.
  - A date cancelled on its own (§2.2) is an `EXDATE` in the event's zone. A date moved or given
    its own words is one more `VEVENT` with the same `UID`, a `RECURRENCE-ID` naming the planned
    start, and its own `DTSTART`, `DTEND`, `SUMMARY` and `DESCRIPTION`. The `VTIMEZONE` covers a date
    moved past the rule's last one.
  - **A cancelled date is an `EXDATE`, not a cancelled `RECURRENCE-ID` copy** (chosen 2026-10-01).
    `EXDATE` is part of the repeat itself, which every calendar program has to read to expand the
    series at all, so the date is gone everywhere. `STATUS:CANCELLED` on one date's copy only works
    in a program that reads status on a single date; one that does not shows the date as if it
    still happens, which is the worse of the two failures. Not checked against the programs
    themselves.
  - `URL` is the event on Modbot's calendar page, `{public address}/calendar?event={id}`, left out
    without a public address. The page asks for a sign-in, and the event id is all it carries: the
    feed holds nothing about members.

## 7. Permissions

| Permission | Bit | Allows |
|---|---|---|
| `ViewCalendar` — "See calendar" | 25 | The calendar page and every event's places and states. |
| `ManageCalendar` — "Manage calendar" | 26 | Create, edit, cancel and delete events; see and regenerate the feed link. |

Neither is added to the built-in roles; Administrator holds both.

## 8. Facts

Every change a person makes is a fact on the Modbot platform with the event's id as subject, in
the operational log:

| Fact | When |
|---|---|
| `modbot.calendar.event.create` | Created (draft or scheduled) |
| `modbot.calendar.event.change` | Edited; carries before and after |
| `modbot.calendar.event.cancel` | Cancelled |
| `modbot.calendar.event.delete` | Deleted |
| `modbot.calendar.date.cancel` | One date of a repeating event cancelled on its own (§2.2); carries the date |
| `modbot.calendar.date.change` | One date moved or reworded on its own; carries the date, before and after |
| `modbot.calendar.event.open` | An occurrence opened |
| `modbot.calendar.event.finish` | The last occurrence ended |
| `modbot.calendar.instance.open` | Modbot opened the instance; carries the world and instance ids |
| `modbot.calendar.instance.fail` | Opening the instance failed; carries VRChat's words |
| `modbot.calendar.publish.fail` | A place failed; carries which place and the error |
| `modbot.calendar.publish.done` | A place got the event for the first time, not for each edit after (added 2026-10-01, §14.1); carries which place |
| `modbot.calendar.publish.remove` | A place that held the event no longer does (added 2026-10-01, §14.1); carries which place and what it was before |
| `modbot.calendar.feed.regenerate` | The feed link was replaced |

`modbot.calendar.event.cancel` carries `postInChannel` (added 2026-10-01, §14.4).

Changes read from VRChat's calendar (§12) use `create`, `change` and `delete` with `"on": "vrchat"` in
the data and no actor: nobody in Modbot made them.

## 9. Scheduling

Two loops, both on `IModbotClock`, never the system clock:

- **`CalendarService`** (VRChat side, every 15 s): moves events through their states, opens
  instances, and sends VRChat calendar writes. Moving states asks VRChat nothing, so a cold stop
  never holds an event in `open` after it ended.
- **`CalendarDiscordService`** (Discord side, every 20 s, only while the bot is connected): brings
  the Discord events and channel posts in line with the events. It only reads what the first loop
  wrote, the same split the instance announcer uses.

## 10. Not checked against the real services

- VRChat's real calendar limits (the 60 s and 10 s above are placeholders).
- Which fields VRChat's calendar requires or rejects, its text lengths, how it reads a monthly
  recurrence, whether `imageId` must belong to the group, and whether an update without
  `recurrence` keeps or removes a series.
- Whether `CreateInstance` for a group needs anything beyond owner, type, access, region and
  `instancePersistenceEnabled` (§4; the last was learned from a 400 on 2026-10-01).
- Discord's handling of external event covers fetched from arbitrary picture links.
- **One date on VRChat (§3.1, added 2026-10-01).** That VRChat's month list gives each date of a
  series an id of its own; that a delete or update by that id changes only that date (and not the
  series); whether an update to one date wants `parentId`; and whether VRChat keeps a changed date
  through a later update of the series. Built from the spec's model (`occurrenceKind`,
  `occurrenceModified`, `seriesId`, `parentId`), not from a call to the real service.

## 11. The page (added 2026-09-27)

The calendar page works like Google Calendar: Day, Week (the default; Day on a phone), Month and
Schedule views, a small month to jump with on wide screens, and events moved by dragging them.

- **Built by hand on pointer events**, not a calendar library. The app has no drag library.
  FullCalendar's day, time-grid and drag parts are MIT (its resource and timeline views are not),
  but they bring their own markup and styles to restyle into the Console look, for a grid that is a
  few hundred lines by hand. The pure parts (snapping, overlap
  layout, week and month maths, drag to time, the move written in the event's zone) are in
  `lib/calendarGrid.ts` with their own tests.
- **A drag is an edit.** It saves once, on let-go, through `PUT /api/calendar/events/{id}` with the
  whole event as the form would send it, so it is checked, recorded as `modbot.calendar.event.change`
  and published exactly as an edit is. No new VRChat or Discord request exists for it.
- **The move is measured in the viewer's zone and written in the event's.** The difference in the
  event's own wall clock between the old and new date is applied to its first start and end, so a
  one-off event lands exactly where it was dropped and a weekly 20:00 stays on the wall clock across
  daylight saving.
- **A repeating event asks "This date / All dates"** (changed 2026-10-01). Until then §2 stored one
  rule and no exceptions, so a drag moved every date and the page said so. "This date" is now a
  change to that one date (§2.2), through `PUT …/dates`, with its own undo; "All dates" is the edit
  it always was, and weekly days and the last date shift with the start. "This and following" is
  still not offered: it would split the event into two, with a second VRChat series and Discord
  events nobody asked for.
- **Undo** is the same update with the event as it was before the drag. Because VRChat writes wait
  20 s for edits to settle (§3.1), an undo straight away usually costs no VRChat write at all.
- **The quick form saves drafts only.** A scheduled event is published at once and VRChat refuses one
  without a description, so the two-field form cannot schedule; **More options** opens the full form.
- **Colour is publish state:** scheduled, open, draft, finished, or failed somewhere (§3).
- Only `ManageCalendar` can drag, draw or edit; `ViewCalendar` alone opens events and moves nothing.

## 12. Reading VRChat's calendar back (added 2026-09-27)

Seen 2026-09-27 on a live group: vrchat.com's Overview showed an upcoming watch party, and Modbot's
VRChat page said "No upcoming events", because Modbot's calendar held only events made in Modbot.
The maintainer settled the design one question at a time from the group's real calendar (10 events
in September, a weekly series, none from Modbot).

### 12.1 When it is read

**Only when someone uses it**, the maintainer's standing rule for VRChat requests:

| Trigger | Requests |
|---|---|
| The calendar page opens, or moves to dates it has not read | One `GetGroupCalendarEvents` per UTC month on screen (a Month view can touch three) |
| The VRChat page's Overview opens | One for this month; one for next month when nothing on VRChat is left this month |
| Refresh on the calendar page | The months on screen again, skipping the memory below |
| A repeating event new to Modbot, or changed on VRChat | One `GetGroupCalendarEvent` for its series, which carries the rule (the month list has dates only) |
| An event missing from a month read to its end | One `GetGroupCalendarEvent` for it, before it counts as deleted |

- Each month's read is **remembered for five minutes, for everyone** (`CalendarVRChatReadMemory`, in
  memory), so ten moderators opening the page cost one read. One read runs at a time; a second waits
  and finds the months the first just read.
- **Nothing on a timer.** The maintainer was offered a background read and chose not to. The feed
  therefore learns about an event made on VRChat only after someone opens one of these pages.
- All on `calendar.read`, at interactive priority. A 429 cold-stops it and is never retried: the
  page says the calendar is waiting, and Modbot's own events still show.
- At most three pages a month, ten series reads and five lookups in one read; the rest wait for the
  next read.
- The page asks through `POST /api/calendar/vrchat` (See calendar), which can take several seconds;
  the page shows what Modbot has first and loads again when the read brought something in.

### 12.2 How an event made on VRChat is kept

- **As an ordinary Modbot event**, editable from Modbot (the maintainer's choice over view-only),
  with `made_on_vrchat` set so the page can mark it **VRChat**. Its VRChat place is `published` with
  VRChat's id and a fingerprint of what it says, so the publisher sends nothing until someone edits
  it; an edit updates that same VRChat event, never a copy.
- A repeating event is keyed by its **series id**; its dates in the list are matched to it by
  `seriesId`.
- It arrives with VRChat and the Discord event on, and the channel post and opening the instance off.
  No world: VRChat's calendar has none. (Changed 2026-09-27: it used to arrive with Discord off,
  so an event made on VRChat never reached the Discord server unless a moderator opened it and ticked
  the box. The first read of a group with many upcoming VRChat events makes a Discord event for each,
  five Discord calls a pass at most.)
- **VRChat's settings the form does not have** -- featured, host and guest early join, closing the
  instance after the end, roles, instance overflow -- are kept on the event and sent back with every
  create and update. The SDK's update body sends `featured` and `usesInstanceOverflow` as false when
  they are left out (checked 2026-09-27), so without this an edit from Modbot would switch them off.
  Events made in Modbot keep sending what they always did.
- A one-off event from VRChat has no time zone; it is kept as UTC until a moderator picks one.
  VRChat's "after N times" end is counted out to a last date.
- **Not taken in:** drafts; a series every second week or more, or yearly (Modbot's rule has
  neither); and a row with the title of a Modbot event whose create has no id yet, or whose VRChat
  place was just removed -- it may be that very event, and taking it in would make a second.
- A title or description longer than Modbot's limits (100 and 1000) is cut.

### 12.3 Changes and deletes made on VRChat

- **The newest change wins**, for every event, Modbot's own included. Each VRChat place keeps
  VRChat's `updatedAt` as last seen, from the answer to Modbot's own write or from a read. A later
  one means it was changed on VRChat. It is copied in, and becomes what Modbot last sent, unless an
  edit made in Modbot is waiting to go out and is newer; then Modbot's goes out as usual. The first
  read of an event Modbot published before this change only notes the `updatedAt`.
- A change copied in touches only VRChat's fields (title, description, times, repeat and §2's VRChat
  calendar fields); the world, access, region and Discord places are Modbot's alone. The picture
  link is taken from VRChat only for an event made there.
- **Deleted on VRChat:** an event missing from a month read to its end, which Modbot expects well
  inside that month (VRChat's month is not exactly the UTC month), is looked up on its own. VRChat's
  404, or the event marked deleted, deletes it in Modbot as a delete on the page does (cancelled and
  hidden; the Discord places end), with the VRChat place `removed` so nothing is sent to take it
  off. Found elsewhere, it is a change like any other. A failed or partial read deletes nothing.
- **One exception, kept from before:** an update that meets a 404, because the event was deleted on
  VRChat while an edit made in Modbot waited to go out, makes the event again. The edit is the newer
  of the two.
- A change to **one date** of a VRChat series made on VRChat stays on VRChat; Modbot keeps the
  series. Modbot's own dates changed on their own (§2.2) are not read back from VRChat: a read finds
  the date Modbot changed with a newer `updatedAt` and reads the series, which has not changed, so
  nothing is copied. Reading VRChat's own one-date changes into §2.2 rows is possible now and not
  built.

### 12.4 Not checked against the real services

- Whether a create for a repeating event answers with the **series** id (assumed, so its dates are
  matched by `seriesId`). If it answered with an occurrence id, later months could show a Modbot
  series as a second event.
- Whether VRChat's update keeps fields it is not sent (the settings above are sent back regardless).
- VRChat's page size for the month list (60 by default) and whether `n` may be larger.

## 13. What each time did (added 2026-10-01)

"Which events are worth running?" is the organiser's question. The calendar knew what was planned and
which instance it opened, the instance knew how full it got, and the fact log knew who joined the
group; nothing joined them. `CalendarResults` does, read-only, from Modbot's own tables.

- **The numbers are the ones other screens show.** Most at once and how long it ran are the
  instance's `InstanceRows` row, exactly as the instance popup has them (over the instance's whole
  life, not clipped to the event). Who a moderator's client saw is `PresenceCounts` over the
  instance's own stretch, as the popup counts it. New members and join requests count the facts
  `members.joined` and `requests.received` count. A second way of counting any of them would be a
  second answer that could disagree with the first.
- **Which instance.** The `calendar_opening` row's, when Modbot opened one for that time. Otherwise
  the managed group's instance in the event's world with the longest overlap with the stretch from
  `OpensAt` to the end (the earlier opened on a tie): an event opened by hand, or one that does not
  open its own. An event with no world, and no opening, has none.
- **Which times.** The rule's times that have started, plus every opening's time, so a time that ran
  before the event was moved still counts at the time it ran. A cancelled event's stop at the cancel;
  a draft never ran; a deleted event is hidden from the page and so from Past events.
- **Joins window.** From `OpensAt` to a day after the end: people often join the day after an event
  they liked. Overlapping windows of a daily event each count the shared part.
- **Compared with the event's own earlier times:** the middle value (median) of each figure over its
  last six times before this one. Times with no instance are left out of the instance figures, and
  times nobody was seen are left out of the presence figures, because a time with no moderator's
  client there is not a time nobody came.
- **Permissions.** See calendar and See analytics; who was seen also needs See the audit log, as in
  the instance popup. `CalendarView.canSeeResults` tells the page whether to ask.
- **Past events** lists every time an event ran over the last 90 days (up to 366 through the API),
  the most at once first, at most 50, with no presence figures: one presence query per instance
  would be one query per row.
- **Not posted anywhere.** A Discord post after the event was left optional and is not built; if it
  ever is, it carries counts and no names.

## 14. Where it goes, the preview, and telling members (added 2026-10-01)

A review of the calendar on 2026-10-01 found that publishing to both sides worked, and that the
weak part was what a moderator could see: the form never said where an event would go or how it
would look there, a ticked place that was not set up did nothing and said nothing, "Published"
never appeared without a reload, and a cancel told members nothing unless they happened to look at
the old card. The maintainer approved the four fixes below on 2026-10-01.

### 14.1 "Published" without a reload

The live stream carries facts and nothing else (live updates design §2), and a place's state
changing wrote none unless it failed. So the page never heard that a place went from Waiting to
Published, although the docs said it did.

- **A place that gets onto Discord or VRChat for the first time writes
  `modbot.calendar.publish.done`; one that comes off writes `modbot.calendar.publish.remove`**, both
  with the place. A failure already wrote `modbot.calendar.publish.fail`. Each publisher remembers,
  for every place, its state and whether the other side held a copy of it (an id) at the start of its
  pass, and compares after saving. So **an edit writes nothing**: an edit that goes through waiting
  or a failure and back to published is not "published" again, and neither is a repeating event's
  Discord place moving on to its next time. **A place the other side never held writes no
  `remove`**: a ticked place that was never set up, or a create that never went through, has
  nothing to be taken down from. (Changed 2026-10-01 after the first build: it wrote `done` for
  every edit that passed through waiting, and `remove` for places that were never published.)
- The calendar page already reads again on any `modbot.calendar.*` fact (live updates design §6.1),
  so the grid and the open event both redraw. No new kind and no new rule were needed.
- **Not changed:** a `fact` on the stream follows the audit log's rules, and calendar facts are in
  the operational log, so somebody with Manage calendar but not See the operational log still gets
  no live calendar updates. That was so before this change and is left as it is.

### 14.2 The form: chips and the preview

- **Where it goes is one row of chips at the top**: VRChat calendar, Discord event, Discord channel
  post, Calendar feed and Open the instance. The feed is shown pressed and cannot be switched: every
  scheduled or open event is in it. The old tick boxes are gone; each place's own settings stay in
  their section and show only while its chip is on (VRChat's category, visibility and the rest; the
  channel; the minutes early). The auto-invite controls for an opened instance belong in Open the
  instance's section.
- **A Preview tab draws the event the way each place that is on would show it**: the Discord event
  (cover, time, name, location, description), the channel card (group, title, description, fields,
  picture, footer, Join button), what VRChat's calendar is sent, and what a phone or desktop calendar
  reads from the feed.
- **One set of rules: the server draws it** (`POST /api/calendar/preview`, Manage calendar). Every
  part comes from the code that sends it, so the preview and the real thing cannot disagree:
  - Discord: the publisher's own `ServerEventDetails` and `Post`, behind `ICalendarDiscordPreview`
    (declared in Core so the API needs no reference to the bot, as `IDiscordBotStatus` is). The name
    is cut at 100 and the description at 1000; the location is the world's name, "VRChat" without a
    world, and the short join address only while an instance Modbot opened is open. The one
    difference: a post sends the world's picture as a file, the preview shows the address.
  - VRChat: the request `CalendarVRChatRequests` builds, read back field by field, VRChat's own
    words for category, platform and days taken from the request as the SDK writes it. An event on
    VRChat already previews as the update it would be, which sends no visibility and notifies nobody.
    VRChat rewrites some characters after it receives them (seen 2026-10-01: an en dash dropped, a
    full stop changed); the preview shows what is sent, and the form says nothing about it.
  - The phone calendar: `CalendarFeedWriter.Entry`, the same values the feed writes.
  A copy of these rules in the browser was the other choice; it would have been a second answer that
  could drift from the first, and the Discord cuts and the location rule have already changed once.
- **The preview lets an unfinished event through**: no title, no description, no channel, or no
  world to open yet is still drawn. A title or description too long, or a time zone or date that is
  not one, is refused with the same message a save gives.
- **Counters** beside the title and the description, "12 / 100" and "0 / 1000", red once over. They
  count as the server does: after trimming, in UTF-16 units.
- **The buttons are pinned under the form** (the dialog's foot), so on a phone Schedule and Save
  draft never scroll out of reach; the form was about 1836px tall at 360px wide.

### 14.3 "Not set up"

A ticked place that cannot work now says **Not set up**, linking to where it is set up, in three
places: beside its chip in the form, on the event where its place would be, and on Health's
Calendar card. The rule is on the server (`CalendarReadiness`) and comes with the calendar's own read
(`CalendarView.ready`), read from settings and the bot's own status and never by asking VRChat or
Discord:

| Places | Set up when | Link |
|---|---|---|
| VRChat calendar, Open the instance | a managed group is chosen and Modbot has a VRChat account (username and password) | Settings → Modbot's VRChat login |
| Discord event, channel post | a server id is set and the bot is connected | Settings → Discord |

- The Discord loop only runs while the bot is connected, so a bot that is connecting or reconnecting
  counts as not set up while it is: nothing is sent until it is back.
- A sign-in VRChat refused is not counted here; the gate's own banner says that.
- In the form a channel post with no channel picked also says Not set up, without a link: the
  channel is picked in its own section, just below.
- On an event, the mark replaces the place's state for draft, scheduled and open events, and shows
  even when the place has no row yet, which is the usual case: a publisher that cannot run never
  makes one.

### 14.4 The cancel post

- **Cancel event opens a dialog with one tick, "Post that it's cancelled in the channel"**, shown
  when the event has a channel, and ticked to start with when the event has a channel post. An edit
  to the old card, which turns red as before, notifies nobody; a new message does.
- Ticked, the cancel makes a `cancelPost` place for the event, waiting, with the channel and the
  occurrence. The calendar's Discord loop posts it **once**: the title, the time as a Discord
  timestamp, and "Cancelled", as a plain message with mentions off. The row then holds the message's
  id and is published; nothing edits or posts it again. A refusal is not sent again; a failure that
  was not a refusal is tried on the next pass.
- A second cancel of a cancelled event changes nothing and never makes a second post. A tick on an
  event with no channel is refused. The body may be left out, as before: nothing is posted.
- Discord scheduled events still end and vanish on a cancel, as before.
- The cancel's fact carries `postInChannel`, so the audit log says whether members were told.
