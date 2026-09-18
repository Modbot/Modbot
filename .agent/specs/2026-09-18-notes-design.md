# Modbot — Notes

- **Date:** 2026-09-18
- **Status:** Implemented with this document
- **Covers:** writing a note about a person, reading them back, taking one back; where a note
  lives; the two permissions; where notes appear; the length cap and how the text is handled
- **Implements:** M4 §2 (the Modbot-side actions — this is the second of the three), §8.1
  (context at the moment of action)
- **Related:** import design §3.3 (`modbot.note.add`, which the importer has written since it
  shipped); ban case files design (the write-up of a ban, which a note is deliberately not);
  foundation §5.5 (a purge erases a person's facts), §5.9.2 and §5.9.4 (the two logs and their
  permissions), §3.1.1 (ids are opaque)

---

## 1. What a note is

One moderator's own words about a person, written in Modbot and read by everybody else who works
there. "Says they are leaving at midnight, ask them about the avatar." "Third time this week."
"Their friend vouched for them."

M4 §2 names three things a moderator can do that are Modbot's own rather than VRChat's: **warn**,
**note** and **watch**. This document is note. Warn is not possible — VRChat has no warning to
deliver and the maintainer settled on 2026-09-18 that it stays undone — and watch is untouched.

A note is **not** a case file. A case file is the write-up of one ban: reasons picked from the
group's list, a written justification, evidence, and a copy of the person's profile at the time. A
note is a sentence somebody typed because it was worth remembering. Nothing about a note is
required, nothing is classified, and a note is not about any particular action.

---

## 2. What already existed

`FactType.NoteAdded` — `modbot.note.add` — was already declared, labelled "Note added", already
classified as moderation history in `AuditVisibility`, and already routed to Discord by the
moderation log. One thing wrote it: the importer, carrying notes in from another platform's export
(import design §3.3). Nothing inside Modbot could write one.

So most of this feature was already built. What was missing was a way to write one and a place to
read them.

---

## 3. Where a note lives

**A note is a fact and nothing else. There is no note table.**

The alternative was a row with a fact beside it, the shape case files use. It was rejected, for
three reasons in descending order of weight:

1. **The importer already writes notes as facts.** A note carried in from an old bot and a note
   typed here would have become two different things, and the list a moderator reads would have had
   to merge a table and a log and pretend the result was one kind of record. Imported notes are the
   majority of notes in any deployment that migrated, so the table would have been the minority
   shape pretending to be the main one.
2. **A fact already carries almost all of a note.** Subject, actor, time, source and a payload.
   The only field a note adds is its text, and a table whose sole purpose is one text column is not
   a record — it is a duplicate of the fact with a second copy of the same words in it.
3. **Erasure.** A purge deletes a person's facts (`UserPurger`, foundation §5.5), so it deletes
   their notes, today, with nothing to add. A note table would have been one more place for the
   next person writing a purge path to forget, and "deletion works" is a claim Modbot has to be
   able to keep.

The cost is that a note cannot be edited. That is not a workaround; see §3.2.

### 3.1 The payload

```
{ "text": "...", "actorDisplayName": "...", "description": "..." }
```

`text` is the note. `description` is the same words again, under the key every timeline reader
already looks in: the audit-log row, the Discord card and the AI tools all read `description`, and
duplicating it there is cheaper than teaching each of them a new key. `actorDisplayName` is the
author's username as it stood when they wrote it, kept because accounts get renamed and a note
should not.

Reading is tolerant, because an imported note carries whatever its file's `data` held: `text`,
then `note`, then `description`, then an empty note. An imported note with no words anywhere is
still a note somebody wrote, and it reads as an empty one rather than vanishing from the list.

### 3.2 Nothing is edited; a note is taken back

The fact log is append-only (M4 §10) and a reversal is a new fact, never a deletion (M4 §9). Notes
follow the same rule:

- **There is no edit.** A note whose wording is wrong is answered with another note, exactly the
  way a ban that was wrong is answered with an unban rather than by rewriting the ban.
- **A note can be taken back**, which appends `modbot.note.take-back` naming the note's fact id.
  The note itself is untouched. On the notes list it stays, marked *taken back*, greyed, with who
  did it and when; it stops counting as standing and it stops being shown before an action.

Both facts are in the audit log, so "this was written, and then withdrawn" is answerable — which is
the property a delete would have destroyed. A note that was written and taken back is a different
thing from one nobody ever wrote, and neither the list nor the log is allowed to blur the two.

Taking back a note that is already taken back changes nothing and appends nothing.

**A take-back is not erasure.** The note's words are still in the log, readable by anybody who may
read the log. Somebody who needs the words gone needs a purge, which is the tool that erases, and
that is the honest answer rather than a delete button that leaves the text in a payload.

---

## 4. Who may write and read one

| | Permission | Bit |
|---|---|---|
| Read a person's notes | `ViewAuditLog` — the existing one | 3 |
| Write one, and take one back | `WriteNotes` — new | 31 |

### 4.1 Reading is the audit log's permission

A note **is** a fact in the moderation category, and `AuditVisibility` already decides who may read
those rows. Putting the notes list behind `ViewProfile` instead would have given the same
`modbot_event` rows a second, looser door: somebody with `ViewProfile` and not `ViewAuditLog` could
read a note's text on the Notes tab and not in the log. One set of rows, one gate.

This is not the shape case files use, and the difference is real rather than an inconsistency. A
case file's content lives in `case_file` and its facts are the audit trail *of* it; the row and the
log are two records, so two gates is coherent. For a note the fact *is* the record.

