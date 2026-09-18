# Modbot — Notifications

- **Date:** 2026-09-18
- **Status:** Implemented with this document, except where §7 says otherwise
- **Covers:** the one pipeline foundation §4.5 asks for — severity, each person's own settings per
  channel, saying a thing once, the daily summary, and the critical nobody could receive
- **Narrows:** foundation §4.5 (it is now partly built, and this says which parts)
- **Related:** accounts and access design §4.4 (the daily email limit, unchanged); evidence storage
  design §8.4 (its critical notification, now connected); M6 §5 (its instance notifications, one of
  which is now checked for); AI insights design §8 (the unusual-activity alerts, deliberately left)

---

## 1. Why this exists, and why it is late

Foundation §4.5 has named `INotifier` since M0. M3, M4, M5, M6 and the evidence storage design all
list it as a dependency. It was never built.

What happened instead is four separate ways of reaching a person, each of which works:

| | What it does | Who it reaches |
|---|---|---|
| Health alerts | Emails when Modbot's own checks fail | Accounts on a list somebody chose |
| Discord event routes | Posts chosen fact types to chosen channels | Whoever reads that channel |
| Unusual-activity alerts | A card in the app, and a Discord post | Whoever opens the page |
| The companion's alert poll | A card in the SteamVR overlay | The moderator in the instance |

None of them is wrong. Each was the shortest path to the thing it does. But together they are what
§4.5 predicted: four routing decisions, four ideas of "too often", no per-person setting, and two
sentences in the specs — a critical notification when the evidence store goes missing, and a warning
when an instance closes with people still in it — that had nowhere to be raised and so were never
built at all.

This adds the pipeline underneath, moves one sender onto it, connects the two missing sentences, and
leaves the other three alone.

---

## 2. What was built

### 2.1 The pipeline

`Modbot.Core/Notifications/`:

- **`INotifier.RaiseAsync(Notification)`** — the one way anything says somebody should be told
  something. Writes rows and returns. Nothing is sent inside it.
- **`Notification`** — kind, severity, title, body, audience, optional link, optional `SameAs`,
  optional quiet time.
- **`INotificationChannel`** — a name, "can this reach this person right now", and "send this". A
  channel knows nothing about severity, settings or quiet times.
- **`NotificationRouting`** — every decision, as a function of its arguments, with no database and no
  clock. This is the part that is worth reading and the part the tests pin.
- **`Notifier`** — resolves the audience, decides per person per channel, writes rows.
- **`NotificationPass`** + `NotificationService` — the background pass that tries the channels and
  builds the daily summaries.

