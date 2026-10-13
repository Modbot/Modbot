# Modbot — Event alerts

- **Date:** 2026-10-13
- **Status:** Built with this document (step 2 of 2)
- **Covers:** members signing up for the event categories admins make in Modbot, and being told
  before a matching event starts, by direct message or a ping in a channel
- **Builds on:** calendar design §19 (categories, step 1) and §19.4 (the hook this fills); Discord
  commands design §3.5 (`/remindme`, whose sending and clean-up this copies)
- **Replaces:** the self-assigned "ping me for karaoke" roles a community keeps by hand

---

## 1. What the maintainer approved (2026-10-12)

| | Answer |
|---|---|
| How people are told | Both a direct message before the event and a ping in a channel are available. |
| What an alert is about | The categories admins make in Modbot (`CalendarCategory`, calendar §19). |
| A *Notify me* button | On each Discord event post. |

The rest of this document is the defaults chosen to build those answers. They can change without
touching what is approved.

---

## 2. What a member sees

- **`/notify`** (everyone in the server, private). Opens a form with a list of the Modbot
  categories, the ones the member has already ticked ticked, and, only when both ways of being told
  are on, a second list: *Direct message* or *Ping in a channel*. Sending it replaces the member's
  category sign-ups with what is ticked. Nothing ticked takes them all off.
  - The form is Discord's own form with a list in it (a "select menu"), the one place Discord lets a
    bot show a list that ticks several things. A list holds 25 choices; with more categories than
    that the first 25 by name are offered.
  - The reply says what the member will now be told about, or that they will be told about nothing.
- **A *Notify me* button** under each event's channel post. Pressed, it signs the member up for the
  categories that event carries; for an event with none, for that one event. The reply (private)
  names what they signed up for. Pressed again by someone already signed up for all of it, it takes
  those sign-ups off again. The button is on the post only while alerts are on.
- **The alert.** One message per member per event, before the start. By direct message:
  **Title** starts *time* (*in 59 minutes*), with a Join link where `/events` would show one (the
  same words as a `/remindme` reminder). By ping: one Modbot post in the chosen channel with the
  same words, mentioning the members who chose ping.

A member needs no Modbot account for any of this, as with `/remindme`.

---

## 3. What an admin sees

On the *Event categories* page (calendar permissions, §7 of the calendar design), a second card,
*Alerts*, for those with Manage calendar:

| Setting | Default | Notes |
|---|---|---|
| Alerts | off | Master switch. Off, nothing is queued or sent, the *Notify me* button comes off the posts and `/notify` is not registered in Discord. |
| Direct message | on | |
| Ping in a channel | off | Needs a channel; saving with it on and no channel is a 400. |
| Channel | none | Picked like the calendar's channel. |
| Minutes before the start | 60 | 5 to 1,440; outside that is a 400. |

It saves in one request (`PUT /api/calendar/alerts`) and writes a settings-change fact for each
value that changed (§8). With alerts on and neither way on, nothing is sent; the card does not
forbid it, because it is a state someone passes through while changing the other two.

`/notify` is not a switch on the Commands card. Whether it exists in Discord follows the Alerts
switch, so there is one place to turn alerts on and no way to have the command without the alerts
behind it.

---

## 4. What is stored

All in Modbot's database, and none of it ever sent to VRChat, Google or anyone else.

| Table | One row is | Keyed on |
|---|---|---|
| `event_alert_signup` | A member's wish to hear about one category, or one event | Discord user id, and a category id *or* an event id (never both). One row per member per category, one per member per event. Both ids cascade: a deleted category or event takes its sign-ups with it. |
| `event_alert_member` | A member's choice of way (direct or ping) | Discord user id |
| `event_alert` | One event date that has been alerted | Event id and the date's planned start (§5) |
| `event_alert_delivery` | One member's alert for one `event_alert` | The alert, and the Discord user id |

The settings (§3) are columns of the existing settings row.

The ids are opaque text, never parsed or validated (foundation §3.1.1). The sign-up names the
category by its id, so a rename changes nothing for the member.

---

## 5. When an alert goes out

A pass runs every twenty seconds in the calendar's Discord loop, only while the bot has a ready
session and only while alerts are on. It does two things, in this order.

**Queue.** For each event that `/events` would show (published in this Discord, scheduled or open;
the rule `/remindme` uses, so an alert never names an event the server does not already show) and
that someone is signed up for (through one of its categories, or itself): for each date that has not
started and whose start is no more than *minutes* away, and that has no `event_alert` row yet, add
the row and one delivery for each member signed up. The unique index on the event and date makes
this safe if two passes meet.

