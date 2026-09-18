# Modbot — Giveaways

- **Date:** 2026-09-17
- **Status:** Built
- **Covers:** Planning a giveaway, the rule model, entering, drawing, the Discord post, the page
- **Depends on:** M0 (facts, daily totals), M3 (presence), M5 (Discord facts, voice and message
  daily totals), the calendar (the pattern for a Modbot record that posts to Discord)
- **Narrows:** M7 §4. M7 §2's segment builder and §3's bulk targeting are **not** built (§10).

---

## 1. What this is

A giveaway is a prize, a set of rules about who may enter, and a draw that picks winners from
whoever qualifies. Modbot runs it end to end: it decides who is in, posts it to Discord, takes
entries, draws, and shows the whole thing afterwards in a form anybody can check.

The interesting half is not the picking. It is that **weighting a giveaway by time spent in your
worlds rewards the people who actually show up**, and that is impossible without recorded presence
history. It is also the most legible payoff the fact log has: most members will never see a
profile, but they will notice whether the raffle is fair.

So the design spends most of its effort on two things: making the rules ask real questions of real
data, and making the result checkable by somebody who does not trust whoever ran it.

---

## 2. The rule model

### 2.1 One record, two jobs

A rule is one record. `kind` is either a combining word — `allOf`, `anyOf`, `noneOf` — in which
case `rules` holds the rules inside it, or a question, in which case `amount`, `withinDays` and
`id` say what is being asked.

```json
{ "kind": "allOf", "rules": [
  { "kind": "discordMemberDays", "amount": 30 },
  { "kind": "instanceHours", "amount": 10, "withinDays": 90 },
  { "kind": "anyOf", "rules": [
    { "kind": "groupRole", "id": "grol_…" },
    { "kind": "voiceHours", "amount": 20 }
  ]}
]}
```

One record type rather than two, because a tree of two is twice the JSON to read and twice the code
to walk, and every combining rule is a rule that happens to hold other rules.

**There is no "does not hold" flag.** `noneOf` around a rule says the same thing with one idea
instead of two, and it reads the same way on the card: *none of: holds the Regulars role*. Rules
nest three deep and a tree holds at most sixty rules; the builder offers one level and the limits
are there so a hand-written tree cannot be a denial of service.

### 2.2 The rules, and what each one reads

