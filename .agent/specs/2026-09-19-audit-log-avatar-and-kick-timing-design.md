# Naming the avatar, and how long they had been there

**2026-09-19**

Two things the audit log knew and did not say.

An avatar change read *“Ada changed avatar.”* — the one fact about the change it did not carry
was which avatar. An instance kick read *“Mira kicked Ada out of The Black Cat #39047.”* — true,
and silent about the thing a moderator reading a kick a week later always wants to know, which is
whether Ada had been there forty minutes or forty seconds.

Neither needs a new VRChat call. Both are already in what Modbot holds.

## 1. The avatar

### 1.1 What VRChat actually gives

**A display name, and never an id.** VRChat's log line is

```
Switching <displayName> to avatar <avatarName>
```

and that is the whole of it. There is no `avtr_…` anywhere in the log — VRChat withholds avatar
ids from clients deliberately, to make avatar ripping harder (log research §4, and the remark on
`AvatarSwitchedEvent`). Nor does the group audit log carry avatar changes at all: the 1,241 live
entries hold no avatar event of any kind (audit-log research §1, §6). A companion watching a
moderator's own log is the only source Modbot has, and a name is all it ever sees.

So the instruction to "keep the id too, because names change" cannot be followed: **there is no id
to keep.** Two people wearing avatars with the same name are indistinguishable to Modbot, and an
avatar renamed between two changes reads as two avatars. That is a limit of the source, stated
here so nobody goes looking for a column that could hold the id.

**Nothing new is fetched.** VRChat does have `GET /avatars/{id}`, but with no id there is nothing
to pass it, so the question of that endpoint's rate limit does not arise. No endpoint Modbot does
not already call is used by this change.

### 1.2 Where the name comes from

It is already stored. The chain has been complete since the companion protocol was written:

- `InstanceSessionTracker` resolves the ambiguous split of the log line against the roster it
  holds, and emits `ObservedPresence.AvatarName`.
- `PresenceEventMapper` puts it on the wire as `data.avatarName`.
- `EventsHandler.ToFact` is on the protocol's short allow-list of payload keys (`displayName`,
  `avatarName`) and writes it into the fact's `data`.
- The audit-log API returns a fact's `data` verbatim.

The only thing missing was the sentence. `factSentence.tsx` said *“changed avatar.”* and never
read the field.

### 1.3 What changed

One sentence. `vrchat.avatar.change` now reads

> Ada switched to the avatar “Nardoragon”.

and, when the payload carried no name — an older row, or a log line the roster could not
disambiguate —

> Ada changed avatar.

The wording matches what the companion's own journal has always shown for the same event
(`SentJournal.Sentence`), so the two screens say the same thing about the same moment.

Nothing was rewritten. Every avatar change ever recorded reads the fuller way the next time
somebody opens the page, which is the property `factSentence.tsx` exists for.

## 2. How long they had been in the instance

### 2.1 Which kick

There is exactly one fact type that means *an instance kick*: `vrchat.group.instance.kick`, and it
always arrives through the group audit-log sync. That covers both paths, because both paths end
there:

- A moderator presses **Kick** in VRChat. VRChat writes the audit entry; Modbot's sync reads it.
- A moderator presses **Kick** in Modbot. `ModerationActionService` performs a *group* kick —
  `GroupModeration.KickAsync`, a removal from the group, with no instance in it — and writes
  `modbot.action.kick`. VRChat then throws the person out of whatever group instance they were
  standing in and writes `group.instance.kick` for it, which the sync reads like any other.
  `LinkedActions` already pairs the two, so the instance kick is shown beside Modbot's own record
  of the press rather than as a second row.

So **the duration is worked out in one place, on the audit-log path**, and Modbot's own
`modbot.action.kick` carries none of its own. It is not an instance event and has no instance to
measure against; the instance kick that followed it does, and is displayed with it.

`vrchat.group.member.remove` (a group kick) is likewise left alone, for the same reason.

### 2.2 Worked out when the kick is recorded, and stored

Both were available. The deciding argument is retention.

Presence facts are the `Presence` retention class — ninety days by default (foundation §5.5).
Kicks are `Moderation` — kept forever. Work the duration out at read time and the same moderation
record says *“after 40 minutes in the instance”* today and says nothing at all in four months,
because unrelated data expired. **A record whose content quietly empties out is not a record.**
That is the whole reason the fact log stores what it observed instead of deriving it.

The cost of choosing storage is that presence arriving *after* the kick was recorded is never
folded in. In practice that window is small: the arrival being measured is by definition older
than the kick, the companion flushes in seconds, and the audit log is polled continuously. A
companion that was offline and buffering for hours is the case that loses the duration, and it
loses it by saying nothing — which is the direction this whole feature is required to fail in.

