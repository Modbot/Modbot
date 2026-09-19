# Modbot — Notification filters in the companion

- **Date:** 2026-09-19
- **Status:** Implemented with this document
- **Covers:** which kinds of event raise a notification on the moderator's own PC, and by which of
  the three ways the companion has of telling them: the pop-up overlay, the sound, and the voice;
  the `notificationFilters` object in `settings.json`
- **Related:** the voice engine design (2026-09-18), the notification bleep and Restart design
  (2026-09-18), the two overlay modes design (2026-09-18), the Events page's filter chips in
  `Presentation/EventFilters.cs`

---

## 1. What a moderator asked for

> "Companion should have notification filters for various events and event types e.g. Notification
> for non-age verified user, notification for 18+ user, notification for flagged user etc."

Before this, what the companion told a moderator about was fixed. The voice had three switches
(joins, leaves, flagged joins); the sound and the pop-ups had none at all and fired for exactly two
things each. Nothing could be turned on that was not already on, and nothing could be turned off
except through the voice's three.

This adds one list of event kinds with a tick per way of being told, so a moderator picks what
interrupts them and how.

---

## 2. The event kinds, and where each one comes from

**Found, not invented.** Every kind below is something the client already has in its hands. The
list is the union of what the log reader produces (`Instances/ObservedPresence.cs`), what a paired
server pushes (`Overlay/LiveEvents.cs`), and what the companion notices about itself.

| Kind | Word in settings | Where it comes from |
|---|---|---|
| Joined | `joined` | The client's own reading of VRChat's log — `PresenceKind.Joined` |
| Already there | `already there` | The same — `PresenceKind.PresenceObserved`: somebody who was in the instance when the moderator arrived |
| Left | `left` | The same — `PresenceKind.Left` |
| Changed avatar | `changed avatar` | The same — `PresenceKind.AvatarChanged` |
| Log stopped | `log stopped` | The same — `PresenceKind.LogStopped`: VRChat stopped writing, so the client can no longer see who is there |
| Flagged join | `flagged join` | The paired server — the `flagged_join` live event, which becomes a `FlaggedJoinAlert` |
| Problem | `problem` | The companion itself — a server that rejected this device, or one that cannot be reached while the roster has gone stale |

**The words are the Events page's words.** `joined`, `already there`, `left`, `changed avatar` and
`log stopped` are spelled exactly as `EventFilters.Kinds` spells them, so a moderator who has used
the Events page's filter bar already knows this vocabulary and a hand-edited `settings.json` reads
the same way in both places. `flagged join` and `problem` are new here because the Events page has
no rows for them — the Events page lists what the client *reported*, and neither of those is
something the client reports.

There is an eighth kind in the code, `Test`, for the Test buttons. **It is never filtered and never
written to settings**, because a person pressed the button and wants the answer.

### 2.1 Which of the asked-for examples the client can actually tell apart

Two of the three examples in the request **are not things the companion can know today**, and
nothing here pretends otherwise:

| Asked for | Can the client tell? |
|---|---|
| Somebody **flagged** arriving | **Yes.** This is the `flagged join` kind. The server decides who is flagged and pushes the alert; the client shows it. |
| Somebody **not age verified** arriving | **No.** |
| Somebody **18+ verified** arriving | **No.** |

The 18+ mark is real and it is Modbot's own: it is stored per VRChat user on the server
(`Is18PlusVerified`, `AgeVerificationStatusLastSeen`) and shown in the web app. But **it is never
sent to a companion.** Everything a companion is told about a person travels in one of three
shapes, and none of them carries it:

- `LivePerson` (the live event) — id, display name, trust rank, standing, prior actions, flags
- `RosterMember` (the instance roster) — id, display name, standing, prior actions, flags, trust rank
- `FlaggedJoinAlert` — alert id, id, display name, instance, reason, prior actions, trust rank

So a filter for "somebody arrived who is not age verified" cannot be written on this side of the
wire. Making the client filter on something it does not have would mean either a silent no-op or a
filter that quietly matched everybody.

