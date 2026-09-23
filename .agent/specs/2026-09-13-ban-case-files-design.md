# Modbot — Ban Case Files

- **Date:** 2026-09-13
- **Status:** Implemented with this document
- **Covers:** the reason list, the written report, the profile snapshot taken at ban time, and the
  list of bans nobody has written up
- **Implements:** foundation §5.8.2 (classification is one tap), §5.8.3 (ban reports), §5.8.1
  (friction scales with reversibility), §10.2 (the subject pane)
- **Depends on:** the evidence storage design, which already stores blobs against a report id and
  had nothing writing one; the user profile sync, for the profile this copies; member and ban sync,
  for the membership and ban list rows
- **Unblocks:** the accountability signal "bans without a report"

---

## 1. The problem, in the maintainer's words

> *"Moderation audit log comes in, 'Gunner24 banned GayHater59'. In Modbot, Gunner24 uploads a
> detailed markup of why GayHater59 was banned and attaches video/screenshot evidence, and Modbot
> also stores a copy of their user profile at the time of banning."*

VRChat's audit log carries **no reason anywhere** — confirmed against real entries, research doc
§2. What it records is that a ban happened, who did it, and to whom. Three months later, when the
ban is disputed, when the moderator has left, or when a federation partner asks why, that line is
almost useless.

The evidence storage layer already exists and every blob it holds can cite a `ReportId`. Nothing
was writing one. This is that.

---

## 2. The reason list

`ban_reason`: id, label, description, sort order, active flag, and whether picking it means the
written reason cannot be left empty.

Foundation §5.8.2 settles the shape and the reasoning is not restated here: a row of buttons costs
a moderator a second and gets used; a free-text field gets a single-digit completion rate. The
classification is **the signal**, not paperwork — it is what lets the accountability checks tell
"five kicks, all *Crashing*, four other moderators agree" from "five kicks, all *Other*, nobody
else has ever touched this person".

**Seeded on first read, not by the migration.** Every time Modbot stamps comes from `IModbotClock`,
and a migration has no clock; and a default set that changes between releases then costs nothing,
because a deployment that already has a list keeps it. Same shape as `GetSettingsAsync`: "the rows
might not exist yet" is handled in one place.

The starting list is plain: Harassment, Hate speech, Crashing or malicious avatars, Underage, Ban
evasion, Spam, Other. **Only "Other" requires the written reason**, because a case file that says
only "other" says nothing.

### 2.1 There is no delete

A case file names its reasons by id. A reason that vanished would take that case file's
classification with it — quietly, and only visibly much later when somebody asks what the group
banned people for last year. So a reason is **switched off**: it leaves the buttons and stays on
every case file that picked it, which is the only safe version of "get rid of this one".

Editing the list needs `EditClassifications`. Reading it needs only a session, because everybody
who writes a case file needs the buttons and a label is not sensitive. Every change is a fact
naming who made it: a reason quietly reworded from *Crasher* to *Other* would change what every
old case file appears to say.

---

## 3. The case file

`case_file`, one row per ban:

| | |
|---|---|
| Who | `user_id` — opaque, never validated (foundation §3.1.1) |
| Which ban | `audit_entry_id` when the audit log recorded one, else `ban_fact_id`, else neither |
| Who wrote it | the Modbot account id **and** the username at the time (§5.9.1) |
| Why | the reason ids as `jsonb`, and the written reason as Markdown |
| The person then | `profile_at_ban`, `membership_at_ban`, `ban_list_entry_at_ban`, all `jsonb` |
| When | created, updated, snapshot taken, profile last refreshed |
| Withdrawn | timestamp, who, and a required note |

**One case file per ban.** A second for the same audit entry — or for the same person after the
same ban — is refused with a 409 that names the first, because two write-ups of one ban is two
answers to the same question and the page cannot say which the group meant.

**Nothing is ever deleted.** A case file can be marked withdrawn with a note; it stays readable,
stops counting as the write-up of its ban, and cannot be edited again. Withdrawing is saying *this
should not have been written*, so the honest consequence is that the ban is unwritten again.

### 3.1 The edit history is facts, not columns

`modbot.report.created`, `.updated`, `.withdrawn` and `.snapshot.recaptured`. The subject is the
banned person, so the case file appears in their timeline beside the ban itself; the actor is
always the Modbot account, because VRChat attributes everything Modbot does to Modbot's own account
and this is the only place per-moderator attribution exists (§5.9.1).

