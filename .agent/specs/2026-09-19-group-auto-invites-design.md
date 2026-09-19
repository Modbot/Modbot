# Modbot — Auto-invites (Settings → Auto-invites)

- **Date:** 2026-09-19
- **Status:** Built with this document
- **Covers:** inviting somebody to the group once they have spent long enough in one of its
  instances and pass the group's rules (§2–§4); where the rules come from (§3); the two hard
  checks that are not rules (§4); pacing and which bucket (§5); how the same person is not invited
  twice (§6); what a restart does (§7); what thin coverage does (§8); the permission, the API and
  the screen (§9–§10); what was decided against (§11)
- **Depends on:** foundation §4.1 (the gate), §4.1.1 (`...WithHttpInfoAsync`), §4.3.1 (cold stop,
  never retry a 429), §4.3.4 (ask about a rate limit first), §4.3.5 (the two backstop buckets);
  giveaways design §2 (the rule tree) and §2.6 (the rule tree is deliberately not about giveaways);
  the live instance rule in `Modbot.Core.Live.InstanceWatching`
- **Related:** join requests design (the other way somebody gets into the group); AutoMod design §5
  (an automatic actor that writes its own facts); VRChat proxy and moderator buckets design §2.2
  (why `interactive` exists and what may draw from it)

---

## 1. What this adds

A group can ask Modbot to invite people who are already spending time in its instances. Somebody
stands in a group instance, stays a while, passes the rules the group set, and Modbot sends them a
group invite. Nobody presses anything.

**It is off by default**, and a deployment that upgrades into it stays off until somebody turns it
on. A feature that starts inviting strangers on an upgrade would be the worst kind of surprise:
the invites are sent in the group's name, to named people, and they cannot be taken back.

---

## 2. What has to be true before an invite is sent

All of these, in this order:

1. **The switch is on.** `settings.group_auto_invite_enabled`, off by default. While off, nothing
   is read and nothing is queried.
2. **A companion is reporting from the instance right now** (§8). Modbot only knows who is in an
   instance while somebody's client is telling it.
3. **They have been in the instance for at least the configured number of minutes**, which is
   never fewer than five (§4.1).
4. **They are not in the group already**, and they are not banned, and they have not been kicked
   or asked to leave (§4.2).
5. **They have not been invited recently** (§6).
6. **They pass the group's rules** (§3).
7. **The pacing allows it**: at most one invite every thirty seconds across the whole deployment
   (§5).

Only then does `POST /groups/{groupId}/invites` go out, and only then is a fact written.

---

## 3. The rules — reused from giveaways, not reinvented

**The rules are a `GiveawayRule` tree, read and written by `GiveawayRules`, and checked by
`GiveawayRuleChecker`.** They are stored as JSON in `settings.group_auto_invite_rules`, the same
shape the giveaway column holds, and the settings screen builds them with the same `RuleBuilder`
component the giveaway form uses.

That is deliberate reuse, and the giveaways design asked for it in advance: its §2.6 says the rule
model "is deliberately not about giveaways — it asks who a person is and what they have done", and
that the next feature wanting those questions should take this tree rather than "grow a second
answer to 'how many hours has this person spent here' that can disagree with the first". Auto-
invites is that next feature. A group that has written "in the group for 30 days, no bans or
kicks, VRChat account 90 days old" for a giveaway should not have to learn a second way to write
it here, and a moderator reading two screens should not have to work out whether the two mean the
same thing.

So the whole vocabulary is available: account age (`vrchatAccountDays`), hours in our instances,
hours in one single instance, seen in the last so many days, no bans kicks or flags (`noTrouble`),
holds a group role, holds a Discord role, Discord and VRChat accounts linked, in Discord for so
long — grouped with **all of**, **any of** and **none of** as deep as three.

### 3.1 Two words the vocabulary did not have

Two of the things this feature has to ask were not in the rule vocabulary, and both are questions a
giveaway would want too. They were added to the shared vocabulary rather than invented privately:

| Kind | Asks | Answered from |
|---|---|---|
| `trustRankAtLeast` | The VRChat trust rank is at least the one named | `vrchat_user.trust_rank` |
| `age18Plus` | Modbot has ever seen them as 18+ verified | `vrchat_user.is_18_plus_verified` |

`trustRankAtLeast` carries the rank in the rule's `id` as its enum name — `"TrustedUser"` — not as
a number. The ranks are stored as a smallint whose members must never be renumbered, but a rule
tree is JSON that people read, and `{"kind":"trustRankAtLeast","id":"TrustedUser"}` says what it
means where `{"amount":4}` does not. A rank name the build does not know is refused when the rules
are saved.

