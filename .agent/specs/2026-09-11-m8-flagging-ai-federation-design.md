# Modbot M8 — Group Flagging, AI Assistance & Federation

- **Date:** 2026-09-11
- **Status:** Draft, awaiting review
- **Covers:** M8 — flagging by group membership, AI-assisted profile review, the federated warning network
- **Depends on:** everything prior — particularly M4 (actions, classification) and M5 (linking)

---

## 1. Why this is last

Every feature here is **advisory input to a human decision**, and every one of them is a way to get a
person removed from a community based on something other than what they did in it.

Nothing else in Modbot depends on any of this. A group that never enables a single M8 feature has a
complete, useful moderation tool. That property is deliberate and must be preserved: **Modbot is
fully functional with M8 switched off**, and every feature in this milestone ships disabled.

This is also the only milestone that is inherently political. The project's stated goal is a tool
usable by everyone with no VRChat politics baked in, which is not achieved by avoiding the subject —
it is achieved by making sure Modbot never decides anything on a group's behalf.

---

## 2. The rule that governs all of M8

> **Modbot never takes a moderation action based on a federation signal or a group flag. It surfaces
> the signal, attributes it, and a human in the receiving group decides.**

Auto-action on inherited or inferred signals is the line between a moderation tool and a weapon. A
system that automatically bans on a third party's say-so means one group's grudge, mistake, or bad
faith propagates untouched into every group downstream, and the person affected has no one to appeal
to because nobody chose anything.

This is not configurable. There is no "enable auto-ban from federation" setting, because the moment
that setting exists it becomes the default in half the deployments and Modbot becomes the
infrastructure of exactly the thing it was supposed to avoid.

**AI moderation rules are the exception (changed 2026-09-15).** This rule used to cover every M8
signal, AI included. The maintainer chose to let AI moderation rules -- term lists and AI topics --
act on Discord chat, for example deleting a message or timing somebody out. The difference from
federation is that the rule is the group's own, written and switched on by its own operator, not a
third party's say-so.

- Every rule starts as flag-only. It acts only after an operator sets that rule to act.
- Every action is recorded as a fact naming the rule, the message or person it matched, and the
  operator who set the rule to act, so the tool's actions are audited like a moderator's.
- Actions on VRChat (kicks, bans, group removal) are still never taken on an AI signal.

---

## 3. Group flagging

Flag users by groups they belong to — known crasher groups, groups a community does not wish to be
associated with.

### 3.1 It is guilt by association, and the UI says so

That is not a criticism; it is a factual description of the signal's strength, and pretending
otherwise produces bad moderation. People join groups for jokes, out of curiosity, years ago, or
because a friend did.

So the signal is presented as *"member of 3 flagged groups"* with the groups named and the operator's
own reason for flagging each one shown — never as a score, a risk rating, or a colour that implies a
verdict. A moderator sees the input, not a conclusion.

### 3.2 Flag lists are local, and shareable only by choice

Each deployment maintains its own list with its own reasons. Lists can be **exported and imported**,
so groups may share them the way people share filter lists — but importing is an explicit act, the
source is recorded, and imported entries stay attributed to their origin.

There is no built-in list. Shipping one would make the project the arbiter of which VRChat groups are
disreputable, which is precisely the politics this design is meant to stay out of.

### 3.3 Cost

Checking group membership is API traffic, and per foundation §4.3.4 the endpoint's rate limit must be
confirmed before this is built. It is checked lazily — on profile view and at the moment of a
moderation action — never as a background sweep of every member.

---

## 4. AI-assisted profile review

Automated checking of display names, bios and profile content for things a group may want to know
about.

### 4.1 Advisory, explained, and never acting

An AI flag produces a note on the profile and, at most, a `Warning` notification. It cannot kick,
ban, or restrict anyone. It must state **what it flagged and why**, in text, so a moderator evaluates
the reasoning rather than deferring to a verdict.

### 4.2 Local models are a first-class path

Sending members' profile text to a third-party API is a decision a self-hosting group should be able
to decline without losing the feature. Modbot supports a **local model endpoint** (Ollama or any
OpenAI-compatible server) as a fully supported configuration, not a degraded fallback.

Where a hosted provider is used, the deployment configures its own key, pays its own costs, and is
told plainly what text is transmitted.

### 4.3 Thresholds and categories belong to the group

What counts as unacceptable differs enormously between an 18+ club, an all-ages community, and a
language-learning group. Modbot ships categories and no opinion about which matter, with per-category
sensitivity set by the group.