`.updated` carries before and after. The row says what the case file is now; the log says what it
has ever said, and a column can never do that.

The row and its fact commit in one transaction. A case file with no record of who wrote it is the
failure §5.8 exists to prevent.

---

## 4. The profile snapshot

### 4.1 Copied, not fetched

Taken from the `vrchat_user`, `group_member` and `group_ban` rows Modbot already holds, as
structured `jsonb` — never a rendered picture (evidence design §12), so *"everyone we banned in the
last year whose bio mentioned this Discord invite"* stays one query against the existing GIN index.
That is how a group finds the rest of a coordinated group after catching one member.

**The write-up never waits on VRChat.** Evidence design §12.2 and M3 §7.3.1 both say it: a lookup
must not be able to delay or fail a moderation record. So the order is *snapshot first, refresh
second* — the case file is written from what Modbot holds, and only then is the profile sync asked
for a fresher copy.

The request goes in at the **"opened in Modbot" tier**, not the top one. Tier 1 is for people a
client is seeing in an instance right now, and a case file queued there would both jump that queue
and misreport on the sync health page. Tier 2 is the tier a moderator's own look already uses.

### 4.2 It reads and never writes

The three rows belong to the sweeps and the profile sync. A snapshot that touched them would be a
fourth writer with its own idea of what "seen" means — every query is `AsNoTracking`, so nothing
here can be saved by accident.

### 4.3 One recapture, and why there is one at all

The snapshot is what it says: it does not change after capture. But the profile it copies may have
been six hours old at that moment, and the banned person edits their name and clears their bio
within the hour — which is exactly why the snapshot is worth having and exactly why a stale one is
a shame.

So: the page says how old the profile was when captured, and offers **"refresh and capture again"
once**, and only when VRChat has actually answered with something newer. The snapshot it replaces
travels in full inside `modbot.report.snapshot.recaptured`, so the first capture is never lost and
"never changes after capture" stays true of the record even where it is no longer true of the row.

Both the moment of capture and the age of the profile at that moment are shown. A bio from six
hours before the ban and a bio from the minute of it are different claims, and a page that rendered
them the same way would be inventing the difference away.

### 4.4 What is not captured

**The profile picture's bytes.** Evidence design §12.3 leaves image capture out until somebody has
asked VRChat what the rate limit on its image hosts is — foundation §4.3.4 forbids inferring one
from a neighbouring endpoint, and §4.3.4.1 is a worked example of that exact mistake. The snapshot
keeps the URL, and the page shows the image if VRChat still serves it. That is the honest state:
Modbot never copied the bytes.

**Group membership beyond the managed group**, for the same reason: `users.groups` is deliberately
isolated because nothing has measured it.

---

## 5. Where it appears

- **The Bans page.** The group's ban list grows a case file column: *Open the case file*, or
  *Write the case file* for whoever may ban. One lookup answers a whole page of rows rather than
  one request per name.
- **"Bans with no case file"**, `GET /api/cases/missing`. Bans in the last 30 days, newest first,
  with the reason a ban counts as covered stated in §6. This is the pipe the accountability signal
  reads.

> **Narrowed 2026-09-23.** Two things here were taken off the Bans page: the *What the audit log
> recorded* tab, and the card above the tabs that listed bans with no case file. Both were asked
> for and both were built; the page they were on turned out to open on a nag about work outstanding
> rather than on the list somebody came to read. The endpoint stays, because it is the pipe an
> accountability signal reads and nothing about it depended on the card. Who issued a ban is still
> recorded and still readable on the audit log page — that was never only in the tab.
- **The subject pane.** A person's case files beside their history, withdrawn ones labelled — a
  case file that was written and then withdrawn is a different thing from one nobody ever wrote.
- **`/cases/:id`**, deep-linkable for the reason §10.2 gives for the pane: one that cannot be
  linked is one nobody shares.

---

## 6. When a ban counts as written up

A ban is covered when a case file **that stands** either names its audit entry, names its ban fact,
or was written for the same person at or after the ban with no audit entry of its own — the last
case being a write-up made from the group's ban list, which carries no entry ids.

A withdrawn case file covers nothing.

The list counts only bans the **audit log** recorded, so it covers the window Modbot's sync covers
and nothing before it. An empty card is not proof that every ban was written up, and the card says
so rather than letting an absence read as an assurance — the same care the ban list's coverage
notice already takes.

---

## 7. Permissions