**Nuisance and VRChat Team are above the ladder, and "at least" does not reach them.** `TrustRank`
orders Nuisance (6) and VRChat Team (7) above Legend (5) because they override the ladder rather
than extend it, so a plain `rank >= threshold` would make "Trusted User or better" true for a
known troll. The check is therefore: the rank must be on the ladder (Visitor to Legend) and at or
above the one named. A person whose rank Modbot has never read at all fails, like every other rule
about data Modbot does not have.

`age18Plus` reads the sticky flag (`Is18PlusVerified`), not VRChat's current
`ageVerificationStatus`. VRChat lets somebody hide their verification again after showing it, and
the user profile sync design §4 already decided that once observed it stays observed. An invite
rule that flickered with the profile field would invite the same person on Tuesday and refuse them
on Wednesday for a thing that did not change.

### 3.2 How one person is checked

`GiveawayRuleChecker.CheckVRChatUserAsync(rule, vrchatUserId, ct)` builds one `GiveawayCandidate`
for one VRChat account — its group membership and roles, its linked Discord account and that
account's roles, the stored profile, the ban row — and runs the same `Check` the giveaway preview
runs, with the same `GiveawayMeasures`. It does not go through `CandidatesAsync`, which loads every
member of both sides: that is the right shape for "who would be in this draw" and the wrong shape
for "does this one person qualify", and the difference is a few targeted queries against a sweep of
the whole group.

### 3.3 Retention, and rules that cannot be answered

`GiveawayCoverage.WhyUnanswerable` applies unchanged. A group that prunes presence facts after
ninety days and writes a rule about all of recorded history gets no answer, and **no answer is not
a pass**: the person is skipped, the reason is logged once, and no invite goes out. The rule is
also refused when it is saved, so the usual case is that this never fires.

---

## 4. The two things that are not rules

Three of the things §2 lists are checked in code, outside the rule tree, and that is on purpose.

### 4.1 The five-minute floor

**How long somebody must have been in the instance is a plain setting, not a rule.**
`settings.group_auto_invite_minutes_in_instance`, an integer, default 5, **and never less than 5**.
The API clamps it on write and refuses a smaller number with a sentence; the number box on the
screen has `min={5}`; the checker clamps it again on read, so a row edited in the database by hand
still behaves.

It is not a rule because the rule tree has **none of**. A rule tree that could express "has been
here five minutes" could also express "none of: has been here five minutes", which is the exact
opposite of the floor the user asked for and would be one wrong click away on a screen built for
combining rules freely. A floor that a rule builder can invert is not a floor. So the minutes live
in a column with a `Math.Max(5, …)` on every path that reads or writes them.

The same argument applies to the two checks below, and they are settled the same way.

### 4.2 Already in the group, banned, or shown the door

Checked in code, before the rules and before any VRChat call:

- **In the group** — a `group_member` row with no `left_at`. Checked before sending, never after:
  an invite to somebody who is already a member is a pointless request against a rate limit that is
  one request per thirty seconds, and VRChat has already been asked plenty.
- **Banned** — a `group_ban` row with no `lifted_at`.
- **Kicked, removed, or a ban that has since been lifted** — any `vrchat.group.member.kick`,
  `vrchat.group.member.ban` or Modbot-side kick or ban fact about them, ever. Somebody the group
  has thrown out is not somebody the group re-invites automatically, and an unban is a decision to
  let them come back rather than a decision to go and fetch them.
- **Modbot's own account** — the account the gate signs in as is never a subject of anything
  Modbot does to people, as `AutoModVRChatActions` already refuses.

None of these are configurable. A group that wants a banned person back has a person who can press
a button.

`noTrouble` in the rule tree is the *configurable* half of "has been warned" — flags, warnings and
the rest, with a window the group chooses. The hard half above is the part that is never a matter
of taste.

---

## 5. Pacing: one invite every thirty seconds, on `groups.invites`

### 5.1 The bucket

`groups.invites` already existed as an endpoint class and nothing had ever used it. It is now the
class every group invite goes out on, **retuned from one request per 3.5 seconds to one request per
thirty seconds**, which is the maintainer's answer to foundation §4.3.4's standing question for this
endpoint. The old figure was the conservative-neighbour guess a never-used class was given; this one
was asked for.

It keeps the **`global` backstop**, and that is the decision worth recording. An invite is neither
of the two things the buckets were split for:

- It is **not** what a moderator pressed. Nobody is watching a spinner. Putting it on `interactive`
  would let a loop that runs forever draw from the 0.575 req/s the ceiling reserves for the one
  action that must always work, and a group whose instances are busy would have Modbot's own
  invites competing with its moderators' bans. That is precisely the contention the VRChat proxy
  and moderator buckets design §2 was written to end, and it would be reintroducing it with the
  roles swapped.