### 4.4 False positives are the expected case

Models are wrong, and they are wrong unevenly — non-English text, reclaimed language, community
in-jokes and unusual names all draw false positives at higher rates.

- Flags are dismissible, and a dismissal **suppresses that flag for that user permanently**. Nobody
  should re-review the same false positive weekly.
- Dismissal rates per category are shown. A category dismissed 90% of the time is noise and should be
  turned off; Modbot surfaces that rather than letting people quietly ignore it.
- AI flags are facts like anything else, so the tool's own accuracy is auditable.

---

## 5. Federation

The shared warning network: when a group bans someone, other Modbot deployments may be told.

### 5.1 Explicit peering, no central registry

Deployments **peer with deployments they choose**. There is no hub, no central blocklist, and no
directory the project operates.

This is the single most important structural decision in M8. A central list would make whoever
controls it the de facto moderation authority for every group using Modbot — which is an enormous
amount of unaccountable power over a community, and an irresistible target for capture. Peer-to-peer
by explicit consent means the worst case is a bad peer you can drop, not a bad authority you cannot.

Community-maintained **peer lists** may circulate as importable files, the same as flag lists (§3.2)
— chosen, attributed, and revocable.

### 5.2 Both directions are opt-in and independent

Sharing outward and accepting inward are separate switches. A group may consume signals without
publishing any, or publish without consuming. Reciprocity is never required, because requiring it
would pressure groups into sharing data they would rather not.

Per-ban opt-out at the moment of banning, per foundation's original intent: a moderator can mark a
ban as local-only, and some classifications (a private dispute, a mistake) should default that way.

### 5.3 Signals carry attribution and evidence

A signal is never an anonymous "this user is bad". It carries:

- **Which group** issued it, and when.
- **The classification** (foundation §5.8.2) — "crasher" and "harassment" warrant different responses,
  and a receiving moderator needs to know which.
- The originating group's own **stated reason** from the ban report (M4 §7).
- Whether that group has since **reversed** it — reversals propagate, or the network accumulates
  permanent accusations that were withdrawn at the source.

### 5.4 Trust is per-peer and adjustable

A receiving group decides how much weight each peer's signals carry — shown prominently, shown
quietly, or ignored. Peers can be removed, and removing one removes its signals from view.

Modbot shows a peer's **signal volume and your own dismissal rate for it**, because a peer whose
signals you dismiss nine times out of ten is a peer costing you attention.

### 5.5 The subject

People are being discussed across organisational boundaries, and the design should not pretend
otherwise.

- Signals are about **actions a group took** ("this group banned this user, classified X"), not
  characterisations of a person. The distinction is real: the first is a verifiable fact about the
  issuing group, the second is an accusation.
- Reversals propagate (§5.3).
- Purge-user (foundation §5.5) covers received signals locally.
- A deployment can see everything it has published, so an operator always knows what they are
  asserting about people.

### 5.6 Technically

Signed, authenticated instance-to-instance HTTP. Each deployment has a keypair; signals are signed by
the issuer so attribution cannot be forged by an intermediary. Pull-based with cursors — a peer going
offline degrades to stale data, never to a stuck queue. Volume is low enough that this needs no
special infrastructure.

---

## 6. Non-goals

- **Auto-action on a federation signal or a group flag**, at any confidence, under any configuration,
  and any AI action on VRChat (§2). AI moderation rules may act on Discord chat when set to.
- A central blocklist, registry, or project-operated peer directory.
- A shipped default flag list.
- Risk scores, threat ratings, or any single number purporting to summarise a person.
- Using AI to write moderation decisions, ban reasons, or reports on a moderator's behalf.
- Federating anything other than moderation signals. Presence, analytics and member data never leave
  the deployment.

---

## 7. Open questions

1. **Peer discovery without a registry.** Manual URL exchange is safe and clunky. Signed importable
   peer lists are probably the answer; worth confirming before building.
2. **Whether a group's own instance identity should be pseudonymous** to peers it has not explicitly
   trusted, to limit fingerprinting of small communities.
3. **Signal expiry.** A ban from four years ago is weaker evidence than one from last week. Time decay
   in display is probably right; an actual expiry is a policy choice that likely belongs to the
   receiving group.
4. **Rate limit of the group-membership endpoint** for §3.3 — must be asked before building, per
   foundation §4.3.4.
5. **Whether AI review should run at all without an explicit per-deployment acknowledgement** of what
   is sent where. Leaning yes: a one-time confirmation at enable, not a recurring nag.