| Rule | Asks | Read from | Exact? |
|---|---|---|---|
| `discordMemberDays` | In the Discord server for at least N days | `discord_member.joined_at` | exact |
| `groupMemberDays` | In the VRChat group for at least N days | `group_member.joined_at` (VRChat's own) | exact |
| `inGroup` | A group member right now | `group_member.left_at IS NULL` | exact |
| `instanceHours` | At least N hours in our instances, all of them added up | presence facts | **polled** |
| `oneInstanceHours` | At least N hours in **one single** instance | presence facts | **polled** |
| `voiceHours` | At least N hours in Discord voice | `discord.member.voice-minutes` daily totals | exact |
| `messages` | At least N Discord messages sent | `discord.member.messages` daily totals | exact |
| `seenWithinDays` | Seen in one of our instances in the last N days | presence facts | **polled** |
| `linkedAccounts` | Has a Discord and a VRChat account linked | `discord_account_link` | exact |
| `groupRole` | Holds a given group role | `group_member.roles` | exact |
| `discordRole` | Holds a given Discord role | `discord_member.roles` | exact |
| `noTrouble` | No bans, kicks or standing flags | moderation facts + `moderation_flag` | exact |
| `vrchatAccountDays` | VRChat account at least N days old | `vrchat_user.date_joined` | exact |

`instanceHours`, `oneInstanceHours`, `voiceHours`, `messages` and `noTrouble` can be narrowed to the
last N days; left alone they count all of recorded history. `seenWithinDays` carries its window in
its number, because "seen in the last thirty days" is one idea and not two.

**Total hours and hours in one instance are different questions and are offered separately.** "Has
spent ten hours here" and "has spent ten hours in one sitting" describe different people, and a
giveaway for regulars usually means the first while a giveaway for an event usually means the
second.

### 2.3 Daily totals wherever a daily total answers it

M7 §5 is explicit and it decides the shape of every read here. Voice hours and message counts are
sums of daily total rows that already exist per member per day, so they are two indexed reads over
a small table rather than a walk of the fact log. Only the presence numbers are counted from facts,
because no per-person presence daily total exists yet (§10).

The session arithmetic is the same arithmetic `PresenceCounts` uses — a person's presence in an
instance is the last thing said about them there, and a session nobody saw the end of closes at the
last report from that instance. **The two have to agree.** A member's profile and a giveaway's
rules answering "how long has this person been here" differently would be the worst kind of bug to
find out about from a member.

### 2.4 Bounded, cancellable, counted separately

- The candidate list is capped at 50,000 people. Past that the run stops and says so, rather than
  holding a connection until something times out.
- Every query takes the cancellation token, so closing the page stops the work.
- A preview returns the count over everybody and a page of people, separately — the rule builder
  can say "312 match" without the page holding 312 rows.
- One query per distinct measurement, not one per person. Four rules over ten thousand candidates
  is four statements.

### 2.5 Who is even considered

For an automatic giveaway: every current group member and every current Discord member, merged by
whatever links exist. For a giveaway people enter by reacting: the people who reacted, and nobody
else.

A person is a VRChat account, a Discord account, or the two linked. The snapshot names them
`vrchat:<id>` when Modbot knows a VRChat id and `discord:<id>` otherwise, so the same person is one
entrant however many rules ask about which side. Presence is recorded about a VRChat account and
voice and messages about a Discord one; somebody with only the wrong half reads as nought, which is
the honest answer and is why `linkedAccounts` exists as a rule.

### 2.6 How a segment feature would reuse this

`GiveawayRule`, `GiveawayRules` and `GiveawayRuleChecker` know nothing about prizes or draws. They
answer *who matches these rules*, which is exactly the question M7 §2's segment builder asks.

When segments are built they should take this tree and this checker, and grow:

- a saved, named definition (M7 §2.2) — a row holding the same JSON, with a name;
- the extra predicate families M7 §2.1 lists that giveaways had no use for — active streak, lapsed,
  new-this-month, first seen;
- the count-and-page shape the preview already has, as the segment's own result page.

What must **not** happen is a second answer to "how many hours has this person spent here". The
names are `Giveaway*` today because that is what exists; renaming them when segments arrive is a
rename, and a second checker would be a second set of numbers that can disagree with the first.

---

## 3. The giveaway itself

### 3.1 The record

A giveaway has a name, an optional prize description, an opens-at and a closes-at, a draw time or a
manual draw, how many winners, how people enter, its rules, its exclusions, its weighting and its
cap, and where it is posted. Its status is one of:

| Status | Meaning |
|---|---|
| `draft` | Saved, posted nowhere, nobody can enter. |
| `open` | Published and running. |
| `closed` | No more entries, not yet drawn. |
| `drawn` | Drawn at least once. |
| `cancelled` | Called off. |

`opensAt` gates whether entries count; the status says whether the giveaway is being run at all.
The two are separate because a giveaway posted on Monday for entries that open on Friday is a thing
people do, and adding a sixth status for it would mean every screen had one more word to explain.

### 3.2 What can be changed, and when

A cancelled giveaway cannot be changed. **A drawn giveaway cannot be changed either**: the draw
copied its parameters, so editing them would not alter the result, but a page showing today's rules
beside last week's winners reads as though the two go together. A published giveaway cannot go back
to being a draft.

### 3.3 Exclusions are parameters

Staff, people who have won before, banned members, and people named one at a time. Each is recorded
on the giveaway, copied onto every draw, and shown on the card and the page.

**An excluded person is in the snapshot with their reason beside them.** "Staff" and "does not pass
the rules" are different answers to *why am I not in this*, and the first one is the answer a person
deserves. A filter applied quietly before the list was written down would hide exactly the decisions
that need to be visible.

---

## 4. Entering

### 4.1 Two ways

**Automatic.** Everybody who matches the rules is an entrant. Nobody does anything.

**React on Discord.** Modbot posts the giveaway and puts the chosen emoji on it; a person enters by
reacting with that emoji. Taking the reaction off withdraws the entry. A giveaway entered this way
must be posted to a channel — without a post there is nothing to react to.

Reactions needed gateway support that did not exist: reaction added and removed are new on
`IDiscordGateway`, along with the call that puts the bot's own reaction on the post. The intent they
need is not a privileged one, so nothing has to be switched on in the Developer Portal.

### 4.2 The rules are checked twice

**When the reaction arrives and again at the draw.** Neither reading on its own is honest:

- checking only on entry lets somebody who has since been banned win;
- checking only at the draw means a person who does not qualify hears nothing until it is too late
  to do anything about it.

So the answer on entry is recorded on the entry row, and the draw asks again. The draw's answer is
the one that decides.

### 4.3 A person who no longer qualifies is shown as such

They stay in the snapshot with `keptOut: "rules"` and the rule they failed in plain words. Dropping
them would leave somebody watching a giveaway they were quietly taken out of, which is the failure
the snapshot exists to prevent.

### 4.4 Anybody may react

Modbot does not have to know who somebody is. A reaction from a person with no member row is an
entry; whether they qualify is up to the rules, and a rule needing VRChat data simply fails for
them, visibly. Reactions on anything but a giveaway post, with any other emoji, or after closing,
are somebody using Discord: one indexed lookup and nothing written.

---

## 5. Fairness

This is the part that earns the feature. A draw nobody can verify is a draw nobody trusts, and a
rigged-looking giveaway does more community damage than no giveaway.

### 5.1 The entrant list is frozen and kept

At the draw, the whole list is written to `giveaway_entrant`: position, who, weight, the number the
weight was counted from, and the reason anybody was out. It is never changed.

Without it the seed proves nothing, because nobody can check a pick from a hat whose contents were
not written down.

### 5.2 The seed is promised before and revealed after

A giveaway carries, from the moment it is created, a SHA-256 of a seed and the seed itself sealed
with the deployment's protector. The hash is shown on the page and is the promise. The draw unseals
the seed, writes it into the draw **in the open**, and **immediately makes a fresh promise for the
next draw** — so the promise for a re-draw is standing before anybody has decided to make one,
which is the only order in which a promise means anything.

The API returns whether the seed keeps its promise, checked server side, so nobody has to do the
hash themselves to notice if it did not.

### 5.3 A draw is a fact and is never re-run in place

Drawing again writes a second draw with the next number, its own seed, its own snapshot and its own
winners, and leaves the first exactly where it was. Somebody who did not like a result cannot make
it go away; they can only draw again where everyone can see that they did. Both draws are on the
page and both are facts.

### 5.4 The pick

Weights are **whole numbers**, and the pick is integer arithmetic:

```
for round i in 0 … winners-1:
    total  = sum of the weights still in the hat
    target = HMAC-SHA256(seed, "round:i")[0..8] as a big-endian u64, modulo total
    walk the list in position order, adding weights, until the running total passes target
    that person wins; take them out of the hat
```

Nothing anywhere is a floating-point number. A draw that could come out differently depending on
how somebody's language rounds a double is not reproducible, and a fairness mechanism that only
works on one machine is not one. Every language has HMAC-SHA256, integer addition and a remainder,
which is the point: the recipe is short enough to follow by hand.

The modulo is very slightly biased towards the front of the list, by about one part in 2^64 divided
by the total weight. Rejection sampling would remove it and would make the recipe something people
cannot follow, which costs more than the bias does.

### 5.5 Weighting, and the cap

Uniform, or by hours in our instances, hours in Discord voice, Discord messages, or days seen —
every one of them a number the rules also expose, so a weight is never counted from something
nobody could have asked about.

A weight is the measured number **rounded to a whole number, never below one, never above the cap**.

- The floor of one keeps a qualifying entrant an entrant. Somebody who passed every rule and happens
  to have nought hours recorded has entered, and a weight of zero would be a silent exclusion of
  exactly the kind §3.3 says must be a recorded parameter instead.
- The cap is what stops one very dedicated person holding most of the probability. It is refused on
  a uniform giveaway rather than ignored, because a control that quietly does nothing is a question
  somebody asks later.

---

## 6. Honesty

### 6.1 Presence figures are approximate, and say so

Presence is sampled by whichever moderator's companion happened to be in the instance. A figure
counted from it is close, not measured.

- Every rule answer, every entrant row and every draw carries `fromPolledData`.
- A measurement within **a tenth of the threshold, never less than half an hour** is a **close
  call**, counted on the draw and marked on the row.
- The page prints a polled figure as "about 12", never "12.4". The preview says "about 312 of
  4,081". A polled number printed to a decimal place would be inventing precision nobody measured
  (M7 §2.3).

### 6.2 A rule past the surviving facts is refused

M7 §6 calls this the most likely quiet correctness failure, and it is.

Nothing is pruned by default, so on a deployment nobody has configured every rule is answerable and
none of this fires. Where an operator **has** set a window:

| Rule reads | Limited by | Refused when |
|---|---|---|
| presence facts | presence retention | the window is longer than retention, or the rule asks about all time |
| moderation facts | moderation retention | the same |
| daily totals | nothing | never — daily totals are not aged out, whatever retention says |

The refusal names the rule in its own words and says how far the facts reach:

> Modbot cannot answer "10 hours or more in our instances in the last 365 days". Presence history is
> kept for 90 days, and the rule asks about 365.

A weighting counted from presence is refused the same way. **No partial answer is ever returned**:
the preview and the draw both come back with the refusal in place of a number.

An all-time presence rule on a deployment with presence retention set is refused too. That is the
honest reading — all time reaches past the surviving facts — and the operator can narrow the window.

### 6.3 Purged people never reappear

A purge (foundation §5.5) deletes the person's facts, and now also:

- **deletes their standing giveaway entries outright**, so they cannot be drawn again; and
- **blanks the name, the ids and the key on every frozen entrant row they are in**, keeping the
  position and the weight, and marking the row as erased.

The two halves pull opposite ways and both matter. A snapshot is what makes a past draw checkable
(§5.1), so deleting the row would make an old result unverifiable for everybody else; a purge has to
actually erase the person, so keeping their name would make the erasure a claim rather than a fact.
What is kept is the arithmetic, which is about nobody once the name is gone: anyone can still
reproduce the draw from the snapshot and the seed, and the row says only "somebody, with this
weight, in this position".

---

## 7. Discord

### 7.1 The card

Posted to a chosen channel in the style `CalendarCard` uses: the name, the prize, when it closes as
a Discord timestamp, how to enter, how many winners, the weighting and its cap when there is one,
**the rules in plain words**, who is not eligible, the entry count, and the winners once it is
drawn.

The rules are on the card on purpose. A giveaway whose rules live on a page members cannot open is a
giveaway they have to take on trust, and the whole point of §5 is that they should not have to.

#### It is built out of the shared card helpers

The card was written at the same time as the embed redesign and against the style that redesign
replaced, so it was brought onto the helpers afterwards (Discord embeds design §5). What that
changed:

- **A winner is a linked name**, through `CardLink` — their VRChat profile when the entrant row
  carries a VRChat id, their Discord profile in Modbot when it carries only a Discord id. This is
  the whole point of the fix: a card announcing that somebody won was the last card in Modbot where
  the name was flat text. With no public address the same helper gives the name on its own, because
  a link built from anything but the address a human typed would not work (accounts and access
  §4.2).
- **Somebody erased at their own request stays erased** (§6.3). Their row has no name and no ids
  left, so there is nothing to link to and the words that replaced them are all the card shows.
- **The winners list is built against Discord's 1024-character field limit rather than cut to it.**
  A linked name is several times the length of a plain one, so twenty of them no longer fit where
  twenty plain ones did; the card lists as many as fit and ends with "and N more". Cutting instead
  would have landed inside a link and put a raw address on the card.
- **The colours are the shared palette's** (`CardColour`), not four constants of its own.
- **The group's name sits above the giveaway's and the footer carries Modbot's mark**, both from
  `CardStyle` — the same line an instance card and a calendar post already had, so a server
  watching more than one Modbot can tell its channels apart.
- **The group's icon is the one picture the card carries**, sent with the first post and referred
  to by name on every rewrite (Discord embeds design §3.3, §3.6). There is no thumbnail and no
  large picture: Modbot holds no picture of a prize, and this card is about neither a person nor a
  place.
- **Each piece of text is escaped for the slot it lands in.** The title is printed literally by
  Discord, so it is stripped rather than escaped — a giveaway called `*hats*` reads as `*hats*` on
  its own card. A rule line is escaped as a name, because a rule about a role prints a Discord role
  name and those are chosen by whoever made the role. The prize keeps the organiser's formatting,
  the way a calendar event's description does.

### 7.2 Keeping it in line

The publisher writes the post only when what it should say differs from the fingerprint last sent —
the calendar's path exactly, and for the same reason: an edit, the giveaway closing, the entry count
moving and the draw all arrive by one route, and a quiet pass costs no Discord calls. Five calls a
pass, one pass every twenty seconds. A refusal that will not fix itself is not repeated until the
giveaway changes.

Closing and drawing on time are **not** Discord's business and live in `Modbot.Analytics`: a
giveaway with no channel still closes, and the announcement is what the Discord side does about a
draw that has already happened. A draw that cannot be made — nobody in it, or a rule no longer
answerable — clears its draw time and records why, rather than being retried every twenty seconds
against a reason that will not fix itself.

### 7.3 The announcement

One line in the channel when a draw is made, naming the winners and linking the giveaway, once per
draw number — so a re-draw is announced and the same draw never is twice.

**Nobody is pinged, ever.** A giveaway with four hundred entrants would otherwise be four hundred
notifications for one result. Mentions are off on every message the bot sends, so no card can ping
anybody however it is written, and a winner's name is escaped like any other display name.

**The names in the announcement stay plain, unlike the ones on the card above it.** This is a line
of message text rather than an embed, and the embed rules are written for embeds (Discord embeds
design, *Covers*). The linked names belong on the card the announcement points at, which is a click
away.

---

## 8. Facts, permissions and the API

### 8.1 Facts

Every consequential action: `modbot.giveaway.create`, `.change`, `.open`, `.close`, `.enter`,
`.withdraw`, `.draw`, `.cancel`, `.delete`, `.winner.announce`, `.publish.fail`.

The subject is the giveaway, **except for entering and withdrawing, whose subject is the person on
Discord** — an entry is a thing somebody did, it belongs in their history, and a purge can only find
it by subject.

They take the moderation retention class (kept forever by default). A draw is a decision about who
got something, and "who entered" is what makes a disputed result answerable months later.

The draw's fact carries the seed, the promise, the entrant counts, the weighting and the winners:
a record of who won is a claim; a record of who won and what it was drawn from is a claim anybody
can test.

### 8.2 Permissions

`ViewGiveaways` (bit 27) and `RunGiveaways` (bit 28), following the calendar's pair.

Seeing an entrant list is seeing a list of named people with a number beside each saying how much
time they spend here, which is narrower than "see members" or "see analytics". Running a giveaway
posts in the group's name and decides who gets something. Neither is in the built-in Moderator or
Viewer roles; Administrator holds both.

### 8.3 The API

`/api/giveaways` — list, one, create, change, open, close, draw, cancel, delete; `POST
/api/giveaways/preview` for the rule builder; `GET /api/giveaways/builder` for the rule kinds,
weightings and roles; `GET /api/giveaways/{id}/draws/{drawId}/entrants` for the frozen list, a page
at a time. All in the OpenAPI document, with a docs page at `/moderation/giveaways`.

---

## 9. Non-goals

Inherited from M7 §7 and unchanged:

- **Prize fulfilment, delivery or tracking.** Modbot picks a winner; the group handles the rest.
- **Automated recurring giveaways** driven by a segment.
- **Cross-group giveaways.** Federation is M8.

Added here:

- **No direct messages to winners.** The announcement is public and the page names them. A bot that
  DMs people about prizes is indistinguishable from the scams that use exactly that shape.
- **No entry outside Discord.** A web entry form would need member authentication that does not
  exist, and a VRChat-side entry would need something to click in-world.

---

## 10. Left for later

- **A per-person presence daily total.** It would make `instanceHours` a daily total sum rather than
  a fact walk — faster, and answerable past the presence retention window, which would lift §6.2's
  refusal for the rule people are most likely to want. It is the single largest improvement
  available here and it is a daily totals job change, not a giveaways one.
- **The segment builder and saved segments** (M7 §2). §2.6 is the reuse plan.
- **Bulk targeting** (M7 §3) and its safeguards.
- **Exporting an entrant list.** M7 §3's export warning applies and would have to come with it.
- **Predicates on derived series** — active streak, lapsed, new this month — which M7 §2.1 lists and
  which a giveaway has not needed yet.
- **Choosing the emoji from the server's own list.** It is typed today, and Discord decides what it
  accepts; a picker needs the emoji list, which the server index does not hold.

---

## 11. Open questions

1. **Should an entrant see their own weight?** The page shows every weight to anybody with
   `ViewGiveaways`, and the card shows none. Publishing the whole list would make the draw checkable
   by the people it is about, which is the point — and would also publish "how many hours each of
   these named people spent in our worlds", which is presence data about members. Not resolved; the
   conservative side was taken.
2. **Whether a re-draw should have to say why.** A required reason would make the second draw's
   motive part of the record. It would also be a text box somebody types "again" into.
3. **A cap expressed as a multiple of the smallest weight** rather than a flat number. More
   principled, harder to explain to the person setting it.