**What it would take.** One added field on `LivePerson` and `RosterMember` — the server's own 18+
mark, and VRChat's last-seen age verification status — and then two more kinds here (`not age
verified` and `18+ verified`) that read it. That is a server change and a companion change
together, and it is deliberately left out of this one: it widens what a paired server tells a
moderator's PC about a person, which is a decision about disclosure and not about notifications.
It is written down here so it is not lost.

---

## 3. One list of kinds, a tick per way

```
Notifications
  Sound on  [x]    Volume [--------]    (Test)

  Tell me about            Pop-up   Sound   Voice
  Joined                     [ ]     [ ]     [x]
  Already there              [ ]     [ ]     [ ]
  Left                       [ ]     [ ]     [x]
  Changed avatar             [ ]     [ ]     [ ]
  Flagged join               [x]     [x]     [x]
  Log stopped                [ ]     [ ]     [ ]
  Problem                    [x]     [x]     [x]
```

One row per kind, one column per way. The three ways keep everything else of their own: the sound
keeps its own on switch and its own volume, the voice keeps its on switch, its volume, its voice and
its output device, the pop-up overlay keeps its own switch, its place on the screen and how long a
card stays. **A way that is switched off tells nobody anything, whatever its column says.**

### 3.1 Why not one shared list for all three ways

The obvious design is one list — "these are the events worth telling me about" — with each way
keeping only its on/off and volume. It was tried on paper and it fails on the one requirement that
cannot be traded away: **a moderator who never opens this screen must keep being told exactly what
they are told today.**

Today the voice says joins and leaves by default, and the sound and the pop-ups do not. Under one
shared list there are only two possible defaults and both are wrong:

- `joined` **on** — the voice keeps talking, but now every arrival also bleeps and puts a card in
  the headset. That is a busy instance turned into a fire alarm, for somebody who changed nothing.
- `joined` **off** — nothing new interrupts, but the voice stops saying joins and leaves, which it
  has always said.

The three ways also genuinely differ in what they cost the person. A spoken name is passive and
hands-free; a bleep and a card in the middle of the view are interruptions. A moderator wanting
every arrival spoken and only flagged arrivals bleeped is the ordinary case, not an exotic one, and
a single list cannot say it. The moderator has previously asked for the three to be customisable
independently, and one list would have collapsed them.

So: **one list of kinds — one vocabulary, one place to look — with its own tick per way.**

### 3.2 Why not the Events page's filter chips

`Presentation/EventFilters.cs` already has a filter language: `kind:is:joined,left`, with
operators, several properties and AND across chips. Its **words** are reused here (§2). Its
**grammar** is not, and the setting is a plain list of kind names per way rather than a chip line.

A chip line would promise things this screen does not have. There is one property to filter on, so
there is nothing for AND to join; `is-not` says nothing that unticking a box does not say more
clearly; and `text:contains:` has no meaning for a notification that has not happened yet. Worse,
an empty chip set means "everything passes", which is precisely the default that §3.1 rules out.
Reusing the grammar would have meant writing a chip set that can never be edited as a chip set, to
express a checkbox grid.

---

## 4. The defaults, and why they preserve what happens today

`NotificationFilters.Default` is exactly what the companion did before this feature existed:

| Kind | Pop-up | Sound | Voice | Why |
|---|---|---|---|---|
| Joined | off | off | **on** | The voice said joins by default (`VoiceSettings.Joins`); nothing else did anything |
| Already there | off | off | off | Nothing reacted to it |
| Left | off | off | **on** | `VoiceSettings.Leaves` |
| Changed avatar | off | off | off | Nothing reacted to it |
| Flagged join | **on** | **on** | **on** | The card, the bleep and the line, all three, today |
| Log stopped | off | off | off | Nothing reacted to it |
| Problem | **on** | **on** | **on** | The banner, the bleep and the line, all three, today |

### 4.1 A settings file written before this feature

A file with no `notificationFilters` gets the defaults above, **except that the voice column is
read out of the `voice` object that is already in the file**:

```
voice.joins        -> voice column, "joined"
voice.leaves       -> voice column, "left"
voice.flaggedJoins -> voice column, "flagged join"
```

Which matters: a moderator who turned joins off a month ago must not have them come back on because
the client learned a new word for the same switch. The rest of the voice column takes the defaults,
because there was nothing in the old file to say otherwise.

A file that has `notificationFilters` but is missing one of the three columns takes that column's
default, the same way every other object in `settings.json` fills a missing field.

### 4.2 What happens to the Voice card's three switches

`Joins`, `Leaves` and `Flagged joins` **leave the Voice card**. They are the same three choices the
new grid's Voice column makes, and two controls for one choice is a screen that lies to somebody.
The Voice card keeps what is only the voice's: on, volume, which voice, which output device, Test.

The three fields stay in `VoiceSettings` and in the `voice` object of the file, for two reasons:
they are what §4.1 reads on upgrade, and a copy of the client older than this feature still reads
them. They are **kept in step**: saving the filters also writes the `voice` object with its three
fields matching the Voice column, so the file never says two different things. After the upgrade,
`notificationFilters` is what the voice actually consults.

---

## 5. Where each filter is applied

One gate per way, at the last moment before the moderator is told, so there is one place to look per
way and no path around it:

| Way | Gate |
|---|---|
| Pop-up | `PopUps.Wanted`, checked in `PopUps.Show` |
| Sound | `NotificationSound.PlayAsync`, beside the existing "is the sound on" and "is the volume above nothing" checks |
| Voice | `VoiceAnnouncer`, on the way into the queue |

`PopUps.Show` takes the kind as an optional second argument. A pop-up shown without one — the
overlay preview and the sample screens — is never filtered, because nothing about a preview is a
notification.

The sound's gate sits beside the switch and the volume rather than inside `BleepRule`, because
`BleepRule` is about **pacing** — the same thing not sounding twice in thirty seconds, and a quiet
gap between sounds — and mixing "is this wanted at all" into it would put two unrelated questions in
one answer.

### 5.1 The four kinds nothing used to react to

`already there`, `changed avatar`, `log stopped` and the presence kinds on surfaces that never had
them are new capability, all of it **off by default**:

- `Presentation/EventNotifier.cs` is a new observation sink, beside the voice's. It hears the same
  observations the voice and the Cloud backup hear, drops the moderator's own comings and goings,
  and offers each one to the pop-up overlay and to the sound. Both gates then decide.
- `VoiceAnnouncer` learns to say the three sentences it did not say before, in the Events page's own
  words (`SentJournal.Sentence`), so what is heard in the headset is what is read on the screen.

**It reads nothing and sends nothing.** `EventNotifier` holds no state beyond what it was handed;
no server is told that a pop-up was drawn, that a sound played, or that a filter is set the way it
is.

---

## 6. The setting

One new key, `notificationFilters`, in `settings.json` beside the rest:

```json
"notificationFilters": {
  "popUp": ["flagged join", "problem"],
  "sound": ["flagged join", "problem"],
  "voice": ["joined", "left", "flagged join", "problem"]
}
```

Its own writer, `CompanionSettings.SaveNotificationFilters`, which rewrites only its own object and
leaves every other field in the file exactly as it found it — the same rule as `SaveVoice`,
`SaveNotifications` and `SaveNotifyOverlay`, and the same refusal: **a file that cannot be read as
JSON is not overwritten.** Somebody who hand-edits their settings and leaves a trailing comma keeps
their file and their comment; they do not get it silently replaced.

A word the client does not know is ignored rather than treated as an error, so a file written by a
newer client — one that has learned `18+ verified`, say — still loads on an older one.

**Nothing here leaves the machine.** These are choices about what this PC does. No server is told
that they exist.

---

## 7. What was decided against

- **A filter on who the person is, not on what happened** — "tell me when anyone under level X
  arrives", "when somebody not age verified arrives". Not possible on the data the client is sent
  (§2.1) beyond flagged/not flagged, which is already a kind.
- **Per-group filters.** A moderator staffing three groups might want arrivals announced for one and
  not the others. Real, and deliberately not built: the whole notification path is about the one
  instance the moderator is standing in, so at most one group is ever in play at a time and a
  per-group list would be a control that does nothing almost always.
- **A quiet hours switch.** Would need the system clock, which the client never reads (foundation
  §4.4), and the honest answer to "not now" is the Sound on switch and the voice's.
- **Reusing the Events page's chip grammar.** §3.2.
- **One shared list across the three ways.** §3.1.
