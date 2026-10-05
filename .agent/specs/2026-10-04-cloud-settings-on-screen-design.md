# Modbot — Modbot Cloud's settings on screen

- **Date:** 2026-10-04
- **Status:** Implemented with this document
- **Covers:** a Modbot Cloud box on the companion's Settings page; a new Cloud Server page where the
  person chooses which events and which details the Cloud backup sends; what the backup then sends;
  and Modbot Cloud (`src/Modbot.Cloud`) taking an event with no world id or instance id
- **Reverses:** the cloud event backup design (2026-09-15, §0.1 and §3.1: "the switch is gone from
  the screen") and the cloud backup stays out of the way design (2026-09-19: "don't announce, don't
  show, don't indicate that modbot cloud exists"), for the two new places below. The Events page is
  exactly as the second of those left it.
- **Related:** `CloudBackup/CloudChoices.cs`, `CloudBackup/CloudSettings.cs`,
  `CloudBackup/CloudEventBackup.cs`, `CloudBackup/CloudWire.cs`, `MainWindow.Cloud.cs`,
  `Presentation/CompanionSettings.cs`, `src/Modbot.Cloud/Features/EventBackup/`,
  `docs/content/docs/companion/settings.mdx`

---

## 1. What the maintainer asked for, and why it reverses two decisions

On 2026-10-04 the maintainer asked for the backup to have a place on the screen: a Modbot Cloud
checkbox on the Settings page, and a page of its own where a person picks what the backup sends. The
two earlier specs decided the opposite: no Cloud switch on screen (2026-09-15, after the first two
versions let a paired server and then the window decide), and no screen naming the backup at all
(2026-09-19, after the Events page showed a pill per place).

What those decisions were protecting still holds, and is kept:

- **Nothing about Cloud comes from, or depends on, a paired server.** Pairing, unpairing and pausing
  change nothing here, and the new page changes nothing about what a paired server is sent.
- **The Events page says one word per event** (`JournalRow.State`). It does not name the backup and
  the new page does not feed it. The source guard still forbids the window from reading the journal's
  Cloud state.
- **The environment variable still wins.** `MODBOT_CLOUD_DISABLED` decides, and the box shows the answer
  and cannot be changed. `cloud.endpoint` and `MODBOT_CLOUD_ENDPOINT` are untouched.

What changes is that a person is now told the backup exists, and given a say in what it holds. That is
the point of the request: "what leaves the machine" should be a choice with its own page, not a file
somebody has to know to edit.

## 2. The screens

### 2.1 Settings

A card, **Modbot Cloud**, after Clips and before Listening. Its title row is the whole card: the name,
a small information icon, and one checkbox at the right end. On is the opposite of
`cloud.disabled`. It is a copy of the box on the Cloud Server page; the two are one setting.

The icon's tip is three short paragraphs and nothing else on either page explains anything:

1. Modbot Cloud keeps a copy of what your app sees, held apart from your group's server.
2. **For your group:** if its server is ever lost or reset, a copy of what its moderators saw still
   exists. Cloud keeps events for a year by default.
3. Your group's server still only gets your group's instances.

### 2.2 Cloud Server

A page after Settings and before Credits. One card, **Modbot Cloud**, with the same icon and the same
box. Under a plain heading, **Instance Data**, two folding sections:

| Section | Events (each a box) | Details (each a box) |
|---|---|---|
| **Group instances** | Joined, Already here, Left, Avatar changed, Modbot stopped logging | World ID, Instance ID, Group, Avatar name |
| **Non-group instances** | the same five | World ID, Instance ID, Avatar name |

A non-group instance is one no group owns. It has no group, so it has no Group box. Each section's
header says "N of 5 events" and carries a box nobody can change: ticked while Modbot Cloud is on and at
least one of its events is.

A third folding section, **Always sent**, holds six boxes nobody can change: Display name, User ID, App
version, Unique ID (the event id), Event Type, and Time / Offset to Cloud (the time, the clock offset
and confidence, and the batch's send time). They are ticked while Modbot Cloud is on and not while it
is off. There is no clock setting.

Below the card, **Next batch** lists the events queued and not yet sent, newest first, up to five, with
what each carries and "not sent" where a detail was left out. With nothing waiting, or with Modbot
Cloud off, it says "Nothing is sent."

### 2.3 Behaviour

- **At least one event must stay on across both sections.** Unticking the last one is refused: it stays
  ticked and an amber line says "At least one event must be on for Modbot Cloud to work." Any other
  change clears it. One section may be fully off; the other then cannot lose its last.
- **Switching Modbot Cloud off** folds every section, greys out every box that can be changed (they keep
  their ticks), and unticks the six required rows and the sections' own boxes. Any section can still be
  opened and read. **Switching it on** opens the sections that were open before.
- Every section folds and unfolds by clicking its header, on or off.
- A test copy (`MODBOT_DATA_FOLDER`) sends nothing to Modbot Cloud whatever the file says, and its box
  shows off and cannot be changed, the same as with the environment variable.
- **It takes effect at once.** Off sweeps away everything queued, in memory and on disk, and stops the
  backup's loop, exactly as starting with it off does. On starts clean: nothing seen while it was off is
  ever sent. A change to the choices applies to events not yet written to the outbox; one already
  waiting goes as it was queued.

## 3. Storage

`settings.json`, inside the existing `cloud` object (`endpoint` and `disabled` stay):

```json
{ "cloud": {
    "endpoint": "https://cloud.modbot.co",
    "disabled": false,
    "group": {
      "events": ["joined", "alreadyHere", "left", "avatarChanged", "stoppedLogging"],
      "worldId": true, "instanceId": true, "groupId": true, "avatarName": true },
    "nonGroup": {
      "events": ["joined", "alreadyHere", "left", "avatarChanged", "stoppedLogging"],
      "worldId": true, "instanceId": true, "avatarName": true } } }
```

- **Everything defaults on**, so an install that never opens the page sends what it always did. A missing
  object or field is on.
- **The client writes only what it owns:** `disabled`, `group` and `nonGroup`. It leaves `endpoint`,
  anything else in `cloud`, and the rest of the file as it was. A file that cannot be read as JSON, or
  whose `cloud` is not an object, is left alone.
- **An `events` list that is there and names nothing** (a hand-edited file) leaves a section with no
  events. With none on in either section the backup has nothing it could send, so it does not register
  and does not ask the time.
- An unreadable file is the defaults, as for every other setting, so a typo does not quietly stop the
  backup.

## 4. What is sent, and the old Cloud

Only events whose section has that kind ticked are queued. A detail whose box is off is **left out of
the event when it is queued**, so it is never written to the outbox on this PC either. Always sent: user
id, display name (when the log gave one; Cloud never requires it), app version, event id, type, time,
clock offset, clock confidence and send time. A clip event is never sent to Cloud, as before.

Modbot Cloud before this change refuses a whole batch with a `400` when an event has no world id or no
instance id, and the client deletes a refused batch. So the client must not leave them out for a Cloud
that has not been updated:

- `GET /api/v1/time` gains `acceptsMissingFields: true`. The client reads it on its existing clock check,
  which still happens only when there is something to send.
- A Cloud whose answer lacks it, or a check that failed, gets the word `hidden` in place of an omitted
  world id or instance id, put in when the batch is built (the outbox keeps it left out). A Cloud that
  says `true` gets them left out. A group id or an avatar name left out never needed this.

## 5. Modbot Cloud

An event now needs only an event id, a type, a time and a user id. World, instance and group may be
missing or blank and are stored as null (`world_id` and `instance_id` become nullable: one migration on
the event storage). The length limits stay. The admin page shows a blank for a missing world or
instance.

## 6. Where the Next batch card reads from

The plan named the journal's Cloud rows. The journal holds only one-line summaries, never the event,
and widening it would put names and ids into `sent.jsonl` for far longer than they sit in the outbox.
The card reads the backup's own outbox instead (`CloudEventBackup.NextBatch`): it shows exactly what
will be sent, with details left out shown as "not sent". The journal and the Events page are untouched.

## 7. Not built here

Install id rotation or reset, any change to what a paired server is sent, blurring the time, trends,
deploying Cloud, restoring events from Cloud.