- It is **not** a sweep either, but it is timer-driven work nobody waits on, which is what `global`
  paces. A request that finds `global` empty sleeps and re-checks, and an invite sleeping for a
  second is nothing.

It is **not** added to `RateLimitOptions.Scheduled`. That list is exactly the rows foundation §4.2
sums to 1.425 req/s, the sum the settings screen shows against the ceiling, and
`BudgetCoverageTests` holds the sum and the `interactive` cap in agreement. Adding 0.033 req/s to
it would shrink the room reserved for moderators to pay for a bucket that spends one token every
thirty seconds. The `global` bucket's own 2 req/s cap still bounds everything that draws from it,
including this.

A 429 here is never retried (§4.3.1). The class cold stops, no invite goes out while it is stopped,
and the loop simply finds the gate declining on its next pass.

### 5.2 The thirty seconds is the sender's, not the caller's

`GroupInvites.SendAsync` reads the most recent `invited_at` in `group_auto_invite` and refuses with
`TooSoon` if it is less than `GroupInvites.NoFasterThan` (thirty seconds) ago. It does that before
touching the gate.

Two reasons for belt as well as braces:

- **The limiter is in-process and the record is in the database.** The token bucket is persisted,
  but `RateLimitOptions.StateFlushInterval` is thirty seconds, so a crash can hand back up to a
  bucket's worth of allowance. The stored timestamp cannot: it is written before the request goes
  out, and a restarted process reads the same row.
- **The caller must not have to remember.** The user's limit is a property of what it means to send
  a group invite, so it is enforced where invites are sent. A second caller added later — a
  moderator's "invite this person" button, say — gets the pacing without knowing about it.

The loop that decides who to invite also runs every thirty seconds and sends **at most one invite
per pass**, so in the ordinary case the bucket is never even asked twice.

---

## 6. Not inviting the same person twice

One row per person in **`group_auto_invite`**, keyed on the VRChat user id:

| Column | Holds |
|---|---|
| `user_id` | The person. Opaque text, never parsed (foundation §3.1.1) |
| `first_invited_at` | The first time Modbot invited them |
| `invited_at` | The most recent time |
| `attempts` | How many invites have been sent to them |
| `instance_id` | The instance they were in when the last one went out |
| `worked` | Whether VRChat accepted |
| `problem` | What VRChat said, when it did not |

**The row is written before the request goes out**, and updated with the answer afterwards. That
order is chosen on purpose: if the process dies between the two, the invite is remembered as having
happened when it may not have. The alternative loses the record of an invite that did go out, and
the person is invited again. Of the two, a missed invite is a person who never hears from the group
and a repeated one is a person the group pesters; the user asked for the second not to happen, so
the write comes first.

A failed attempt counts as an attempt. Otherwise a person VRChat keeps refusing — blocked, deleted,
whatever the reason is — would be retried every thirty seconds forever, which is a hot loop against
a rate limit.

**`settings.group_auto_invite_again_after_days`**, default 30, minimum 1: how long before the same
person may be invited again. Somebody who declines, or who leaves the instance and comes back an
hour later, is not asked again that evening. A person who joined the group in the meantime is
already excluded by §4.2 and never reaches this check.

---

## 7. What a restart does

**Nothing is kept in memory, so there is nothing to lose.** There is no queue: each pass works out
from the database who is in the group's instances right now, in one query, and picks the person who
has been there longest. A restart mid-pass loses the pass, and the next one thirty seconds later
reaches the same answer.

The two things that *must* survive a restart both do, because both are rows:

- **Who has been invited** — the `group_auto_invite` table.
- **When the last invite went out** — the newest `invited_at` in it, which is what the thirty-second
  pacing is measured from (§5.2).

An in-memory queue was considered and rejected for exactly this reason: it would have had to be
rebuilt on start-up from the same query the pass already makes, which makes it a cache of a cheap
query that can be wrong.

---

## 8. Thin coverage: no companion, no invite

**Modbot only knows how long somebody has been in an instance while a moderator's client is
reporting from it.** `InstanceWatching` already says so and already enforces it: its `Here` list is
empty whenever nobody is watching, and a roster from a finished watch is `LastSeen`, which is a
different field with a different name for a reason.

Auto-invites read `InstancePeopleReader.ForInstancesAsync` for the group's open instances and use
only instances where `IsWatched` is true. Where it is false there is no list of people, so there is
nobody to consider and nothing to guess.

**A duration is never invented.** Each `PersonHere` carries `Since` and `SeenArriving`:

- `SeenArriving == true` — Modbot saw them arrive, and `now - Since` is how long they have been
  there.
- `SeenArriving == false` — they were already there when the watch began. Their real arrival is
  some earlier moment nobody saw, so `now - Since` is a **lower bound** on their stay, not an
  estimate of it. Using a lower bound is safe in the only direction that matters: if the bound
  already passes five minutes then the real stay certainly does. Somebody who was already present
  simply waits the full five minutes from the moment watching started before they can be invited.

That is the whole coverage rule. There is no fallback to the instance's head count, no inference
from the group's instance list, and no "they were seen an hour ago so they are probably still
here".

---

## 9. The permission, the switch and the API

**`ManageAutoInvites`** (bit 37, "Set up auto-invites"). Its own flag rather than part of
`ManageSettings`, for the reason `AnswerJoinRequests` is its own flag rather than part of
`ManageSettings`: everything else under settings changes what Modbot does to its own data, and this
decides who ends up inside the group. It is also strictly larger than answering one join request,
because it answers all of them in advance and without anybody looking. Not on the built-in roles;
Administrator holds it.

`GET`/`PUT /api/settings/auto-invites` (`ManageAutoInvites`). The view carries `enabled`,
`minutesInInstance`, `inviteAgainAfterDays`, `rules`, the `ruleKinds` the builder offers, the group
and Discord roles a rule can name, the retention windows the builder warns about, and
`invitesSent` — how many invites have gone out. The update takes `enabled`, `minutesInInstance`,
`inviteAgainAfterDays` and `rules`; it refuses fewer than five minutes and refuses a rule tree
`GiveawayRules.Read` will not accept, each with the sentence a person should see. A change writes a
`modbot.settings.change` fact naming the setting, as every settings screen does.

The roles are on the response so the rule builder works on this tab without the caller also holding
`RunGiveaways` — the same lists the giveaway builder endpoint serves, read from the same two places.

### 9.1 The facts

| Type | Written when |
|---|---|
| `modbot.group.auto-invite` | VRChat accepted the invite |
| `modbot.group.auto-invite.failed` | VRChat refused it, or the gate declined to send |

Subject: the person, on the VRChat platform, so it lands in their history beside everything else
about them. **No actor** — Modbot did this, as `AutoModVRChatActions` and the calendar's opener do.
Source `FactSource.Modbot`. The payload carries the instance, how many minutes they had been there,
and — on a failure — what went wrong. Moderation retention, which is what an unprefixed
`modbot.group.*` type falls to, and the right answer: "Modbot invited this person" is history a
group should still be able to read in a year.

VRChat's own audit log records the invite too, as `vrchat.group.invite.create`, and the member and
ban sweeps confirm whatever comes of it. These facts are the other half of the same pair the
moderation facts already form (foundation §5.9.1): VRChat's log says an invite was created by
Modbot's account and cannot say why, and this one says why.

---

## 10. The screen

**Settings → Auto-invites**, its own tab because it has its own permission and a tab a person may
not open is not drawn at all.

One card, *Auto-invites*: the switch, **Minutes in the instance** (`min=5`), **Invite again after**
(days), the rule builder, and Save. Labels and units, no paragraphs — the reasoning is here.

---

## 11. What was decided against

- **A second rule vocabulary.** See §3. The one that exists was built to be shared and says so.
- **Putting the five-minute floor in the rule tree.** See §4.1: a tree with **none of** in it can
  invert anything it can express.
- **Putting invites on the `interactive` backstop.** See §5.1. They are automatic; the reserved
  room is for the moderator who is waiting.
- **Counting `groups.invites` in the scheduled sum.** See §5.1. It would shrink the moderators'
  room to account for one request every thirty seconds.
- **An in-memory queue of people to invite.** See §7.
- **Re-inviting on failure.** See §6. A failure counts as an attempt.
- **Inviting somebody the group has kicked or banned before, ever.** See §4.2. Not configurable.
- **Inviting from the group's instance list alone, without a companion.** See §8. The head count
  says how many people are there, never who or since when.
- **Telling the person why they were invited, or telling them at all.** The invite is VRChat's own
  notification and Modbot adds nothing to it. There is no message to write and no place to put one.
- **A "who would be invited right now" preview.** The giveaway preview answers the same question
  for a saved rule tree and the machinery is there, but a preview of a live instance's people is a
  different screen from a settings card, and nobody asked for one.
- **More than one invite per pass.** The loop could send a burst up to the bucket's allowance. One
  per pass keeps the deployment-wide rate obvious from reading the loop rather than from reasoning
  about a token bucket.
