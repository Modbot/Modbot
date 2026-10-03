# Modbot Calendar

- **Date:** 2026-09-15
- **Status:** Built
- **Covers:** planning events in Modbot, publishing them to VRChat's calendar, Discord and a
  channel, opening the instance on time, and a calendar feed; reading VRChat's calendar back in
  (§12, added 2026-09-27); where an event goes, its preview, places not set up and the cancel
  post (§14, added 2026-10-01); cancelling or changing one date of a repeating event (§2.2,
  added 2026-10-01); uploading the VRChat picture (§15, added 2026-10-01); taking Modbot's old
  channel posts down (§3.3, added 2026-10-01); mentioning a role on the channel post (§3.3.1,
  added 2026-10-02); copies of an event in Discord (§16, added 2026-10-02); sending, failing and
  trying again (§17, added 2026-10-02); every few weeks, a number of times and Featured
  ([their own spec](2026-10-02-calendar-repeats-and-vrchat-settings-design.md), added 2026-10-02);
  picture links, the crop box and picture kinds (§15.1–§15.3, added 2026-10-02); the picture
  cropped for Discord (§15.4, added 2026-10-02)
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
| Repeat | `none`, `daily`, `weekly` on chosen days, or `monthly` on the same day of the month; every 1 to 52 days, weeks or months; ending never, on a last date, or after 1 to 500 times (every and times added 2026-10-02: [calendar repeats design](2026-10-02-calendar-repeats-and-vrchat-settings-design.md) §2). **The rule is stored, not the occurrences.** A monthly event on the 31st skips months without one, the same as iCalendar. |
| World | Picked from worlds Modbot knows, or typed as an id. Never checked for shape (foundation §3.1.1). Or picked from a world list, date by date (world lists design, added 2026-10-01): `WorldId` is then the current date's pick. |
| Instance access, region | `members`, `plus` or `public`; `us`, `use`, `eu` or `jp`. |
| Image | Two fields, kept apart on purpose: a picture link for Discord, and a VRChat picture for VRChat's calendar, stored as its VRChat file id. The VRChat picture is uploaded from the form (§15, changed 2026-10-01). Before that the form took a typed `file_…` id and Modbot uploaded nothing, because the upload endpoint had no rate limit set. |
| VRChat calendar fields | Category, languages, platforms, tags, who can see it (`group` or `public`), whether VRChat notifies group members, and Featured (added 2026-10-02). Exactly the fields `CreateCalendarEventRequest` has that make sense to set; the others, field by field, are in the [calendar repeats design](2026-10-02-calendar-repeats-and-vrchat-settings-design.md) §6. |
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
  event); its end can still change. A date put back exactly as planned loses its row -- once VRChat
  has it back: while VRChat may still hold the change (something was sent, or its id was found) the
  row stays with nothing of its own, the publisher sends the planned time and the event's words to
  that date, and removes the row after. Removing it at once left VRChat showing the move for good.
  **On an event that goes to VRChat, a date cannot move to or past its neighbours' planned
  starts** ("Can't move a date past the next date on VRChat"): Modbot keeps the dates in order
  either way, but whether VRChat takes one of a series' dates moved past another is not known, and
  refusing is the honest answer until it is. Facts `modbot.calendar.date.change` (before and after)
  and `modbot.calendar.date.cancel` (§8).
- **A cancelled date cannot be brought back from the page.** Not asked for; it can be added later.
- **The cancel post (§14.4) for one date.** The "This date / All dates" cancel dialog has the same
  "Post that it's cancelled in the channel" tick. For one date it is kept on the date's own row
  (`cancel_post_channel_id`, then `cancel_post_id` once posted) rather than as a `cancelPost` place,
  because a place is one per event and an event can have several dates cancelled. The Discord loop
  posts the same message with that date's time and title, once; a refusal is recorded as a failed
  `cancelPost` and not sent again. It is dropped unposted when the event has been deleted since, or
  the date ended more than a day ago: by then it is not news.
- **A date's VRChat id is never written to from an earlier pass** (changed 2026-10-01, after
  phase-2 testing). Writing the whole series again makes VRChat build its dates afresh, with new ids,
  and put a moved date back at its planned time; an update to the old id then answered 200 and
  changed nothing (a read of it was a 404), so Modbot marked the move sent and it was lost on
  VRChat. Now every date write is preceded by the month read that finds the date's id, so the id
  written to is always one VRChat listed in the same pass (a 404 for it counts as gone for a delete,
  and as a failure for an update). The kept `vrchat_id` is used only to pick the date out of that
  read when VRChat still lists it. This costs one `calendar.read` (up to its three pages) per date
  write, on the read budget that is ten times the write one; a confirming read after each write
  was the other choice, and was not taken because a write by a freshly found id has not been seen
  to fail silently. After any series write, every changed date still to come -- cancelled, moved,
  reworded or put back -- forgets its id, its sent fingerprint and where it was last sent, and is
  looked for and sent again (a cancelled date before where the series now starts excepted). The row
  keeps where the last update put the date on VRChat (`vrchat_sent_starts_at`), so a date put back
  as planned -- no times of its own any more -- is still looked for at the time VRChat has it.
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
  on days, monthly; its interval; the event's time zone; an end date or a number of times when
  there is one), not one VRChat event per occurrence. Until 2026-10-02 the interval was always 1;
  a number of times is counted from where the series sent starts ([calendar repeats
  design](2026-10-02-calendar-repeats-and-vrchat-settings-design.md) §3).