- **Once.** An event date alerts once. A *one-time* event alerts once for good: moving its start
  later, or earlier, after the alert was queued does not alert again, because any existing
  `event_alert` for a one-time event counts. A *repeating* event alerts each date once, a date being
  named by its planned start (calendar §2.2), so a date moved on its own is the same date.
- **Re-arms only if not yet sent.** The due time is worked out from the date as it stands now, every
  pass, and nothing is stored until the row is queued. A start moved before the row exists simply
  moves when it is queued. A delivery still waiting when its date moves later waits for the new
  time.
- **Cancelled and deleted.** A cancelled or deleted event, and a date cancelled on its own, is not in
  `/events`' rule or is not a date, so it queues nothing; deliveries already waiting for it are
  marked skipped.
- **Who.** The members signed up, once each however many of the event's categories they chose. Left
  the server (`discord_member.left_at` set) or banned: skipped. A member whose way is *ping* gets a
  ping delivery, *direct* a direct one. Without a stored choice: direct where it is on, otherwise
  ping. A way that is off in the settings falls back to the other when that one is on, and is
  otherwise not queued.

**Send.** Waiting deliveries, in order, a few a pass, so a long list is a trickle.

- **Direct message.** At most 5 a pass, 2 seconds apart (the same pacing as `/remindme`). The row is
  marked *sending* before the message goes, in one statement that only succeeds while it is still
  waiting: a restart in between leaves it *sending*, counted as sent and never sent again. Sent,
  or failed. A closed direct message (Discord error 50007) is *failed* and logged, never tried again.
  Any other refusal, a rate limit included, is also *failed*: nothing is retried.
- **Ping.** The members who chose ping for one event date go in one post in the chosen channel, in as
  many posts as Discord's limits need: a post holds 2,000 characters and may mention 100 people, so
  a post carries up to 80. One post a pass. It goes with the post-send rule (`RetryMode.AlwaysFail`:
  the library never sends a message again by itself, so a timeout comes back unclear and is *failed*
  rather than risking two posts). The post's allowed mentions are exactly those members, and nothing
  else it contains can ping.
- **Checked again just before sending:** the event is still one `/events` shows, the date still
  exists and has not started, the alert time has come, the way is still on, the member has not left.
  Otherwise the delivery is *skipped* (or left waiting, when the time has not yet come).
- **Alerts off** leaves queued deliveries where they are; they send if alerts are turned on again
  before the event starts, and are skipped otherwise.

When an alert's last delivery is done, a fact is written (§8) and `finished_at` is set.

---

## 6. The post's button

The channel post is made by the calendar publisher (calendar §3.3). While alerts are on, every post
it makes or edits carries the *Notify me* button, whose id is `modbot:notify:` and the event's id.
The publisher's fingerprint (§17) gains the button only when alerts are on, so a server that never
turns them on re-sends nothing; turning them on or off edits each live post once, on its next pass.
A post for a date that has ended keeps no button.

The press is handled like `/remindme`'s: the member's Discord id comes with it, no Modbot account
is needed, and the reply is private.

---

## 7. What is kept and for how long

- Sign-ups and a member's way are kept until the member changes them, the category or event is
  deleted, or a **purge** removes the person (the purge counts them in its own record, naming
  nobody; `PurgeResult.EventAlertsDeleted`).
- `event_alert` rows, and with them their deliveries, are deleted 30 days after they were queued.
  Deliveries name a member by id, so none stays without an end.
- The audit log records the alert (§8), never who it went to.

Rate limits: `/notify` and the button count against one per-person limit (the same size as
`/remindme`'s), apart from the other commands'.

---

## 8. Audit log

| Fact | Subject | When |
|---|---|---|
| `modbot.calendar.alert.send` | The event's id | An alert finished. Carries the title, the date, and how many went by direct message, by ping, how many direct messages were closed, how many failed and how many were skipped. Names no member. |
| `modbot.settings.change` | `settings` | Each alert setting that changed, as the calendar's public-feed switch does. |

The alert fact is **short-retention** (the presence class, 90 days by default) like the other
bot-send markers: it is plumbing, not history, and it names no one, so a purge has nothing to find
in it.

---

## 9. Not built

- A web page for a member's sign-ups: `/notify` is the only place. (A member would need a Modbot
  account to have a page; most of the people this is for have none.)
- Alerts by VRChat invite or email.
- A count of sign-ups per category on the Event categories page.
- A different lead time per member or per category.