The roles page description of `ViewProfile` said "A member's history, notes and past actions" and
now says "A member's profile, their history and their past actions", because the old wording was
not true.

### 4.2 Writing has its own permission

No existing flag fitted.

- `Warn` (bit 11) is the verb this document explicitly does not build. Granting warn to allow a
  note would pre-commit what warn means.
- `Kick`, `Ban` and `Unban` are VRChat-side; a note changes nothing in VRChat.
- `EditClassifications` is about the reason list, which a note does not use.
- `ViewAuditLog` is reading, and writing is a different question.

Writing a note puts one moderator's words about a named person into the log every other moderator
reads before deciding what to do about them, and it stays there. That is the same power
`ImportOldData` was given a flag of its own for, and for the same reason: the volunteer who should
be able to read the log is not automatically the one who should be able to add a claim about
somebody to it.

`WriteNotes` is **bit 31** and is not in the built-in Moderator or Viewer roles; Administrator
holds it, as it holds everything.

### 4.3 Taking one back

`WriteNotes`, **or** being the note's author. The same rule case files use for editing and
withdrawal: a volunteer who has since lost the permission can still take back something they wrote,
and a note left behind by somebody who has moved on is not stuck there forever. The check is in the
handler rather than on the route, because a route attribute can only say "all of these flags".

---

## 5. The endpoints

Under `/api/notes`, tag **Notes**.

| | |
|---|---|
| `GET /api/notes?userId=&platform=&limit=` | One person's notes, newest first. `ViewAuditLog`. |
| `POST /api/notes` | Write one. `WriteNotes`. |
| `POST /api/notes/{id}/take-back` | Take one back. Author, or `WriteNotes`. |

`platform` is `VRChat` or `Discord` and defaults to VRChat; anything else is refused rather than
guessed at, because a note filed under the wrong platform is a note about somebody else.

The person's id travels in the query string or the body, **never in the path** — a route constraint
on a VRChat id would be a format check, and legacy ids follow no structure (foundation §3.1.1).
Only the note's own id is in a path, and that is Modbot's own number.

`POST /api/notes/{id}/take-back`, not `DELETE`. Nothing is deleted, and a verb that says otherwise
would be the wrong shape for what happens.

A host that mapped the API without a fact writer reads notes and answers 503 on a write, the same
way the moderation endpoints refuse to act rather than pretending to.

---

## 6. Where notes appear

### 6.1 The person popup, on a Notes tab of its own

Its own tab rather than a corner of an existing one. The merged **Logs** tab already carries every
note as a fact — and that is exactly the problem: reading a person's twelve notes there means
scrolling past four hundred joins, leaves and avatar changes. "What has the group written down
about this person" is a different question from "what has happened to them", and it gets its own
answer.

Not folded into **Cases**, either. A case file is the write-up of a ban; a note is not about any
action, and most people with notes have never been banned.

The tab is offered when the caller may read the log and there is an account to file notes under —
the person's VRChat account when they have one, their Discord account otherwise.

### 6.2 The confirmation before a kick, ban or unban

The standing notes, newest three, read-only, above the reason buttons. M4 §8.1: the moment a
moderator is about to act is the moment the group's own remarks about somebody are worth having,
and the only moment at which showing them costs nothing.

Nothing is drawn when there are none, when the caller may not read them, or when the read fails.
A confirmation must never grow a row saying "no notes": the question on the screen is whether to
ban somebody, and an absence is not evidence.

Taken-back notes are left out here. A note the group decided should not stand is not something to
put in front of somebody about to act.

### 6.3 Not the members list, and not the flags screen

Deliberately. A note is not a status, and a marker on every row would mean a lookup per row and
would invite moderators to act on the presence of a note rather than on what it says. Notes are
read where a person is read.

---

## 7. Length and content

**2,000 characters.** A note is a remark, not a write-up: the write-up of a ban is the case file,
which allows twenty thousand for exactly that reason. Two thousand is what a case file's withdrawal
note already allows, and a cap keeps one person's essay out of a list ten other people have to scan
before acting. The server refuses a longer one; the browser disables the button before the round
trip, which is a courtesy on top.

**Text, never markup.** The note is rendered as text with line breaks preserved — not through the
Markdown renderer case files use. A case file's written reason is a document; a note is a remark,
and a remark that can draw a heading or an image is a remark that can be made to look like
something Modbot said.

**On a Discord card it goes through the escaping that already exists.** The note's words are in the
fact's `description`, and `ModerationEventEmbed` puts `description` through `CardText.EscapeText`
and cuts it to 300 characters before posting. So a note reaches a log channel as typed, with its
markdown inert and its mentions disabled at send time, through the path every other piece of
user-written text already takes. Nothing new was written for this.

---

## 8. Retention and erasure

Moderation retention, by the rule that governs every unprefixed `modbot.*` type: kept. Erased with
everything else about a person by `UserPurger`, which deletes facts by subject — notes included,
with no code added, which is §3's third argument in practice.

A note about a moderator written by another moderator is a fact whose *subject* is the moderator,
so a purge of that person erases it. A note *they wrote* about somebody else survives their purge,
because it is a record about the other person and erasing it would delete somebody else's
moderation history (`UserPurger` keeps facts where the person is the actor, for exactly this
reason).

---

## 9. Still open

- **Searching notes.** There is no "everyone with a note saying X". The log's free-text search
  covers the payload today; a notes-specific search can be added when somebody wants it.
- **Notes about a world or an instance.** A note is about a person. The fact type could carry
  either, and nothing here decides that.
- **Warn and watch**, the other two verbs of M4 §2. Warn is settled as not possible. Watch is
  untouched.