- **One date changed on its own (§2.2)** is sent to that date, not the series. VRChat lists a
  series' dates as `occurrenceKind: occurrence` rows with an `id` of their own and the series' id as
  `seriesId` (the SDK's `CalendarEvent`); a cancelled date is deleted by that id and a moved or
  reworded one updated by it (no `recurrence`, the series' other settings, the date's times and
  words). These are the delete and update calls the series already uses, on the same
  `calendar.write` budget; the id is found by reading the date's month with `GetGroupCalendarEvents`
  on `calendar.read` -- every page, up to the calendar page's own three, by the reader's own paging
  -- plus the neighbouring month when the date is within 14 hours of the edge and the month it was
  moved to, before every write (§2.2: a kept id can be stale). No new endpoint. The id must be a dated row of **this** series and never the
  series' own id, because deleting that would take every date off VRChat. **A date not found is
  shown as failed on that date**, cancelled or not, and not looked for again until the date changes:
  taking "not found" as "nothing to take off" would mark a cancel sent while the date could still be
  on VRChat (a later page, a month edge). Dates wait for their series: nothing is sent for a date
  while the series itself has a write waiting, and after every create or update of the series each
  date still to come is looked for and sent again, since VRChat puts a moved date back at its
  planned time under a new id when the series is written (seen 2026-10-01) -- except a cancelled
  date before where the series now starts, which the series no longer holds. A finished event's dates are still sent: an event finishes when its
  last date is cancelled, and that date's delete has to go out. Settling (20 s), one write a pass,
  and the rules for refusals and no answers are the series' own.
- **An update carries `accessType`** (added 2026-10-01). The SDK's `UpdateCalendarEventRequest` has
  no such field, so until then every update -- on master too -- went without one and VRChat refused
  it with a 400, "Can't unpublish or change access type once the calendar entry is published".
  `CalendarUpdateBody` adds the same word the create sent (`group` or `public`, from "Visible to");
  `isDraft` stays `false`, as in the create. Checked by capturing the SDK's own request body on this
  machine, not yet against VRChat.
- Cancelling, deleting, or unticking VRChat deletes the event on VRChat. A finished event is left
  there: it is history on VRChat's side as well.
- A write VRChat refuses is not sent again until the event changes, a save or Try again clears it,
  or -- for a missing group permission -- a read of the group finds the permission (§17, changed
  2026-10-02). An update or delete that got no answer (timeout, VRChat's own 5xx) is tried again
  after 15 minutes. What VRChat said (`error.message` in its reply) is what the event's VRChat
  status shows, not the bare HTTP reason.
- **A create with no answer is never sent again on its own** (changed 2026-10-01). VRChat has
  answered a create with a 500 and saved the event anyway (2026-09-25, and three times on
  2026-10-01), so a 5xx, a timeout or an answer with no id says nothing about whether it saved:
  - The place shows **Waiting**, not Failed, and no `publish.fail` fact is written: nothing is
    known to have failed yet. VRChat's words go to the log.
  - **What the create sent is kept** on the place (`create_sent`: title, times, repeat), because
    VRChat's copy holds that, not a fix made while it is looked for. The copy is matched against it.
  - Two minutes later the group's calendar is read for it: every page of the month the sent time
    starts in, and the month beside it when the start is within 14 hours of the edge
    (`GetGroupCalendarEvents`, `calendar.read`, the same paged read as §12). A copy is a row with the
    same start and length as sent (for a repeating event, one of its times), the same title once
    both are cut down to their letters and digits, made no earlier than two minutes before the
    create was sent, and not owned by another Modbot event.
  - **A copy found is taken as this event's own** (adopted): its id is kept and the place is
    Published. If the event was edited after the create was sent (a title or time fixed during the
    wait, say), the edit goes out as an update. An event no longer wanted has its copy deleted.
  - **None found after a whole read:** the place is Failed with *VRChat did not add the event.*,
    and that is when the `publish.fail` fact is written. Nothing is sent until a moderator presses
    **Try again** on it (`POST /api/calendar/events/{id}/vrchat/try-again`; the place's
    `canTryAgain`). An edit does not send it either. A read that could not reach the end of a month
    (more than three pages) decides nothing, and the look is made again 15 minutes later; so is a
    read that failed.
  - **Try again looks once more first**, with the same read: a copy that turned up since is adopted
    and nothing is sent. Only with none there is the create sent. If that look fails, the place goes
    back to waiting for the copy as above.
  - The calendar read (§12) does the same: a row that is a copy of a create with no answer, or one
    waiting for Try again, is adopted rather than taken in. A copy of one found not added is still
    adopted for 24 hours after the create was sent (VRChat may show it late). After that a late copy
    is neither adopted nor taken in as a new event: the title guard (§12.2), which holds the sent
    title as well as the current one, still keeps it out.
  - **Why** (2026-10-01): the old look ran once, only before an automatic retry 15 minutes on; it
    compared titles exactly and read one page. VRChat changes titles (it dropped an en dash and
    turned "." into a look-alike dot), so neither the look nor §12.2's title guard recognised the
    copy, and the read about two minutes later took it in as a second event, which then made its
    own Discord event. The retry would have made a third.
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
- Unticking the channel post deletes the post. Mentions are off, as on every message the bot sends,
  except the one role an event may name (§3.3.1).
- The channel picker marks a channel missing View Channel, Send Messages or Embed Links.
- **Deleting an event ends its post as "Cancelled"**, the same last word a cancel gives it, at once;
  the Discord event is ended as for a cancel. A delete is a cancel that is also hidden, so the post
  goes through the cancel's path.

**Old posts come down (added 2026-10-01).** Nothing ever removed Modbot's posts, so a channel with a
weekly event filled with old cards and "Cancelled" lines, a card per date and a line per cancel.

- **Every post Modbot made in the channel is deleted a day after its event or date was due to end**
  (`CalendarDiscordPublisher.PostKeptFor`): the cards, whatever their last word (finished, cancelled
  or deleted), one per date of a repeating event; the whole-event "Cancelled" line (§14.4); the
  one-date "Cancelled" line (§2.2). "Due to end" is the end the post showed: a moved date's own end,
  the event's length otherwise. A day lets a member who looks the morning after still see what
  happened. A deleted event's card stays red until then too.
- **Only messages Modbot holds the id of from posting them** are deleted, so nothing anyone else
  posted, and none of the bot's other posts (instance cards, giveaways), is ever touched. A card's
  id is kept in `calendar_old_post` when it gets its last word, because the card's place row moves on
  to the next date of a repeating event and forgets it. A whole-event line is marked on its place row
  (its state turns to removed; the message id stays, so it is never posted again), a one-date line
  on its date's row (`cancel_post_removed_at`).
- **Last in a pass, with what is left of its 5 calls, and at most 2 a pass**
  (`RemovalsPerPass`). Posting and editing come first; an install that already had many old lines
  works through them slowly, about six a minute at most.
- A post that is already gone -- someone deleted it by hand -- is marked done and not asked about
  again; so is any other refusal that will not change on its own. Anything else is tried on the next
  pass.
- **No fact for each removal.** The removal is written on its row and logged. A fact per old card
  would bury the calendar's real changes in the log; the post's last word already had its
  `publish.remove`.
- **Cards from before this change stay.** Until then a card's id was forgotten once it had its last
  word, and nothing else keeps it, so those cards can only be deleted by hand. Cancel lines kept
  their ids, so older ones are taken down.
- A one-date "Cancelled" line that could not be posted until after the time it would come down is
  not posted at all (it was a fixed day before; it is now the same `PostKeptFor`).

### 3.3.1 Mentioning a role (added 2026-10-02)

A review of a live server on 2026-10-02 found 16 "event notification" roles pinged by hand or by
another bot, while Modbot's post pinged nobody. The maintainer approved an opt-in mention. This
narrows the rule that Modbot's messages never ping (§3.3; the instance cards and giveaways keep it):
a ping has to be asked for, per event, and goes to one role.

- **One role per event, off by default** (`calendar_event.mention_role_id`). Picked in the form's
  Discord channel post section, **Mention role**, from Modbot's copy of the role list. The post's text
  is the mention, `<@&id>`, above the card; the Preview draws it as `@Name` in the role's colour.
- **Only a role the bot may ping.** A role whose "Allow anyone to @mention this role" is on
  (`discord_role.mentionable`), or any role when the bot holds Mention @everyone, @here and All Roles
  (`discord_server.bot_can_mention_everyone`). The picker marks the others **Bot cannot mention**, the
  way it marks a role the bot cannot assign, and a save naming one is refused ("The bot may not
  mention that role."). The role already on the event may be kept, so an unrelated edit is not
  refused for a change made in Discord since; Discord then decides whether it pings.
- **Never @everyone or @here.** @everyone's role id is the server's own: a save naming it is refused,
  the picker does not offer it, the publisher sends no mention for it, and the gateway posts with
  mentions off if asked to ping it. @here is not a role and cannot be named.
- **Pinged once per date, on the date's first post.** Discord's allowed mentions name that one role
  on that one message (`IDiscordGateway.PostMentioningRoleAsync`); nothing else in it can ping. Every
  edit, the last word included, keeps the mention text so the post goes on showing the role, and is
  sent with mentions off -- Discord does not notify for an edited-in mention either, but Modbot does
  not rely on that.
- **"First" is kept per date, not per message.** `calendar_role_ping` has one row for each date
  (the event's id and the date's planned start) whose post pinged, and the publisher reads every row
  of the event. So a post made again for a date that has pinged -- after somebody deleted it, after
  the post was turned off and on, or after the event was moved to another date and back -- shows the
  role and pings nobody. The next date of a repeating event pings again on its own first post. A row
  is written only once Discord took the post: a post that failed pings when it finally goes up. (Kept
  for every date rather than the latest one, 2026-10-02, because a single event moved A to B and back
  to A would otherwise ping A twice.)
- The cancel post and the post when the first person is in never mention the role.
- **Not built:** a default role per world list or category (not needed, 2026-10-02).

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
- **A refusal is not tried again for that occurrence.** It is shown on the event, on Health and as
  a red event block, and the next occurrence gets its own attempt. An automatic retry loop against a
  write VRChat just refused is the thing §4.3 exists to prevent.
- **Sent again only when it certainly made nothing** (changed 2026-10-01). Until then any failure
  gave up on the time, even one where nothing was sent (the gate waiting out a rate limit or a
  sign-in), and the event went without its instance. Now:
  - nothing left Modbot, Cloudflare stopped it, or VRChat answered 429: the row is marked
    `try_again` and a later pass sends it again while the time has not ended, a minute on at the
    soonest, and for a 429 only once the limiter's cold stop is over (never in the same pass). The
    row is cleared and marked in flight before each try, as before;
  - any other 4xx except 408: final, as before;
  - a 5xx, a 408, no answer, or a success with no location: VRChat has answered 500 while still
    making things, so a second request could open a second instance. The row is marked `checking`
    and **never sent again on its own**. Each pass looks, in `vrchat_instance` as the group instance
    poll recorded it, for an open instance of the event's world in the group (same access and
    region) first seen after the attempt and taken by no other time; one found becomes the time's
    instance. Once a poll that ran at least 15 s after the unclear answer came back
    (`calendar_opening.checking_since`, against `settings.group_instances_polled_at`) shows none, or
    the time ends, the row is shown as failed, the same `modbot.calendar.instance.fail` fact a refusal
    writes is recorded, and Open now is left to the moderator. Counting from the answer rather than
    from when the request left matters: a request can hang, and a poll that ran meanwhile proves
    nothing about what it made.
- **Open now** (added 2026-10-01): somebody with Manage calendar can open the instance for the current
  or next time, from two hours before its start until its end, while none is open and no attempt is
  in flight or `checking` (the page shows "Checking…"). Same path, same
  `instances.create` budget, at interactive priority; the row records who pressed it
  (`opened_by_user_id`) and so does the fact. Whenever a time's instance opens after an earlier one of
  that time closed (Open now, a later try, or one found while checking), the people its earlier
  instance left out go back on the queue, and the first-person posts may go again for the new one.
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
on a timer is within VRChat's Terms of Service before this feature is promoted any further. The
same review covers what follows an opened instance: the invites and direct messages, and the group
post when the first person is in (calendar auto-invite design §11). It stays as built until then;
nothing in the user docs speaks to it.

**Inviting people, and the first person** (added 2026-10-01). Once the instance is open, an event can
invite its host, staff and one saved list, by VRChat invite or Discord direct message, and post once
in Discord and in the VRChat group when the first person is in. See the calendar auto-invite design.

## 5. Rate limits

| Class | Lane | Rate | Source |
|---|---|---|---|
| `calendar.write` | `calendar` | **1 per 60 s**, shared by create, update and delete | **Not measured.** The maintainer asked for "a very lax rate limit by default" because VRChat's calendar limit is strict. Must be confirmed. |
| `calendar.read` | `calendar.read` | **1 per 10 s** | **Not measured**, same reason. Used for the look for a create that got no answer (§3.1), and for reading the calendar back when someone opens a page or presses Refresh (§12.1). Never on a timer. |
| `instances.create` | `instances.create` | **1 per 5 s** | Measured by the maintainer. |
| `files.upload` | `files.upload` | **1 per 60 s** | **Not measured.** Taken from the codebase on 2026-10-01: `calendar.write`'s pace, since the only upload is an event's VRChat picture (§15). Not scoped to the group, because the file belongs to Modbot's VRChat account. |
| `invites.send` | `invites.send` | **1 per 30 s** | Set by the user 2026-10-01, not measured. Inviting people to an opened instance (calendar auto-invite design §4). |

All four are resource-scoped to the group where it applies, count against the global backstop,
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
| `modbot.calendar.picture.upload` | A VRChat picture was uploaded (added 2026-10-01, §15); carries the file id, the event's id and title when it was already saved, and the size and type |
| `modbot.calendar.feed.regenerate` | The feed link was replaced |
| `modbot.calendar.invite.send` | A person was invited to the instance (subject: the person; calendar auto-invite design §8) |
| `modbot.calendar.invite.fail` | A person could not be invited (subject: the person) |

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
- Discord's handling of external event covers fetched from arbitrary picture links. Whether VRChat's
  calendar shows exactly 16:9 at every size (§15.3; taken from VRChat's default event picture, not
  measured), and whether a `vrchat.com` gallery link carries the same id VRChat's calendar takes.
- **One date on VRChat (§3.1, added 2026-10-01).** That VRChat's month list gives each date of a
  series an id of its own; that a delete or update by that id changes only that date (and not the
  series); whether an update to one date wants `parentId`; and whether VRChat keeps a changed date
  through a later update of the series. Built from the spec's model (`occurrenceKind`,
  `occurrenceModified`, `seriesId`, `parentId`), not from a call to the real service.
- Whether `POST /file/image` with the `gallery` purpose needs VRChat+ on Modbot's account, what size
  VRChat takes, and its real rate limit (§15).

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
- **VRChat's settings the form does not have** -- host and guest early join, closing the instance
  after the end, roles, instance overflow -- are kept on the event and sent back with every create
  and update. The SDK's update body sends `featured` and `usesInstanceOverflow` as false when they
  are left out (checked 2026-09-27), so without this an edit from Modbot would switch them off.
  Events made in Modbot keep sending what they always did. Featured was one of them until
  2026-10-02; it is a form field now, copied in like the rest.
- A one-off event from VRChat has no time zone; it is kept as UTC until a moderator picks one.
  VRChat's "after N times" end is kept as a number of times (until 2026-10-02 it was counted out to
  a last date), and its interval as every N.
