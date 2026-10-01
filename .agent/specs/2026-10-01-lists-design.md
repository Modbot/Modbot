# Modbot — Lists (saved segments)

- **Date:** 2026-10-01
- **Status:** Built
- **Covers:** Saved lists of people, the rules they added to the giveaway rule tree, using a list in a
  giveaway or auto-invites, who may see and change one, and the export
- **Depends on:** giveaways design (the rule tree, the checker, precision honesty, the retention
  refusal), auto-invites design, the repeat-offender counts (spec 5.8.4)
- **Narrows:** M7 §2.2 and §3. What M7 calls a segment is a **list** here, because "segment" is a word
  a volunteer moderator would have to have explained.

---

## 1. What this is

A list is a name and a rule tree: "in the group, and seen on at least four different days in the
last month". It is the definition, never a copy of who matched it once (M7 §2.2), so opening
**Regulars** next month gives next month's regulars.

What a group does with a list:

- **Look at it**: who is in it now, a page at a time, with a count.
- **Export it**: CSV or JSON, as a person's action, recorded as a fact.
- **Name it in a giveaway or auto-invites**: as one of their rules.

What a list never does is act by itself (M7 §7). Nothing here messages, invites, bans or gives a role
to anybody because they are in a list.

---

## 2. One rule tree, one checker

A list's rules are `GiveawayRule`, stored as the same JSON a giveaway stores, and answered by
`GiveawayRuleChecker`. Giveaways design §2.6 asked for exactly this: a second checker would be a
second answer to "how many hours has this person spent here", and the first time the two disagreed a
member would find it.

`GiveawayRuleChecker.PeopleAsync` is the list's face of the checker: the same candidates (group
members and server members, merged by link), the same measurements, the same `Check`, no
exclusions and no weighting. A test pins that a list and a giveaway preview with the same rules name
the same people.

**The `Giveaway*` names were not renamed.** Giveaways design §2.6 expected a rename when segments
arrived. It was left out on purpose: it touches some thirty files other work was changing at the same
time, it changes nothing anybody can see, and it can be done on its own later without changing a
byte of stored JSON.

---

## 3. The rules lists added

The predicate families M7 §2.1 lists that giveaways had no use for. Every one is offered to
giveaways and auto-invites too, since they share the builder.

| Rule | Asks | Read from | Exact? |
|---|---|---|---|
| `groupJoinedWithinDays` | Joined the group in the last N days ("new this month") | `group_member.joined_at` | exact |
| `groupJoinedBefore` | Joined the group before a day | `group_member.joined_at` | exact |
| `groupJoinedSince` | Joined the group on or after a day | `group_member.joined_at` | exact |
| `firstSeenWithinDays` | Modbot first saw either account in the last N days | `vrchat_user.first_seen_at`, `discord_member.first_seen_at` | exact |
| `daysSeen` | Seen on at least N different days, optionally in a window | presence facts | **polled** |
| `notSeenWithinDays` | Seen before, not in the last N days ("lapsed") | presence facts | **polled** |
| `moderationCount` | Banned / removed / instance-kicked / warned / turned down at least N times, optionally in a window | moderation facts | exact |
| `inList` | In a saved list | that list's rules (§4) | as its rules |

### 3.1 Days, not instants

The two join-date rules carry a `date` (`"2026-06-01"`), a new field on the rule. A day starts at
midnight UTC, as every daily total does. "Joined before June" is a question about a calendar; an
instant would make the builder ask for a time nobody has in mind.

### 3.2 Days seen is the weighting's own count

`daysSeen` reads the same measurement as the "days seen" weighting, narrowed to the rule's window. A
weight and a rule asking the same question must not answer it twice.

### 3.3 Lapsed asks about all of history

"Seen before" has no start. Where an operator has set a presence retention window, somebody last
seen past it reads as never seen, and would quietly drop out of "lapsed". So `notSeenWithinDays` is
refused whenever presence retention is set, by the same coverage check and in the same words as
every other refusal (giveaways design §6.2). Somebody never seen at all has not lapsed; they never
came, which is a different list.

### 3.4 Moderation counts agree with the profile

`moderationCount` names one of five kinds, each counted from the same fact types as the
repeat-offender numbers on a profile (`ActionsOnPeople`): `vrchat.group.member.ban`,
`.member.remove`, `.instance.kick`, `.instance.warn`, and `.request.reject` with `.request.block`. It
counts from the facts rather than from `modbot_repeat_offender`, because that table knows all time,
30 and 90 days and a rule may ask about any window. Moderation retention refuses it the way it
refuses `noTrouble`.

### 3.5 Not built: a streak

"Seen every day for a week" — an unbroken run — is not offered. A per-person presence daily total
(giveaways design §10, the presence daily-totals work) is the right place to count runs from, and
`daysSeen` in a window answers "regulars" well enough until it exists.

---

## 4. A list inside another rule tree

`inList` names a list by its id. Wherever a tree is asked, `SavedListRules.Expand` first puts each
named list's own rules in its place. The checker, the retention check and the measurements then
work on the tree that comes back and never see a list at all.

- Under **All of**, a list whose own root is **All of** is written flat: "all of: in Regulars, linked"
  becomes "all of: in the group, seen on 4 days, linked". The Discord card then prints the list's
  rules as separate lines, the way the organiser would have typed them.
- Under **Any of** or **None of** the list keeps its own group. "None of: in Regulars" must stay
  "not (a and b)".

### 4.1 No list inside a list

A list's own rules may not name a list. Expanding then takes one pass, and no list can name itself
however indirectly. A tree naming many large lists is still bounded: once written out it may hold at
most four times the usual sixty rules, and past that it is refused in words.

### 4.2 What sees the list, and what sees its rules

