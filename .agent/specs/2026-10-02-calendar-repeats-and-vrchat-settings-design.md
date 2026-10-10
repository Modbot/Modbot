# Calendar: every few weeks, a number of times, and VRChat's event settings

- **Date:** 2026-10-02
- **Status:** Built
- **Covers:** a repeat every N days, weeks or months; a repeat that stops after a number of times;
  Featured; which of VRChat's calendar event settings the form has, and which it leaves out
- **Builds on:** [Modbot Calendar](2026-09-15-modbot-calendar-design.md) §2 (the event), §3.1
  (VRChat's calendar), §6 (the feed), §12 (reading VRChat's calendar back)
- **Narrows:** calendar design §3.1 ("interval 1") and §12.2 ("Not taken in: a series every second
  week or more"), and moves Featured out of "VRChat's settings the form does not have"

## 1. Why

From a recorded call with testers, 2026-10-02:

- "currently it's only weekly for the one week instead of every two weeks or every three weeks";
- "repeats weekly for six weeks, or eight weeks ... after the four weeks, it stops repeating. So
  then I could just edit it, rather than make a whole new one";
- an event made in Modbot never showed as featured on VRChat, because the form had no way to ask
  for it: "you're missing an option for the VRChat event to be featured. Please review all of the
  different settings for a VRChat event and make sure they're accounted for".

## 2. The repeat

Two new fields on `calendar_event`, beside `repeat`, `repeat_days` and `repeat_until`:

| Column | Holds |
|---|---|
| `repeat_every` | How many days, weeks or months apart: 1 to 52, default 1. Every event before this had 1. |
| `repeat_times` | How many dates before it stops, the first included: 1 to 500, or null. |

- **One end or the other.** `repeat_until` and `repeat_times` are never both set: the API refuses
  "Pick a last date or a number of times, not both." iCalendar's `UNTIL` and `COUNT` are exclusive
  in the same way, and VRChat's `end` is one type or the other. The form's **Ends** is **Never**,
  **On a date** or **After a number of times**.
- **Weeks run Monday to Sunday**, counted from the first start's own week. With every 2 weeks, that
  week and every second one after it; within an "on" week, every ticked day on or after the first
  start. This is iCalendar's default `WKST=MO`, and the feed writes `WKST=MO` out (§5) so a program
  that defaults to Sunday counts the same weeks.
- **Every N months** steps N months from the first start's month; a month without the day is
  skipped and still counted, as `INTERVAL` counts it.
- **A number of times counts the rule's dates**, not the ones that happen: a date cancelled on its
  own (calendar design §2.2) still counts, as an `EXDATE` does under `COUNT`. Cancel one of six and
  five happen. A date moved on its own counts once, at its planned time.
- **The limits are Modbot's own.** 52 covers a year of weeks; 500 covers more than a year of daily
  dates. VRChat's limits are not known (§8). `CalendarRepeat.MaxSteps` still bounds how far a rule
  is walked (now in days or months of span, not in dates).
- A one-off event drops both: the API saves `repeat_every` 1 and `repeat_times` null for it,
  whatever the form still held.

### 2.1 Everything that reads dates

The rule lives in one place, `CalendarRepeat.LocalStarts` (now `RuleStarts` for the rule and
`LocalStarts` for its end). Every place below reads dates through `CalendarRepeat`, so none needed a
rule of its own:

| Place | Reads |
|---|---|
| Timeline, state, finishing after the last date | `CalendarRepeat.Next` |
| One-date changes and `Rematch` after an edit | `PlannedBetween`, `IsPlannedDate`, `ForDate` |
| Discord per-date events and channel posts | `CalendarRepeat.Current` (the timeline's date) |
| Opening the instance, invites, the first-join posts | `CalendarRepeat.Next`, `OpensAt` |
| World-list picks | the timeline's date |
| Past events (results) | `CalendarRepeat.Between` |
| The page's grid, month and schedule | the occurrences the API works out with `Between` |
| The feed | `Between` for what belongs; the rule as an `RRULE` (§5) |
| Finding a create's copy on VRChat | `Between`, with every and times kept in `create_sent` |

Two places needed a change of their own:

- **The feed's database filter** for finished events loaded a repeating event only while its last
  date could be recent. An event with a number of times has no last date stored, so it is always
  loaded and `CalendarFeedWriter.Belongs` decides, as it already did. A group would need a great many
  finished counted events before that cost anything.
- **The page's drag** moves the first start and the days. With every 2 weeks and several days, a day
  carried over Sunday into the next week changes which weeks it falls in; a drag that keeps the days
  inside their week does not. Not handled; the page asks for no more than it did.

## 3. VRChat

### 3.1 What is sent

VRChat's `CalendarEventRecurrence` has had all of this from the start (SDK 2.21.1-nightly.41):
`interval` ("how often the event will be scheduled, in units of frequency") and `end` with
`type` `afterDate` or `afterOccurrences` and a `count`. Nothing has to be turned into an end date.

- `interval` is `repeat_every`.
- **A number of times is sent as `afterOccurrences`, counted from where the series sent starts.** The
  series VRChat is sent starts at the date Modbot is on now, not the first one (calendar design
  §3.1), so the dates before it are taken off: six times with two over is sent as four. This is
  worked out at each write; a series that moves on to its next date is not sent again (its
  fingerprint leaves out where it starts), and what VRChat was sent stays right, since it was sent
  with the start it counts from.
- A last date is sent as before.
- `CalendarEventRecurrenceEnd` serialises only the field its type needs (checked by printing the
  SDK's own JSON on this machine, 2026-10-02: `{"type":"afterOccurrences","count":4}`).

### 3.2 The fingerprint

`CalendarVRChatRequests.Fingerprint` gains every, times and Featured **only when they are not what
every event had before** (every 1, no times, not featured). An event that uses none of them keeps
the fingerprint it was published with, so the deploy does not send every event to VRChat again at
one write a minute. An event read from VRChat that is featured there gets one update after the
deploy, with what VRChat already has.

### 3.3 Read back

- `repeat_every` is copied from `interval`; an "after N times" end is kept as `repeat_times`
  instead of being counted out to a last date. The count is VRChat's, from the series' start as
  VRChat has it, which is the start copied in with it, so the two agree.
- Still not taken in: a yearly series, and one more than 52 apart or more than 500 times.
- `create_sent` keeps every and times, so a create with no answer is matched by the dates it really
  sent. A `create_sent` written before this reads as every 1, no times.

## 4. Featured

- `calendar_event.featured`, a form field: **Featured**, in the VRChat calendar section beside
  **Notify group members**. Off for a new event.
- It replaces `vrchat_featured`, which was kept only as VRChat said it for an event read from
  VRChat. The migration copies a true one across; everything else starts off, which is what was sent
  until now.
- Sent in every create, update and one-date update, as the form has it. Read back from VRChat with
  the rest, so the box shows what VRChat has.
- The API's `featured` is optional: left out, the event keeps what it has, so a client that does
  not know the field does not switch it off.
- **A refusal.** Featuring may need more than Manage Group Calendar, or may be limited per group;
  VRChat's answer to it has not been seen. A write VRChat refuses with a 4xx while Featured is on,
  and that is not the Manage Group Calendar refusal Modbot already names, shows VRChat's own words
  followed by **(sent with Featured on)** in the event's VRChat status. The place stays failed until
  the event changes, as every refusal does, so unticking Featured and saving sends it again.
- **A refusal, seen (2026-10-09, changes the paragraph above).** On live Modbot VRChat answered a
  create with 403, "You do not have permission to make a featured event", for an event with the
  box ticked, and the whole event failed on it. Now:
  - A 4xx whose words speak of "featured", for a write that sent `featured: true`, is sent again at
    once without Featured. The event is published; Featured is the only thing missing. The
    refusal is kept in `settings.vrchat_featured_refused_at`, and for 7 days after it every create
    and update leaves a wanted Featured out, so no event is refused for it twice. After the 7 days
    the next write asks again, in case the account may now.
  - Nothing tells the moderator that Featured was left out: the box stays ticked, and VRChat's
    copy, once read back, shows what VRChat has. This was left alone on purpose (no new text on
    the form); see the open question in §8.
- **An event made on VRChat is not refused for Featured it did not change (2026-10-09).** "18+
  Hangout", read in from VRChat's calendar and pushed back, was refused with the same words: the
  account may not feature, and VRChat refuses a `featured: true` even when it already holds the
  event as featured. `calendar_event.vrchat_featured` keeps what VRChat itself last said
  (set with the rest of VRChat's copy, filled from `featured` for events already read in). While
  an event made on VRChat still has `featured` equal to it, the body leaves `featured` out: the
  SDK's update model always writes it, so `CalendarUpdateBody` hides it behind a nullable of its
  own, and the create (which has to say something) says false. A moderator changing the box makes
  the two differ, and then it is sent as any event's.

## 5. The feed

The `RRULE` gains `INTERVAL=N` (and `WKST=MO` for a weekly one) when every is more than 1, and
`COUNT=N` for a number of times. Both are left out otherwise, so the rule of every existing event
reads exactly as before and calendar programs see no change. How far ahead a counted repeat's time
zone is written runs to its last date.

## 6. VRChat's event settings, field by field

Every field of `CreateCalendarEventRequest` and `UpdateCalendarEventRequest`, and where it is:

| VRChat field | In the form | Notes |
|---|---|---|
| `title` | **Title** | |
| `description` | **Description** | VRChat refuses an empty one (calendar design §2). |
| `startsAt`, `endsAt` | **Starts**, **Ends** | The read model's `durationInMs` is worked out from these. |
| `category` | **Category** | |
| `tags` | **Tags** | |
| `languages` | **Languages** | |
| `platforms` | **PC**, **Android**, **iOS** | |
| `accessType` | **Visible to** | The update model has none; `CalendarUpdateBody` adds it (calendar design §3.1). |
| `imageId` | **VRChat picture** | Calendar design §15. |
| `sendCreationNotification` | **Notify group members** | Only a first create can. |
| `recurrence.frequency` | **Repeat** | `yearly` is not offered. |
| `recurrence.daysOfWeek` | the day boxes | |
| `recurrence.interval` | **Every** | New, §2. |
| `recurrence.end` `afterDate` | **Ends: On a date**, **Last date** | |
| `recurrence.end` `afterOccurrences` | **Ends: After a number of times**, **Times** | New, §2. |
| `recurrence.timezone` | **Time zone** | |
| `featured` | **Featured** | New, §4. |
| `isDraft` | left out | Modbot's own draft is never sent, and VRChat refuses to unpublish an entry ("Can't unpublish or change access type once the calendar entry is published"). Always false. |
| `hostEarlyJoinMinutes`, `guestEarlyJoinMinutes`, `closeInstanceAfterEndMinutes`, `usesInstanceOverflow` | left out | They set how VRChat runs an instance **linked to the event**: an instance created with `calendarEntryId` (the instance create has it, and the group permission "Link Instances and Events" covers it). Modbot opens its instance without that link (calendar design §4) and has its own **Minutes early**, so on the form they would do nothing to the instance Modbot opens. Kept as read from VRChat and sent back unchanged, as before. |
| `roleIds` | left out | The SDK says nothing about it. Modbot reads it as the roles that see the event, but whether it limits who sees it, who may join, or both has not been checked, and a picker needs the group's roles on the calendar page. Kept as read and sent back. |
| `parentId`, `occurrenceKind` | left out | VRChat's own bookkeeping for a series and its dates. |

## 7. The form

In **When**, below **Repeat** and the day boxes, for a repeating event: **Every** (a number, and
"day", "week" or "month", or the plural), **Ends** (**Never**, **On a date**, **After a number of
times**), then **Last date** or **Times** for the end picked. Picking **After a number of times**
starts at 4. The event's details read "Every 2 weeks, 6 times"; its facts read "every 2 weeks on
Thursday, 6 times". No other text was added.

## 8. Not checked against VRChat

- Whether VRChat takes `interval` above 1 and `afterOccurrences` from an API write, and whether it
  counts weeks from Monday as iCalendar does. Weeks counted from Sunday would put a Sunday date of an
  every-2-weeks series in a different week from Modbot's.
- Whether VRChat's count takes a date deleted from a series (a date cancelled on its own) off the
  count or leaves it in, as Modbot does.
- What VRChat answers to Featured from an account or group not allowed to feature. **Seen
  2026-10-09:** a 403 with "You do not have permission to make a featured event", on a create with
  Featured on and on a write-back of an event VRChat holds as featured (§4). Still open: whether
  the permission is the account's, the group's or VRChat's own (a VRChat+ or partner matter), so
  when "may now" is true is not known and the 7-day retry is a guess; whether VRChat keeps
  `featured` on an update that leaves the field out (assumed, as for every other field left out of
  an update); and whether a moderator should be told when Featured was left out (no text was added
  for it).
- VRChat's own limits on `interval` and `count`.

## 9. Follow-ups

- Link the instance Modbot opens to its VRChat event (`calendarEntryId`), after which the early-join,
  close-after and overflow settings would mean something on the form.
- Roles that see the event, once what `roleIds` does is checked on a test group.
- A drag of an every-2-weeks event with several days that crosses a Sunday (§2.1).