- **Not taken in:** drafts; a yearly series, or one more than 52 apart or more than 500 times
  (Modbot's rule has neither; every second week and more was added 2026-10-02); and a row with the
  title of a Modbot event whose create has no id yet, or whose VRChat
  place was just removed -- it may be that very event, and taking it in would make a second.
  Titles are compared by their letters and digits only, since VRChat changes the text it is sent
  (2026-10-01). A row that is the copy of a create with no answer is adopted instead (§3.1).
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
- **An event made in Modbot keeps its own title and description** (narrowed 2026-10-01). VRChat
  rewrites the text it is sent -- an en dash dropped, "." turned into "․" -- so its copy of a Modbot
  event's words differed from Modbot's, and when anything else made the event look changed on VRChat
  (its dates rebuilt after a series write, say) the read copied VRChat's rewrite over the
  moderators' words with nobody having edited anything. A rewrite and an edit on vrchat.com cannot
  be told apart, so for an event made in Modbot the words are Modbot's alone; times, repeat and
  VRChat's own settings are still copied in. An event made on VRChat still takes VRChat's words.
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
  was not a refusal is tried on the next pass. It is deleted a day after the date it names was due
  to end, and the row turns to removed (§3.3, "Old posts come down").
- A second cancel of a cancelled event changes nothing and never makes a second post. A tick on an
  event with no channel is refused. The body may be left out, as before: nothing is posted.
- Discord scheduled events still end and vanish on a cancel, as before.
- The cancel's fact carries `postInChannel`, so the audit log says whether members were told.

## 15. The VRChat picture (added 2026-10-01)

The picture link only ever reached Discord, and VRChat's calendar needed a `file_…` id that nobody
had a way to make, so in practice events reached VRChat without a picture. The maintainer kept the
two fields apart (a picture link for Discord, a VRChat picture for VRChat) and made the second an
upload.

- **Uploaded when it is chosen, by its own request**, `POST /api/calendar/vrchat-picture` (Manage
  calendar), not when the event is saved. Saving stays plain JSON and never waits on VRChat, which is
  what the rest of the calendar is built on (§3, §9): an upload waits on its own one-a-minute budget
  and can be refused, and a refusal belongs on the picture field, at the moment the picture is
  chosen, not on a save that would then fail as a whole. The answer is the file id; the form puts it
  in `vrChatImageId` and the save stores it like any other field. Publishing is unchanged.
- **The request body is the picture**, no multipart form. PNG or JPEG only, told apart by the first
  bytes rather than the Content-Type, and at most **10 MB**. VRChat publishes no limit for this
  endpoint, so 10 MB is a guess kept well above an event picture and well under what an image host
  is likely to refuse. Anything else is refused before VRChat is asked (400, or 413 for too big).
  Since 2026-10-02 the form always sends the PNG or JPEG its crop box made (§15.3), whatever kind of
  picture was chosen.
- **One request, through the gate**, `FilesApi.UploadImageWithHttpInfoAsync` with
  `ImagePurpose.Gallery`, on `files.upload` (§5) at interactive priority. A 429 cold-stops the class
  and answers 429 with a plain sentence; nothing is sent again (foundation §4.3.1). Any other refusal
  carries VRChat's words.
- **Modbot keeps none of the bytes.** The body is read into memory, sent, and dropped; only the file
  id is stored, on the event. The file lives on the VRChat account Modbot signs in as, and the
  privacy page says so.
- **An operator's switch, off by default** (added 2026-10-01): Settings → Modbot's VRChat login →
  Pictures → *Upload VRChat pictures* (`settings.vr_chat_picture_uploads`), with a *Get VRChat+* link
  beside it. It first shipped on; the maintainer decided the same day to ship it off, after VRChat
  answered the first real upload with 403 "You don't have permission to use tag: gallery." (it very
  likely needs VRChat+ on Modbot's account; VRChat does not say so). Scripted uploads are also the
  part of Modbot's VRChat use a terms-of-service review would look at hardest, and the project's value
  is that an operator can switch off what leaves the server. Every install starts off, new or
  upgraded. A refusal that names the gallery tag shows VRChat's words and the same link under the
  picture field. Off, the endpoint answers 409 "Picture
  uploads are off." before it reads the body, VRChat is never asked, the calendar read says
  `pictureUploads: false` (what the calendar read says whenever it is not on), and the form shows the old typed *VRChat image id* box instead of
  *Choose picture*, so an event's existing id can still be seen and changed. Changing it writes a
  settings-changed fact.
- **Large bodies** are refused before they are read: a `Content-Length` over the limit gets 413 with
  nothing read, and the endpoint sets its own request body limit to 10 MB plus one byte (as imports
  do), so a chunked body cannot be read past that either.
- **Remove** clears the id on the event. The file stays on VRChat: deleting it would be a second
  VRChat write for nothing members can see.
- **The thumbnail** in the form is drawn from the file on the person's own computer, so it shows only
  for a picture chosen on that page, or (since 2026-10-02) from the picture link when the VRChat
  picture came from it (§15.1). A saved id, or one an event made on VRChat brought with it, reads
  "VRChat picture set": Modbot has no address for it without asking VRChat.
- **Fact:** `modbot.calendar.picture.upload` with the person as actor. Its subject is the event's id
  when the form sends `eventId` (an event already saved); for a new event there is no id yet, so the
  subject is the file id, and the event's `create` fact names the same file as `vrchatImageId`. No
  member data is in it.

### 15.1 A VRChat link fills the VRChat picture (added 2026-10-02)

A tester call on 2026-10-02 showed the two picture fields asking for the same thing twice: the
picture link was a VRChat file link (`https://api.vrchat.cloud/api/1/file/file_…/1/file`), and the
VRChat picture wanted the `file_…` inside it, which the tester then cut out by hand, kept `_blob` on
the end of, and had refused by VRChat at publish time with no word on what was wanted.

- **A picture link on VRChat's hosts fills the VRChat picture.** In the form, when the picture link
  changes and the VRChat picture is empty, or is the one the old link gave, it becomes the file id in
  the new link, or empties when the new link has none. A VRChat picture chosen any other way (an
  upload, an id typed) is never touched. Only VRChat's own hosts (`vrchat.cloud`, `vrchat.com`) count:
  `file_` in somebody else's address is somebody else's file. The form does this, not the server, so
  an API caller says what it means and gets exactly that.
- **The VRChat picture takes any VRChat link** (`VRChatFileIds.Find`, the same rule in the form):
  the whole link, with `/1/file` or `/image/…/1024` after the id, `_blob`, a query string, or the id
  alone. An id in VRChat's usual form (`file_` and a UUID) is taken exactly, which drops anything
  after it. Any other `file_…` is taken up to the first character that cannot be in an id in a link,
  so an odd id VRChat really issued still goes through: **this finds ids, it never checks their shape**
  (foundation §3.1.1). Only text with no `file_` in it, or a link on another site, is refused, and
  only while the event goes to VRChat (§17.2: the box is hidden otherwise), with an
  example: "Use a VRChat file link or id, like file_1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d." An id the
  event already holds is kept as it is, whatever it looks like, so an id read back from VRChat
  (§12) never stops an edit.
- With uploads off the field is still the typed box; a link pasted there becomes the id inside it as
  it is pasted, and text with no id says so once the field is left.

### 15.2 Fetching a picture link (added 2026-10-02)

Testers paste links that are not a plain `.png`: a page that shows the picture, a signed CDN link
with a query string, a host that answers with WebP. A browser cannot read another site's picture
into a canvas to crop it, and VRChat serves its files only to a signed-in session, so Modbot fetches
the picture. **`POST /api/calendar/picture-link`** (Manage calendar), body `{ url }`, answers with the
picture's bytes; Discord's event cover (§3.2) is fetched the same way when it is published.

Every fetch is Modbot's server calling an address a person chose, so the guard (`PictureLinks`) is
the webhook's and more:

| Rule | How |
|---|---|
| https on port 443 only, no user name or password in the link | refused before anything is sent |
| Public addresses only | a literal or `localhost` is refused before sending; every connection goes through `PublicAddresses.ConnectAsync`, which resolves the name and drops loopback, private, link-local (the cloud metadata address among them), carrier-grade NAT, multicast, documentation and IPv6 equivalents, so a name that resolves somewhere private reaches nothing |
| Redirects | never followed by the HTTP stack; each one is read, checked like the first link, and followed at most **3** times |
| Size and time | at most **10 MB** (a larger `Content-Length` is refused unread, a body is read one byte past and no further); **15 s** for the whole fetch, redirects included |
| A picture by its bytes | PNG, JPEG, GIF, WebP, BMP, AVIF or HEIC, told apart by the first bytes (`PictureFormats`), whatever the host called it. SVG and HTML are never pictures |
| A page | read only as far as its `og:image` (its secure form first) or `twitter:image`, in the first 512 KB; that picture is fetched under the same rules and must itself be a picture. A page whose picture is a page gives nothing |
| Nothing else | no proxy from the environment (it would connect somewhere the check never saw), no cookies, nothing of the person's sent, nothing kept |

- **A VRChat file link** (`vrchat.cloud` and below) is fetched through the VRChat side
  (`IPictures`, the gate's file fetch), and only while the operator lets this server fetch VRChat
  pictures (`settings.vr_chat_images_proxied`); off, the endpoint answers 409 and VRChat is not asked.
- **The answer is somebody else's bytes on Modbot's own address**, so it goes out with the type its
  bytes have, `nosniff`, a `sandbox` content security policy and `no-store`, as the VRChat file route
  does.
- **Discord.** The event cover now goes through the same fetch, so a page or a host that mislabels its
  picture gives a cover where before it gave none. A cover on VRChat is fetched with Modbot's VRChat
  session, and only while VRChat pictures are on. Discord takes PNG, JPEG, GIF and WebP; a cover in
  another kind is left off. On the channel post a link anywhere but VRChat is put on the card as it
  is (Discord fetches it); a VRChat link, which Discord could not fetch, is sent with the message as
  the world's picture is (Discord embeds design §3).
- **Why the server does not convert.** It has no image library, and adding one (a native decoder in
  the container) for the one picture an event has is more to keep safe than it is worth. The browser
  already decodes every kind it shows, so the conversion is done there (§15.3) and the server only
  ever passes bytes on. A signed CDN link that has expired by the time the event is published gives
  no cover, the same as any other link that stops answering.

### 15.3 The crop box (added 2026-10-02)

The tester's worry was whether a picture "is going to fit": VRChat crops what it is given. VRChat's
event pictures are **16:9** (an event with no picture of its own shows VRChat's 1024 × 576 one,
VRChat's wiki); Discord's event cover is **2.5:1** (the 800 × 320 Discord's own help asks for, and
the shape the preview already drew).

- **With uploads on, a picture opens in a 16:9 crop box** before anything is uploaded: one chosen on
  this computer (any kind the browser can draw: PNG, JPEG, WebP, AVIF, GIF's first frame, BMP, HEIC
  where the browser reads it), the picture behind a picture link given in this form (fetched as in
  §15.2, about a second after the typing stops), or, with Crop, the picture link a VRChat picture came
  from. The box starts as the largest one in the middle, what VRChat would show; it is dragged to
  move it (or moved with the arrow keys) and sized with a slider, never past the picture's edges.
- **Upload** draws the crop onto a canvas no wider than 2048 px (never enlarged) and sends it as a
  PNG, or a JPEG when the PNG would be over 10 MB. That is the picture §15 uploads; the file VRChat
  makes from it is the event's VRChat picture. Cancel closes the box; a link closed or removed is not
  opened again on its own. A link the form opened with is not opened on its own either: editing an
  event does not fetch anything until a link is given.
- **Save or Schedule with a crop still in the box uploads it first.** Pressing Schedule with a crop on
  screen means the picture is wanted. The upload is still its own request; the save waits for it, and
  a refused upload stops the save and shows why on the picture field.
- **The Preview tab shows the crop** as VRChat's picture, drawn in the browser, before anything is
  uploaded; after it, the uploaded picture, and for a VRChat picture from the picture link, the
  link's picture at 16:9. VRChat is sent only the file id, so this part of the preview is the
  browser's, not the server's (§14.2).
- **With uploads off VRChat's crop box never opens**: nothing could be uploaded, and a box that
  changes nothing would mislead. Discord's (§15.4) does.
- The crop is held by the form, not the field, so it survives the Preview tab and the VRChat chip
  being turned off and on; the picture is let go of when it is uploaded, cancelled, replaced, or the
  form closes.

### 15.4 The picture cropped for Discord (added 2026-10-02)

The maintainer chose, the same day, a second crop box for Discord that can be moved like VRChat's.
Discord's event cover is 2.5:1 and VRChat's picture 16:9, so one crop cannot serve both.

- **Where it shows:** a *Discord picture* field under the picture link, while the Discord event or
  the channel post chip is on. It needs no setting and no VRChat+: nothing goes to VRChat.
- **The same crop box**, at 2.5:1: a picture chosen on this computer, the picture behind a picture
  link given in this form (§15.2, once the typing stops), or, with Crop, the picture link again. A
  picture chosen in either field opens in every crop box shown, so one choice serves both places,
  each cropped to its own shape. Upload, Cancel, Remove, Save uploading a crop still open, and the
  Preview tab drawing it before it is uploaded all work as for VRChat's (§15.3).
- **Modbot keeps it** (`calendar_cover_picture`: the bytes, their type, when and who). Discord is
  sent a cover's bytes each time the event is made or changed, and the crop exists nowhere else, so
  something has to hold it between the form and the Discord loop. Keeping a crop rectangle instead
  and cutting the picture on the server was the other way; it would need an image library on the
  server (the one thing §15.2 avoids) and the picture link would have to be fetched again, and still
  answer, every time the event changed.
  - `POST /api/calendar/cover` (Manage calendar): the body is the picture, PNG, JPEG, GIF or WebP by
    its first bytes, at most **8 MB** (under Discord's 10 MB for a cover); a larger
    `Content-Length` is refused unread. Answers `{ coverId }`, saved on the event as
    `coverPictureId`, checked to exist when the event is saved.
  - `GET /api/calendar/covers/{id}` (See calendar): the bytes, with their type, `nosniff`, a
    `sandbox` policy, and a week's private cache (a cover never changes).
- **Deleted when nothing uses it:** when its event is deleted or given another picture, unless a copy
  of the event (Duplicate keeps the picture) still points at it. One uploaded and never saved is
  deleted once it is a day old (long enough for any form still open), by `CalendarCoverSweep`, which
  the calendar service runs in a loop of its own once an hour, so it is gone within the hour after
  whether or not anybody uploads again (changed 2026-10-02: the first build swept only on the next
  upload, which the docs' "a day later" did not match). Deleting the row empties `coverPictureId` on
  any event that still pointed at it, so an id never dangles.
- **What Discord gets:** with a picture cropped, the Discord event's cover is its bytes, in place of
  the picture link, and the channel post carries it as a file the card points at
  (`attachment://cover-{id}.png`). It is read only when something is to be sent. **Every post, edit
  and last word sends the files its card points at** (changed 2026-10-02 after review): an edit used
  to point at the file the first post left, which broke the card when that was no longer the file it
  wanted (a cropped picture removed leaving the world's picture the post never carried, a world
  picture that could not be fetched once, or a deleted event's last word after its picture was
  deleted). Now the message carries exactly what its card points at, and a card that points at an
  address carries no file. The fingerprint names the picture's id only when there is one, so events without one are not
  sent again for nothing. Without one, the picture link works as before.
- **The preview** names `GET /api/calendar/covers/{id}` as the cover and the post's picture once
  uploaded, and draws the crop in the browser before.
- **In the audit log** the event's create and change facts carry `coverPictureId`; the picture itself
  is not in them.

## 16. Copies of an event in Discord (added 2026-10-02)

A review of a live server on 2026-10-02 found its upcoming Discord events listed twice: two other
bots (one mirroring VRChat's calendar) and a person had made Discord events, two of them for the
same evenings, and Modbot's calendar was trying to make its own for the same titles (refused for
want of Manage Events). Members saw two of everything. Modbot
cannot know which copy a server wants to keep, so it **finds them and says so; it deletes nothing**.

- **Read from Discord, whoever made them.** `IDiscordServerEvents` asks Discord for the server's
  scheduled events (`GET /guilds/{id}/scheduled-events`, one request through Discord.Net's
  `GetEventsAsync`), only when the calendar page or Health is opened, and keeps the answer five
  minutes for everybody, the way the online count does (`DiscordServerEvents.KeepFor`): at most
  twelve requests an hour while somebody looks, none while nobody does. A failed read is kept too,
  so a Discord that is refusing is not asked on every page open. The gateway's scheduled-event
  intent is not asked for: the list is read when it is looked at, and nothing has to be kept up to
  date in between. Discord.Net waits out Discord's own rate limits on this request as on every other.
- **What is kept of each:** its id, title, start, end, whether it has started, and who made it --
  **Modbot** (the bot's own account), **a bot or app**, named, or **a person**, never named: the
  list is about tools, and a member's name has no place on it. Discord gives no maker for events
  made before late 2021; those read **Unknown**. Nothing is stored in the database.
- **What counts as a copy** (`DiscordEventDuplicates`):
  - **The title,** compared by its letters and digits only, in lower case, with a bracketed
    tag at the front left out (unless that leaves nothing) and accents and styled letters reduced to
    plain ones. So case, spaces, punctuation and emoji do not count, and "[VRChat, Group Public]
    Movie night" is "Movie night". A bracket after a word stays, so "Game Night (Among Us)" and
    "Game Night (Minecraft)" are two events. Two titles are the same when they are equal that way, or one holds the
    other whole and the shorter has at least six letters (`ShortestContainedTitle`), so "Art" is not
    found inside "Watch party".
  - **The time:** starts at most **15 minutes** apart (`StartsWithin`). A tool that rounds, or a
    copy made by hand, is a few minutes off; two different events with the same title that close
    together are rare enough that the list says "possible".
  - Copies of copies are one group: three events where the first matches the second and the second
    the third are shown together.
- **Modbot's own copy is marked.** A copy whose id is held by a calendar event's Discord place is
  Modbot's, and the page names that calendar event and opens it ("Modbot's calendar", Open). A copy
  the bot made that no calendar event holds any more still reads **Modbot**.
- **Where it shows.** The calendar page: a **Possible duplicates in Discord** card under the
  calendar, only when there are some, one block per group with each copy's title, who made it, and
  its time when it differs (`GET /api/calendar/discord-duplicates`, See calendar). Health: one line
  per group in the Calendar card, leading to the calendar page. The page says nothing when the list
  could not be read; neither does Health.
- **Not built:** deleting a copy, or telling Modbot's calendar to stop making one. Either would be a
  choice between tools that belongs to the server's owners, and is made in Discord or in the other
  tool's settings.

## 17. Sending, failing and trying again (added 2026-10-02)

A recorded test on a live install on 2026-10-02 went like this. An event was scheduled; the event's
box sat still for a while and then said **VRChat calendar: Failed**, because VRChat did not take the
picture id. The id was fixed and the event saved again; the box went on showing the old failure until
the new one arrived: Modbot's VRChat account lacked **Manage Group Calendar**. The permission was
given in VRChat, the event saved again with nothing changed, and nothing happened. A duplicate of the
event was refused too. The testers asked for four things, built here.

### 17.1 A permission given is picked up

**Why nothing happened.** A refusal was held until the event changed (§3.1): the place kept what
it had tried to send, and was not sent again while the event would send the same thing. That is right
for a field VRChat does not accept, and wrong for a permission: giving it changes nothing about the
event, so saving the event again unchanged kept the same content and the same hold, and the place
showed the old failure for as long as nobody changed a field VRChat sees. Modbot's copy of the
account's permissions played a part too: it is read with the group every five minutes, and a 403 was
labelled "needs Manage Group Calendar" whenever that copy did not show the permission (or had never
been read), so a refusal for any other reason could read as the permission while the copy was behind.

Now:

- **A refusal for a missing group permission stands only while the account lacks it.** When a read
  of the group -- the five-minute poll, or one of the reads below -- finds the permission, the next
  pass sends the event again, with no edit and no button. A refusal for anything else is still held
  until the event changes, a save clears it (§17.3) or Try again is pressed (§17.4).
- **A 403 is judged against a fresh read.** When VRChat answers a calendar write with 403, the group
  is read once more before the refusal is labelled: if the account has the permission now, the place
  shows VRChat's own words instead.
- **The read** is the group poll's own: `GetGroup` on `groups.read`, whose limit is already set
  (foundation §4.2, one request every 10 seconds), so no new endpoint. At most once a pass, and only
  when a send is due, which a hold keeps to once per change, save or press. A 429 cold-stops the
  class like any other and is not retried; the last read then stands.

### 17.2 Every problem at once

**Before sending** a create or an update, the VRChat calendar loop checks what it can know
(`CalendarVRChatChecks`):

1. **Manage Group Calendar**, as the group was last read. When that says it is missing, the group is
   read once more first (§17.1), so a permission given since is not refused from a stale copy. Not
   read yet is not a problem: VRChat's answer says.
2. **The title** and **the description**, which VRChat requires.
3. **The VRChat image id.** Never checked for its shape (foundation §3.1.1: ids are opaque); only
   what cannot be an id at all is refused: a space in it, or a slash, backslash, question mark or
   hash, which a link has and a path segment cannot. What a person pastes is saved as the file id
   inside it (`VRChatFileIds.Find`, §15.1, which takes any VRChat link, not only the API host's;
   this replaced taking the id from between the slashes, 2026-10-02). Every id the save takes out
   of a paste passes this check, so the save and the send agree on what an id is.

Anything found fails the place with **every problem together, the permission first**, and nothing is
sent: the place's error holds every sentence, its `problems` the ones other than the permission, and
its missing permission is shown first with the link to the group's roles. It is held like a refusal.
One `publish.fail` fact carries every sentence in its `error`, the permission first; it has no
separate `fix`, which the Discord card would show as the same sentence twice.

**A refused save names everything at once.** The form's save (`POST` and `PUT /api/calendar/events`)
checks every field and answers 400 with `problems` (one sentence each, in the form's order) and
`error` (the same, joined); until now it stopped at the first. When the event goes to VRChat's
calendar and the last read of the group says the permission is missing, that comes first. **On its
own it never refuses a save**: the read may be minutes old, a save never waits on VRChat (§9), and
the event is still worth saving for Discord; the loop reads the group again before it refuses to
send. The image id rule above is a save problem too, while VRChat calendar is ticked (the box is
hidden otherwise, and a problem in a hidden box could not be fixed): text with no file id in it is
refused with an example (§15.1), and an id the event already holds is refused only for what the
check above refuses. With VRChat calendar unticked, what was typed is kept as it is (the id in it,
when it has one) and never blocks the save. A changed value is checked when
VRChat is on, and an id the event already holds is kept as it is. VRChat itself
refuses a bad one at sending.

### 17.3 Being sent

- **A place being sent says so.** The event's box shows **Sending…**, with a turning mark, for a
  place that is waiting (for edits to settle, its turn, or a rate limit), and also for a ticked place
  Modbot has not made a row for yet: right after a save the loops have not run, and before this the
  box showed nothing at all for up to a pass. **Waiting** reads **Sending…** wherever a place's state
  is shown.
- **The box reads the event again every 4 seconds** while anything on it is being sent or the
  instance is opening. Not every step writes a fact for the live stream (an edit reaching a place
  that already had the event writes none, §14.1), so "Sending…" could otherwise outlast the sending.
- **A save clears the old failure at once.** Saving the event, or one of its dates, puts every place
  that failed back to waiting with nothing of the failure left, and the loops send it again even when
  nothing they send changed: saving again is how a moderator says "send it again". Two are left
  alone: a VRChat create that got no answer (it is looked for first, §3.1) and one VRChat did not
  add (only Try again sends it). A failed error is shown only while the place is failed, so an old
  error no longer shows beside **Sending…**.
- **A place whose content is back to what was last sent** (an edit undone, say) is published
  again without a write, rather than left failed or waiting for a write that never comes.

### 17.4 Try again

- **Every failed place has Try again**, for people with **Manage calendar**: the VRChat calendar,
  the Discord event, the channel post, the cancel post, and one date's own VRChat change. It sends
  it again, as it is, without editing the event.
- `POST /api/calendar/events/{id}/{place}/try-again`, `place` one of `vrchat`, `discordEvent`,
  `channelPost`, `cancelPost`; with `plannedStartsAt` in the body, one date of a repeating event on
  VRChat. The place goes back to waiting and the next pass sends it. The route is the one VRChat's
  "not added" Try again had (§3.1), which keeps its own way: one more look at VRChat's calendar, then
  the create. A create with no answer whose look could not be made is looked for now, never sent
  without the look. A refusal for a missing permission is checked against a fresh read first
  (§17.1), so a press before the permission is given sends nothing to VRChat's calendar.
- **A second press is refused** (409, "There is nothing to try again."): the place is waiting by
  then. The button stays off while its request is out.
- **The instance** is tried again with **Open now** (`POST /api/calendar/events/{id}/open`, §4),
  which already shows after a refused opening and refuses while an attempt is under way or being
  checked. It gets no Try again of its own: two buttons beside each other doing the same thing
  were one too many (changed before release, 2026-10-02).
- `canTryAgain` on a place now means "failed, and Try again sends it again"; until 2026-10-02 it
  meant only a VRChat create that was not added.

**Not built:** a Discord refusal for a missing permission is still held until the event changes, a
save or Try again; Modbot does not watch the bot's Discord permissions for it. The posts for the
first person have no Try again.