The value is written into the fact's `data`, which is `jsonb`. **No column, no model change, no
migration.**

### 2.3 The rule

`InstanceWatching.Work` already answers *who was in this instance at this moment, and since when*,
from presence facts alone, and it is careful in exactly the ways this needs to be: a roster is
believed only while a companion was actually reporting from inside the instance, an unwatched gap
starts a fresh stretch rather than carrying the old one across it, and somebody first seen in an
arrival burst is recorded as **here before** that moment, never as having arrived at it.

So the rule reuses it rather than reimplementing it. `TimeInInstance.Work` asks
`InstanceWatching.Work` for the instance as it stood at the kick, finds the kicked person in it,
and returns the time from their `Since` to the kick, plus the `SeenArriving` flag it already
carries.

Three things follow, and each of them is the honest answer rather than a convenient one:

**Nobody was reporting — nothing is said.** No presence facts, or none inside a watch that reached
the kick, and the person is simply not in the roster. No field is written, and the entry reads the
way it always has. Never zero, never a guess.

**Seen arriving, or only seen already there.** A companion that watched them walk in gives an
exact start. A companion that arrived later and found them already there gives a *lower bound* —
they had been there at least that long and arrived at some earlier time nobody saw. Both are
recorded, and the sentence says which:

> Mira kicked Ada out of The Black Cat #39047 after 12 minutes in the instance.
>
> Mira kicked Ada out of The Black Cat #39047 after at least 12 minutes in the instance.

Collapsing those two into one number would be inventing precision in exactly the way `occurred_before`
and `SeenArriving` exist to prevent.

**A lower bound of zero is not worth saying.** A moderator who walks in and kicks somebody in the
same second knows only "they were here", which the entry already says. That case writes nothing.
A person seen *arriving* and kicked in the same second is different — that is a real measurement
of zero, and it is kept.

### 2.4 The person's own leave does not count against them

A kick throws the person out, so their companion-reported `vrchat.instance.leave` lands within a
second or two of VRChat's audit entry — and VRChat's log timestamps are whole seconds, so it can
land on either side of it. Counted naively, that leave says the person was not in the instance at
the moment they were kicked out of it, and every kick would report nothing.

So the subject's own departure marks within ten seconds of the kick are set aside when the
question is asked. Ten seconds is the allowance `InstanceWatching.ArrivalBurstAllowance` and
`LinkedActions.Window` already use for the same kind of second-resolution slop.

### 2.5 How far back to look

Twenty-four hours. A stay longer than that is not a real person in a real instance, and the bound
is what keeps the lookup to one small query per instance on a page rather than an unbounded scan.
An instance genuinely running longer than a day reports the part of the stay it can see, which is
the same shape of answer as a watch that started late.

### 2.6 What is written

Two fields on the kick fact's payload:

| Field | Meaning |
|---|---|
| `inInstanceSeconds` | Whole seconds from the arrival Modbot knows about to the kick. |
| `seenArriving` | `true` when a companion saw them arrive, so the number is exact; `false` when it only ever saw them already there, so the number is a lower bound. |

Both absent when Modbot does not know. `seenArriving` is the same word `PersonHere.SeenArriving`
already uses for the same distinction.

### 2.7 Cost

One query per distinct instance on a page of audit entries, not one per kick — a kick spree is one
instance, and the page's kicks are grouped before anything is loaded. A page with no kick on it
loads nothing extra, which is most pages.

## 3. What was decided against

**Fetching the avatar by id.** There is no id (§1.1). Had there been one, this would have needed a
new endpoint and the rate-limit question would have had to be asked first (foundation §4.3.4).

**Deriving the duration when the log is read.** Loses the answer when presence is pruned (§2.2).

**Putting the duration on `modbot.action.kick`.** It is a group removal with no instance in it.
Measuring against "wherever they happened to be" would be Modbot inventing the instance rather
than reading one (§2.1).

**Showing a duration for a group kick or a ban.** Same reason. The instance kick VRChat writes
alongside them carries it, and the linked-facts display already puts the two together.

**A "roughly" or "about" hedge on every number.** The distinction that matters is exact versus
lower bound, and that is said in words. A hedge on both would make the exact case read as a guess.

**Storing the arrival time rather than the duration.** The arrival is a fact of its own and
already exists as one; copying it onto the kick would create a second, divergent record of the
same moment. What the kick needs is the measurement, which is the thing that cannot be
reconstructed later.
