# Modbot Calendar

- **Date:** 2026-09-15
- **Status:** Built
- **Covers:** planning events in Modbot, publishing them to VRChat's calendar, Discord and a
  channel, opening the instance on time, and a calendar feed
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
| Title, description | Title up to 100 characters (Discord's limit for an event name), description up to 1000. |
| Start, end, time zone | Stored as the first start and end (UTC instants) plus an IANA time zone. The time zone is what keeps a weekly 20:00 event at 20:00 through daylight-saving changes. |
| Repeat | `none`, `daily`, `weekly` on chosen days, or `monthly` on the same day of the month. An optional last date. **The rule is stored, not the occurrences.** A monthly event on the 31st skips months without one, the same as iCalendar. |
| World | Picked from worlds Modbot knows, or typed as an id. Never checked for shape (foundation §3.1.1). |
| Instance access, region | `members`, `plus` or `public`; `us`, `use`, `eu` or `jp`. |
| Image | A picture link for Discord, and optionally a VRChat file id for VRChat's calendar. Modbot does not upload files to VRChat — that endpoint has no rate limit set. |
| VRChat calendar fields | Category, languages, platforms, tags, who can see it (`group` or `public`), and whether VRChat notifies group members. Exactly the fields `CreateCalendarEventRequest` has that make sense to set. |
| Where it goes | VRChat calendar, Discord event, channel post (with the channel), open the instance and how many minutes early (default 10). |

### 2.1 States

| State | Meaning |
|---|---|
| `draft` | Saved, published nowhere, never opened. |
| `scheduled` | The next occurrence has not started. |
| `open` | The next occurrence has started — or its instance opening time has come — and has not ended. |
| `finished` | No occurrences are left. |
| `cancelled` | A person cancelled it. Removed from every place it was published. |

A repeating event goes `scheduled → open → scheduled` for each occurrence and only becomes
`finished` after the last one. The event row keeps the start of the **current occurrence**, so
every place knows which one it is about.

Deleting an event is a cancel that also hides it from the calendar page. The row stays, because
the facts about it point at it.

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
- Cancelling, deleting, or unticking VRChat deletes the event on VRChat. A finished event is left
  there: it is history on VRChat's side as well.
- A write VRChat refuses is not sent again until the event changes. A write that got no answer
  (timeout, VRChat's own 5xx) is tried again after 15 minutes.
- **Changes made on VRChat's own site are not read back in this version.** Modbot is the source; the
  next edit in Modbot overwrites them. Reading them back would need the calendar read budget on a
  schedule, and nobody has asked for it yet.
- VRChat's own per-event `.ics` download (`GetGroupCalendarEventICS`) is **not exposed**. It needs a
  signed-in VRChat session, so a link to it is useless to members, and proxying it would spend the
  strict calendar budget every time somebody's calendar app refreshes. Modbot's own feed (§6)
  answers the same need without asking VRChat anything.

### 3.2 The Discord event

- An **external** event in the server from settings, with the occurrence's start and end, the
  description, and the picture link as its cover.
- **Location:** the join link once the instance is open; before that, the world's name. Discord
  allows 100 characters there and VRChat's launch links are around 180, so the location is Modbot's
  own short address, `{public address}/api/calendar/join/{event id}`, which redirects to the open
  instance and answers 404 otherwise. Without a public address, or when that is too long too, the
  location is the world's name and the link goes at the top of the description.
- Started when the occurrence opens, ended (completed) when it finishes, ended (cancelled) when
  the event is cancelled. Discord cannot move an event backwards, so **each occurrence of a
  repeating event gets its own Discord event.**
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
- The instance is not linked to the VRChat calendar event (`calendarEntryId`): whether VRChat
  wants the series id or an occurrence id there has not been checked against the real API.

## 5. Rate limits

| Class | Lane | Rate | Source |
|---|---|---|---|
| `calendar.write` | `calendar` | **1 per 60 s**, shared by create, update and delete | **Not measured.** The maintainer asked for "a very lax rate limit by default" because VRChat's calendar limit is strict. Must be confirmed. |
| `calendar.read` | `calendar.read` | **1 per 10 s** | **Not measured**, same reason. Nothing in this version reads the calendar; the budget is set so a later read-back has one. |
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
- **Regenerate** makes a new token and the old link stops working at once.
- Contains every `scheduled` and `open` event, one `VEVENT` each, repeats as `RRULE`:
  - `UID` is `{event id}@modbot`, stable for the life of the event; `SEQUENCE` is its version.
  - `DTSTART`/`DTEND` carry `TZID` with a `VTIMEZONE` built from the time zone database for the
    years the event covers; a UTC event uses `Z` times and no `VTIMEZONE`.
  - `UNTIL` is in UTC, as RFC 5545 requires when `DTSTART` has a `TZID`.
  - Text is escaped (`\\`, `\;`, `\,`, `\n`) and lines are folded at 75 octets with CRLF.
  - The location is the world's name. The join link is left out: the feed is public to anyone with
    the link, and the link changes every occurrence.

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
| `modbot.calendar.event.open` | An occurrence opened |
| `modbot.calendar.event.finish` | The last occurrence ended |
| `modbot.calendar.instance.open` | Modbot opened the instance; carries the world and instance ids |
| `modbot.calendar.instance.fail` | Opening the instance failed; carries VRChat's words |
| `modbot.calendar.publish.fail` | A place failed; carries which place and the error |
| `modbot.calendar.feed.regenerate` | The feed link was replaced |

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
- Whether `CreateInstance` for a group needs anything beyond owner, type, access and region.
- Discord's handling of external event covers fetched from arbitrary picture links.