No new flag. The existing four carry it:

| Action | Needs |
|---|---|
| Read a case file, the list, the unwritten list | `ViewProfile` |
| Write one | `Ban` |
| Edit or withdraw one | `Ban`, **or** being its author |
| See the evidence on one | `ViewEvidence` |
| Attach evidence | `UploadEvidence`, and being able to edit the case file |
| Change the reason list | `EditClassifications` |

Reading is `ViewProfile` rather than `ViewAuditLog` because a case file is part of a person's
history and is shown wherever their history is.

**The author can edit their own write-up** even after losing `Ban` — a volunteer who steps back
from moderating should still be able to correct their own account of something. That is a fact
about the row rather than about the person, so the endpoint decides it and the browser is told the
answer rather than guessing at it.

`ViewEvidence` stays separate, unchanged from evidence design §14: a moderator who needs to know
this person was banned for harassment does not automatically need to watch it happen. Where it is
absent the case file reads normally and the evidence list is `null` — not zero, which would be a
claim about how much there is.

---

## 8. The written reason is Markdown, and Markdown is an attack surface

Evidence design §17, implemented:

- **Raw HTML is dropped, not escaped.** `skipHtml`, and no `rehype-raw` anywhere in the pipeline.
- **Link schemes** are react-markdown's own allowlist — `http`, `https`, `mailto` — so
  `javascript:` and `data:` cannot reach an `href`. Links open in a new tab with
  `rel="noopener noreferrer"`.
- **Images resolve only to evidence attached to the same case file**, through an internal scheme:
  `![what it shows](evidence:<sha256>)`. Anything else is shown as its alt text and never fetched.

That last rule is the one worth spelling out. An external image in a case file would fire whenever
any staff member opened it, reporting their IP address and the time they read it — a surveillance
channel pointed at the moderation team, created by a feature nobody asked for. The fix is to never
resolve an external image URL at all.

### 8.1 How this is checked

There is no JavaScript test runner in `Modbot.Web` — the suites are .NET — so this is a **manual
check**, recorded here so it is the same check every time:

> Write a case file whose written reason contains `<script>alert(1)</script>`, `<img
> src=x onerror=alert(1)>`, `[click](javascript:alert(1))` and
> `![pixel](https://example.com/a.png)`. Open the case file. Expected: the script tags appear
> nowhere in the DOM, the link renders without an `href`, the external image renders as
> `[image: pixel]` and no request is made for it (check the network tab), and an
> `![shot](evidence:<hash>)` pointing at a file attached to that case file renders inline.

The server side of it is tested: `CaseFilesTests` stores a written reason containing a `<script>`
tag and asserts it round-trips **unchanged**, because sanitising on the way in would quietly edit a
moderator's words. Rendering is where the safety belongs.

---

## 9. What was deliberately not built

- **No case file on kicks or warns.** Foundation §5.8.1: friction scales with reversibility, and a
  required form on a high-volume reversible action produces moderators who stop using Modbot, or
  who type "troll" five hundred times. The one-tap classification on those actions belongs with the
  actions themselves, which Modbot does not yet perform.
- **No blocking a ban on its case file.** Modbot does not perform bans yet; when it does, the report
  is required *before submitting*, which is a different mechanism from writing one up afterwards.
  Both will exist: this one has to, because every ban already in the audit log happened in VRChat's
  own UI and has no report at all.
- **No profile image capture** (§4.4).
- **No federation.** M8 §5.1 is unchanged: a federated signal carries an assertion, never a file.
- **No case file export.** Evidence design §20.12 leaves the shape of a dispute export open, and it
  should be designed before the first group needs one rather than during.

---

## 10. Open questions

1. **Should a lifted ban's case file be marked somehow?** Today the unwritten list shows that a ban
   was later lifted and still asks for the write-up, on the reasoning that a ban somebody reversed
   is *more* worth an account, not less. Nobody has disagreed yet.
2. **Should the reason list be per-action?** The same list serves bans today. Kicks and warns may
   want a shorter one, or the same one with different defaults, and that is a decision for when
   those actions exist.
3. **How long should the unwritten window be by default?** 30 days, matching the repeat-offender
   window. A group that bans rarely may want longer; the endpoint takes `days`, and nothing yet
   remembers a preference.
4. **What happens to a case file when its ban is lifted and the person rejoins?** Nothing today. It
   stays in their history, which seems right, but "spent" convictions are a real moderation concept
   and nobody has asked for one.