| Where | Shows |
|---|---|
| A giveaway's page, auto-invites' settings | "in the list “Regulars”" — the moderator can open the list |
| The giveaway's Discord card | the list's rules, written out — members cannot open lists, and the point of the card is that they can read the rules (giveaways design §7.1) |
| A draw | a copy of the rules with every list written out, kept on the draw |

The draw's copy is the important one. A draw is a fact with its parameters (giveaways design §5.3);
if it kept only "in Regulars", changing Regulars next week would change what an old draw says it was
drawn from. The stored copy can be deeper than a tree anybody may submit, so stored trees are read
without the size limits, which exist to stop a submitted tree from being a denial of service.

A list changing moves a giveaway's card through the card's fingerprint, like any other edit: the card
says what the list holds today.

### 4.3 A list that is gone

A deleted or unknown list is left as `inList`, and the checker lets nobody through it with the
reason "That list does not exist any more." A missing list must never quietly mean "everybody".
Saving a giveaway or auto-invites that names a missing list is refused, and deleting a list that a
giveaway still being run (draft, open or closed) or auto-invites names is refused too, naming what
uses it. A drawn or cancelled giveaway does not hold a list back: its draw has its own copy.

Lists are soft-deleted (`deleted_at`), so an old giveaway naming one can still say which.

---

## 5. Who is in it

`GET /api/lists/{id}/people` works the list out on every request. The answer carries the count over
everybody, how many people were looked at, the close calls, whether any figure came from presence
reports, and when it was counted; the page is a window on it. The page prints a polled count as
"about 312", the giveaway preview's pattern (M7 §2.3).

Each row of the Lists page asks for its own count with a page of one, so the page draws at once and a
slow list holds nothing else up. Nothing is cached: a list is small, the checker is bounded at 50,000
people and stops with a sentence past that, and a cache would be one more number that can be stale
without saying so (M7 §5 allows one only with a visible counted-at time; the answer carries one
anyway).

People are sorted by name, then by key so two people with one name always come back in the same
order.

---

## 6. Permissions

| Action | Needs |
|---|---|
| See the Lists page, a list's rules, who is in it; export | `ViewMembers` |
| The export's profile columns | `ViewProfile` as well |
| Make, change and delete; the builder and its preview | `ManageLists` (new, bit 44) |
| Change a list a giveaway still being run names | `RunGiveaways` as well |
| Change a list auto-invites name | `ManageAutoInvites` as well |

**Seeing a list needs only See members**, as the plan for this work set out: the people in a list are
group and server members, which See members already shows.

**The preview needs Manage lists.** A rule can ask about bans, flags, 18+ verification and presence,
which See members alone does not show. Whoever may make lists decides which questions the saved ones
ask; the preview belongs to the builder and is not a way round that. This leaves one thing worth
knowing, recorded as an open question (§9.1): a saved list's members are visible to See members
whatever its rules read.

**Changing a list something uses takes that thing's permission.** A list auto-invites names decides
who gets invited, so Manage lists alone would otherwise be a way to change auto-invites without Set
up auto-invites.

`ManageLists` is not in the built-in Moderator or Viewer roles; Administrator holds it.

---

## 7. Export, and the facts

`POST /api/lists/{id}/export` with `{ "format": "csv" | "json" }`.

- **A POST, not a link.** Nothing but a person pressing the button — no prefetch, no link preview —
  ever makes one.
- **Recorded before the file is handed over**, as `modbot.list.export`, with the list's name, the
  format, the number of people and the columns. Never the people: the record says a copy was taken,
  and a second copy inside the audit log would be the opposite of what it is for.
- **The columns depend on what the caller may see.** Membership columns for See members; trust rank,
  18+ verified, VRChat account date and first seen only with See profiles.
- **A cell a spreadsheet would run is made inert.** Names are chosen by their owners; a cell starting
  `=`, `+`, `-` or `@` gets a leading apostrophe.
- **The consequence is said where it belongs.** M7 §3 asks for a warning that an export leaves
  Modbot's retention and purge. The page's rule is no explanatory text (`CLAUDE.md`), so the export
  dialog names what it will do — how many people, which columns — and the docs page carries the
  warning in full.

Facts: `modbot.list.create`, `.change` (before and after), `.delete`, `.export`. Subject is the list
on the Modbot platform; operational log; moderation retention (kept forever by default), because
"who took a copy of this list of people" has to be answerable long after the file went wherever it
went.

---

## 8. Not built

- **Announce.** M7 §3 calls for "a Discord message to linked members". The only way to message a
  list's members on Discord is a direct message to each, which giveaways design §9 rules out for the
  same reason here (a bot that DMs people looks like the scams that do), and a channel post cannot
  reach a list. VRChat group posts target roles, not people, and using them for lists would be a new
  use of a VRChat endpoint, which needs its rate limit asked about first (foundation §4.3.4). Left as
  a decision for the project.
- **Bulk targeting.** M7 §3 hands a list to M4's bulk actions. There are no bulk actions yet
  (`BulkAction` does nothing). When they exist, `GiveawayRuleChecker.PeopleAsync` is what they take:
  the same people the Lists page shows. M7 §3's safeguard — the preview list viewed in this session
  before execution is offered — belongs to that work.
- **A streak rule** (§3.5).
- **Sharing a list between deployments** (M7 §8.2).
- **Renaming the `Giveaway*` engine types** (§2).

---

## 9. Open questions

1. **Should seeing a list need See profiles as well as See members?** A list "banned twice" shows,
   to anybody with See members, who was banned twice — something they would otherwise need See the
   audit log to learn. Today the guard is that only Manage lists decides what lists exist.
2. **Announce** (§8): leave it out, or build one of a channel post naming the list, DMs, or VRChat
   group posts to a role.