Five tables: `notification` (what happened), `notification_person` (who it was for, and whether they
have seen it), `notification_send` (what each channel did with it), `notification_choice` (one
person's setting for one channel), `notification_settings` (the quiet time, and an off switch).

### 2.2 Severity and routing

Three severities, exactly §4.5.1's. Each person has a **level** per channel — `off`,
`critical`, `warning`, `everything` — and a **daily summary** switch per channel.

Defaults, for somebody who has never changed anything:

| Channel | Level | Daily summary |
|---|---|---|
| Email | Critical only | On |
| Discord | Critical and warnings | Off |

A channel with no row uses its default, so nobody has to opt out of anything and there is no
migration when a channel is added.

**Anything a level silenced goes into that channel's daily summary when the summary is on.** Only
sending information there would make "critical only" quietly mean "critical only, and forget the
rest".

### 2.3 Channels

**Email** goes through `IEmailSender` as `EmailKind.Other`. The daily email limit and the twenty
sends kept back for account email (accounts and access §4.4) are untouched: a deployment drowning in
notifications still has room for a password reset.

**Discord** is a direct message through `IDiscordMessenger`, to the Discord account on the person's
Modbot account.

**Web push** is named in `NotificationChannels` and deliberately not in its list of channels — see
§7.1.

**The client is not a channel, on purpose.** §4.5.2 is right that the overlay is the only channel a
moderator in a headset has, and that is exactly why nothing was put in front of it. A flagged join
already reaches the overlay from the live stream as a fact, with nothing in between. Routing it
through a pipeline with a per-person setting and a quiet time would make the channel that matters
most the slowest of the four, to gain a preference nobody asked for. See §7.3 for what should change
there and what should not.

### 2.4 Saying a thing once

**Two notifications are the same when their `SameAs` matches.** The caller decides the key, and the
rule is that it names *the thing that is wrong*, not the moment it was noticed: `modbot.health.problem:sync`
is one key however often the check runs. A key that moved would make every raise look new, which is
the failure §4.5.1 exists to prevent. Unset, it is the kind, which is right when there is only one of
the thing.

**The window is the quiet time** — six hours by default, the same number the health alerts and the
unusual-activity alerts already use, and for the same reason. A caller that already had a quiet time
of its own with a control on a screen passes it with the notification, so an operator's number keeps
meaning what it meant.

Inside the window the notification is **counted, not sent**: `repeats` goes up, `last_at` moves, and
the message that eventually goes out says how many times it happened. Outside it, it goes out again,
because a problem nobody fixed is still a problem.

**Unless it got worse.** A warning that has become critical goes out whatever the quiet time says.
Without that exception, the severity that matters most is the one most likely to be swallowed by a
quiet time somebody set for something milder. The other direction is not news: a critical that is now
a warning is the same problem, still there.

### 2.5 The daily summary

Everything held back collects as `for-summary` sends and goes out as **one message per person per
channel per day**. The first is a day after the oldest thing in it; later ones are a day after the
last one. Counting from "now" would send a summary of one item the moment the first information
notification landed and then call it daily.

### 2.6 The critical nobody could receive

This is the most valuable sentence in §4.5 and the easiest to skip, so it is worth saying how it is
actually done.

**Everybody in the audience gets a `notification_person` row before anything is sent.** That row is
the record of who *should* have been told, and it exists whether or not any channel can carry the
message.

A critical notification then becomes **waiting** for a person in two places:

1. **At raise time**, when no channel could even be tried — email not set up, no address, no linked
   Discord account, every level set to off.
2. **In the send pass**, when everything that *was* tried has failed.

A send held only for tomorrow's daily summary does not count as reaching them. A critical that
arrives tomorrow morning is not a critical.

`GET /api/notifications` returns those waiting rows, and `WaitingAlertsBanner` puts them across the
top of every page, above everything, until the person marks each one seen. Marking it seen never
deletes it: what happened stays on the record.

### 2.7 The screens

- **Your account → Notifications**: a level and a daily-summary switch per channel, and a line
  saying when a channel cannot reach you. Nothing here needs a permission — it is one person's
  choice about their own alerts, the same kind of act as changing their own password. **No new
  permission bit was taken.**
- **The red bar at the top of every page**, beside the sign-in wait banner, for waiting criticals.

---

## 3. The sender that moved: health alerts

`HealthAlertChecker` no longer sends email. It raises through `INotifier`, and email is one of the
channels the pipeline chooses.

What stayed:

- The per-check state row (`HealthWatch`), so a restart in the middle of a problem does not start
  everything over.
- The recipients list. Being on it is an explicit choice by an administrator, so the audience is that
  list and not a permission.
- The quiet time on the health alerts card, passed with each notification.
- The rule that a recovery is said whether or not the quiet time is up, and only if the problem was
  said first.

What changed, and why:

- **The checker's own "is it due" gate is gone.** It raises every pass while the problem is there and
  the pipeline counts it. Two quiet times doing the same job is one of them being wrong eventually,
  and this is the case the pipeline was built for.
- **It does not also send its old email.** There is one path to an inbox now. Two would mean a
  deployment told twice, which is worse than not being told at all.
- **Each check has a severity.** VRChat unreachable, the Discord bot down, sync stopped, email stuck
  and logs not reaching Cloud are `Critical` — §4.5.1's own critical class, said another way. The
  database passing a size and a spending limit being reached are `Warning`: those are a line
  somebody drew, not something that has stopped working.
- **A recovery has the severity of the problem it closes.** "It is over" belongs to the incident that
  interrupted somebody; telling them about the break at once and about the fix in tomorrow's summary
  would leave them investigating something already fixed.

The visible effect for an existing deployment: storage and AI-spend problems now reach a Discord
direct message at once and email in the daily summary, rather than email at once. Everything else
arrives as it did. That is a deliberate narrowing of email volume, which is what §4.5.1 asks for.

---

## 4. The two sentences that had nowhere to go

**Evidence storage §8.4** has always said a locked store raises a `Critical` notification on every
available channel. There was nowhere to raise it, so a lock produced a banner and a log line and
nothing that reaches somebody who is not looking at the screen. `EvidenceStoreAlarm` raises it from
the startup store-marker probe, to everyone holding **Change settings**, keyed on the expected store
id so one incident is one notification.

**M6 §5** asks for a warning when an instance closes unexpectedly while populated, and there has
never been a check for it. `GroupInstanceSync` now raises one when an instance drops off the group's
list with **five or more** people still in it. Not one: an instance emptying out is how every evening
ends, and the last poll before a normal close often still shows one or two people on their way out. A
number this size means something took an instance away from people who were using it. The key is the
instance's own id, so it is said once.

Modbot only knows that the list stopped carrying it, never why, so the message says what happened and
does not guess at a cause.

---

## 5. How §4.5's list maps onto this

| §4.5 asks for | Where it is |
|---|---|
| Severity routing (critical, warning, information) | §2.2. Built. |
| Per-person, per-channel preference | §2.2, §2.7. Built, including per-channel daily summary. |
| A digest | §2.5, as **daily summary** — "digest" is not a word to explain to a volunteer moderator. |
| Deduplication of repeated notifications | §2.4, as **`SameAs` plus a quiet time**. Built. |
| Web push | Not built. §7.1. |
| A critical nobody can receive is surfaced at next sign-in | §2.6. Built. |
| Email channel | Built, through the existing sender and limit. |
| Discord channel | Built, as a direct message. |
| Client channel (toast and overlay HUD) | Deliberately not a channel. §2.3, §7.3. |
| Warnings escalating to email after a delay if unacknowledged | Not built. §7.2. |
| Channel health surfaced in settings | Recorded in `notification_send`; no screen yet. §7.4. |

---

## 6. What was deliberately not done

Building all of §4.5 at once, on top of four working systems, is how a half-migrated mess happens. The
scope was chosen to be the smallest thing that is provably the pipeline and not a fifth parallel
system: **build it, prove it by moving one real sender fully onto it, connect the two sentences that
had nowhere to go, and write down the order for the rest.**

The three senders that did not move are untouched. Each still works exactly as it did, and none of
them is now half on and half off the pipeline.

---

## 7. The order the rest should move in

### 7.1 Web push — first, because nothing else needs reshaping for it

VAPID keys in `Settings`, a service worker in `Modbot.Web`, a `push_subscription` table keyed by
person and browser, and a `WebPushNotificationChannel`. The pipeline needs no change: the channel
name is already reserved in `NotificationChannels`, the preference row already stores a level and a
summary switch for any channel name, and `NotificationChannels.All` is the only list to add to.

It is first because §4.5.1's warning routing ("push and Discord immediately") is not really honoured
until push exists, and because it is the only remaining item that is purely additive.

### 7.2 Warnings that nobody acknowledged escalating to email — second

Needs one thing the pipeline does not have: a person acknowledging a notification they *did* receive,
as distinct from seeing a waiting one. `notification_person.seen_at` is already the column; what is
missing is a pass that finds warnings sent a while ago and never seen, and sends them on the next
channel up. Small, and it completes §4.5.1's middle row.

### 7.3 The unusual-activity alerts — third

`AlertChecker` should raise through `INotifier` rather than writing a row that `AlertPoster` picks
up. Its quiet time and its "unless it gets much worse" rule are already the pipeline's, so the move is
mostly deletion. Two things must survive: the alert card on the Analytics and Health pages, which
reads its own table and should keep doing so, and the Discord *channel* post, which is not a direct
message and is therefore not something this pipeline does yet.

That last point is the real work: a channel post is a different kind of destination from a person.
Either the pipeline grows a "post it here as well" idea, or the alert poster stays as a second
consumer of the same row. Prefer the second — it is honest about the fact that a channel is not a
person.

### 7.4 Channel health in settings — fourth

`notification_send` already records every failure and its reason. §4.5.3 asks for that to be surfaced
as channel health rather than thrown at whoever raised the notification. One card on the settings
screen reading the last failure per channel.

### 7.5 Discord event routes — last, and possibly never

Routes post facts to channels with filters on who the fact is about. That is a feed, not a
notification: nobody chose to receive it personally, there is no severity, and deduplication would be
wrong. The right end state is probably that routes keep the fact feed and the pipeline keeps people,
and the two never merge. Revisit only if a route ever needs to reach a person rather than a channel.

### 7.6 The companion's alert poll — leave it

The long poll (`/api/v{n}/companion/alerts`) is already legacy: the shipped companion reads the live
stream and builds its overlay cards from facts. It stays for clients built before that change. The
overlay should keep getting flagged joins straight from the live stream for the reason in §2.3.

The part of §4.5.2 worth revisiting later is the *other* direction: a critical notification about
Modbot's own health should probably reach a moderator's headset, because that is where they are. That
is a `ClientNotificationChannel` that writes into the live stream, and it can be added without
touching the presence path.

---

## 8. Non-goals

- **A deployment-wide screen for the quiet time and the off switch.** `notification_settings` holds
  both and defaults are sensible; a control for them can wait until somebody wants one.
- **Retention.** Notification rows are small and there is no pruning yet. When there is, it belongs
  with the log and daily-totals retention, not here.
- **Grouping several notifications into one message outside the daily summary.** The daily summary is
  the answer to volume; a second grouping rule would be a second idea of "too often".
- **Anything that makes raising a notification able to fail.** §4.5.3 is absolute: a ban does not fail
  because SMTP is down. Nothing in this pipeline throws for a delivery problem, and a channel that
  cannot even say whether it is available is treated as unavailable.
