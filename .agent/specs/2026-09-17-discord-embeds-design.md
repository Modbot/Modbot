# Modbot — What a Discord card shows

- **Date:** 2026-09-17
- **Status:** Built
- **Covers:** Every embed the bot posts — the moderation log, instance announcements, calendar
  posts, giveaway posts, alerts, insights, the `/lookup` and `/recent` replies, and the link prompt
- **Depends on:** foundation §10.2 (the subject popup and the `?subject=` format), §4.1 (the VRChat
  gate), §3.1.1 (ids have no structure); brand design 2026-09-16 (the violet and the mark);
  VRChat files and profile pictures design 2026-09-17 (`ProfilePictures.Best`, `IconUrl`,
  `BannerUrl`, the represented group)
- **Narrows:** the rule recorded in `Modbot.Discord`'s own doc comments that the Discord side reads
  rows and asks VRChat nothing — see §3.4

---

## 1. What the maintainer asked for

> the embeds look weird, with lots of raw ids printed where it should be a linked username, and
> they should use a person's icon as the embed icon or thumbnail and their banner as the embed
> image

A moderation card read:

```
Banned
Who: **jessie** (`usr_c9094d86-1846-43eb-b79d-7e3dc318f42a`)   By: **E-Ray** (`usr_2a32…`)
When: 13 September 2026 02:25 (2 hours ago)
```

Forty opaque characters, twice, on every card. They say nothing about who somebody is, they cannot
be searched by eye, and they took a third of the width of a field — while the name they were
attached to was not a link, so a moderator who wanted to know more had nothing to click. The same
shape was repeated on the `/lookup` reply, in `/recent`, and in the list the bot prints when several
people match a name.

## 2. A person reads as a name

**One helper, `CardLink`, and every card uses it.** A person, a world, an instance or a Discord
account becomes `[Display Name](<their popup in Modbot>)`.

| What Modbot knows | What the card shows |
|---|---|
| A name and a public address | `[jessie](https://…/audit?subject=usr_…)` |
| A name, no public address | `jessie`, escaped, no link |
| No name, a public address | ``[`usr_…`](https://…/audit?subject=usr_…)`` |
| No name, no public address | `` `usr_…` `` |

**No public address means no link.** The rule the reset-link sender already follows (accounts and
access §4.2): a link Modbot sends out is built from the address a human typed and confirmed, never
from a request's host or a forwarded header. The name is shown on its own rather than pointing at
an address that cannot work.

### 2.1 Where the id went

**Off the card.** Not into the footer, not into a small line under the title, not behind a
disclosure — off it.

The reasoning, because this is the decision most likely to be argued with later:

- **The link carries it.** Every linked name has the id in its address, and the card's own `Url`
  opens the same person. Nothing was lost; it moved from the part a moderator reads to the part a
  moderator clicks.
- **The profile is where you copy it from.** The popup shows the id with a control that copies it.
  A moderator who needs the id is one click away, and that click is one they were already going to
  make, because nobody wants an id without also wanting the profile it belongs to.
- **It was never the thing being read.** A card is scanned. An id cannot be scanned: two ids differ
  in the middle, and a moderator comparing them by eye is doing something they should not have to
  do.

**Two exceptions, both because there is nothing else to print:**

1. **Somebody Modbot has never read a profile for.** A card headed by nobody is not a card, so the
   author line falls back to the id, and a linked name in a field falls back to the id in code
   style — code style, so it reads as an identifier rather than as a person called `usr_1234`.
2. **The list `/lookup` prints when several people match.** That list exists to say *run the command
   again with the id*, so there the id is the answer rather than decoration. It keeps the name and
   the link too.

### 2.2 The address the link opens

`PersonLink` built `/audit?subject=usr_…` and knew about people and nothing else. The popup has
since taken three more kinds, and the format grew a prefix to match (foundation §10.2):

| Kind | Address |
|---|---|
| Person | `/audit?subject=usr_…` |
| World | `/analytics/worlds?subject=world:wrld_…` |
| Instance | `/live?subject=instance:<Modbot's id>` |
| Discord account | `/discord/members?subject=discord-person:<id>` |

**A person stays bare and stays on `/audit`.** Bare because that is what the format has always said
and what keeps every link already pasted somewhere opening the same person; on `/audit` because
every card Modbot posts about a person is a moderation event and the log is the rest of them, and
because links posted before this change point there.

**The other kinds land on the list they belong to** — the page under a popup should be where a
moderator would go next to see more of the same kind of thing, and a world over the audit log is
not that.

Ids are percent-encoded and never checked for shape (§3.1.1). An instance's qualifiers carry `~`,
`(` and `)`, and they survive.

### 2.3 Escaping, in three passes rather than one

The cards had each grown their own escape and the copies had drifted. One of the differences
mattered: the moderation log's escape left `[` and `]` alone. Harmless while a name was plain text;
not harmless the moment a name goes inside `[…](…)`, where a single `]` ends the link early and puts
the address on screen.

`CardText` now has three passes, because an embed has three kinds of slot and treating them alike
breaks one of them:

| Pass | For | What it does |
|---|---|---|
| `EscapeName` | A name inside a link, a field or a list | Escapes every character Discord reads as formatting, brackets included; control characters become spaces |
| `EscapeText` | A ban reason, an event description | Escapes markdown and brackets, leaves `:` and `-` alone — a sentence with backslashes through the middle of it is worse than the risk |
| `Plain` | A title, an author line, a footer | Escapes **nothing**; strips control characters and cuts |

The third is a fix, not a refinement. Discord prints those slots literally, so the old code put a
person called `*nova*` on screen as `\*nova\*` in the title of their own card.

`Fit` cuts with an ellipsis and never leaves a dangling backslash, which would turn the ellipsis
into the thing being escaped.

## 3. Pictures

### 3.1 Why this is the hard part

Three facts that together rule out everything easy:

1. **VRChat's hosts refuse a request with no session.** A picture address in a row is an address
   only Modbot can follow.
2. **Discord fetches an embed's pictures from its own servers, signed in as nobody.** So a VRChat
   address in an embed is always a broken picture. This is not a new bug: the instance card has
   been handing its world picture straight to the embed since it was written, and it has never
   been drawn.
3. **Modbot's own `/api/files/vrchat` route needs a signed-in caller**, and most deployments have
   no address the internet can reach at all.

### 3.2 What was chosen, and what was not

**The bytes go to Discord.** The message carries the file; the embed points at it as
`attachment://<name>`.

The alternative considered was **a signed link into the file route**, derived from the address with
a server secret so no session is needed. It was rejected:

- It does nothing for a deployment with no public address, which is most of them. A feature that
  works for the minority of installations is not the feature.
- Every embed ever posted would depend on a secret that must never rotate and a deployment that
  must never move. Rotating the secret would break years of channel history, silently, all at once.
- It keeps Modbot in the path for ever: the group's moderation log stops having pictures whenever
  the server is down.

Sending the bytes has none of those. Once they are sent **Discord owns them**: the card keeps its
picture when Modbot is offline, when the deployment moves, and when VRChat rotates the file.

### 3.3 Paid for once

An instance card is rewritten every minute for as long as the instance is open. Re-uploading half a
megabyte every minute for six hours is not acceptable, so:

- **The file name is a hash of the address.** The same address always names the same file.
- **An edit that sends no list of pictures leaves the ones already on the message alone.** The
  rewritten card keeps pointing at `attachment://<name>` and nothing goes over the wire. Passing a
  list — the empty list included — replaces them, so a card that has lost its picture loses the file.
- **Within one message, the same address costs one file.** Ten moderation cards about one person in
  one message upload one picture.
- **What was fetched is remembered**, across messages and across passes, failures included.
  Remembering the failures matters more: somebody with no picture would otherwise be asked for on
  every pass for as long as their card exists.

If the address on a row changes while a card is live, the rewrite names a file that is not on the
message and Discord draws the card without it — the same as any other missing picture, and the next
post carries the new one.

**Discord's cap is ten files per message**, and it enforces it by refusing the whole message. The
cards on one message are built by separate pieces that do not know what the others asked for, so the
cap is held in the gateway. A card that does not fit loses its picture; the message still goes out.

### 3.4 Where the bytes come from

`Modbot.Discord` has never referenced `Modbot.VRChat`: the Discord side is built out of rows the
VRChat side wrote and asks VRChat nothing. Pictures are the one thing it cannot get from a row, so
**the rule is narrowed rather than dropped**:

- The interface (`IPictures`, one method: an address in, bytes or null out) lives in `Modbot.Core`.
- The one implementation lives in `Modbot.VRChat` and goes through `IVRChatGate` like everything
  else that touches VRChat (§4.1).
- `Modbot.Discord` still references no VRChat project and still knows nothing about sessions, hosts
  or cookies.

**It costs no API budget.** VRChat's file addresses are not paced and belong to no bucket — the
maintainer confirmed on 2026-09-17 that they are not rate limited.

### 3.5 Anything that goes wrong is no picture

A blank address; an address VRChat does not serve; no VRChat session (a deployment still being set
up, and every demo); a file that is gone; bytes that are not a picture — VRChat serves video from
the same addresses and an embed has nowhere to put one; a picture past four megabytes; the operator's
`VRChatImagesProxied` switch turned off; the picture source throwing.

All of them: **null, and the card is posted without it.** A broken picture and a post that did not
happen are both worse than a card with one less thing on it.

The operator's switch is honoured because it is worded as what it is — *an operator who would rather
their server never fetched a picture*. A deployment with it off shows no faces in the web app and
none in Discord.

### 3.6 Which picture goes where

| Card | Small picture | Large picture |
|---|---|---|
| Moderation event | The person's, beside their name | — |
| `/lookup` | The person's, as the thumbnail | Their banner |
| Instance | — | The world's |
| Calendar | — | The event's own, else the world's |
| Giveaway | The group's, beside its name | — |
| Alert, insight, `/recent`, link prompt | — | — |

The person's picture is whichever of the three VRChat has carried over the years that the row holds,
through `ProfilePictures.Best` — the same rule the web app follows, so a face in Discord and a face
in Modbot are never different.

**The moderation card has no banner on purpose.** The log posts up to ten cards in one message. Ten
banners is a wall, not a record. The banner belongs on the one card that is about a person rather
than about something that happened to them, which is the `/lookup` reply.

**A group's icon appears on two cards.** The person a `/lookup` reply is about may be representing a
group, and that group's name and icon sit above their name. The giveaway post is the other: a
giveaway is something the group is doing rather than something that happened to a person, so the
group's icon sits above the giveaway's name and the card carries no other picture. Modbot holds no
picture of a prize, and an empty slot is not a reason to invent one.

## 4. What each card shows

Common to all of them: times in Discord's own timestamp markup, so every reader sees their own time
zone; names escaped; mentions disabled at send time, so a name spelled like a mention is text; text
cut to Discord's limits and never mid-escape.

**No card explains anything.** A card names what happened and who. The `/lookup` reply used to
footer *"From Modbot's stored records. Nothing was fetched from VRChat for this reply."*; it has a
**Profile last refreshed** field that says the same thing as a fact rather than as a paragraph, so
the sentence is gone.

### 4.1 A moderation event

```
[icon] jessie                       ← the author line: their name, their picture, their popup
Banned                              ← the title: what happened, and the same link
> User jessie was preemptively banned by E-Ray.
By: [E-Ray](…)      When: 13 Sep 2026 02:25 (2 hours ago)
[mark] The Kingdom · 02:25
```

The person heads the card and the title says what happened to them, so a channel of these reads as
faces and verbs rather than as verbs you have to open to understand. The **Who** field is gone.

The footer names the group, so a server watching more than one Modbot can tell its channels apart,
and carries Modbot's mark (brand design 2026-09-16) when the public address is set.

Colours by event: banned red, unbanned green, kicked orange, warned yellow, a join request turned
down grey, anything else Modbot's violet.

### 4.2 An instance announcement

Unchanged in shape, and deliberately so: **this is the one card whose readers are members rather
than moderators**, and most of them have no Modbot to open. Its title is the world's name and it
opens **VRChat**, not Modbot.

What changed: the group's name sits above the title, so a member can see whose instance they are
being invited to; the world's picture now actually appears; and the colours come from the shared
palette.

**The names under "Who is here" are not links**, unlike every other name on every other card.
Twenty linked names run past Discord's 1024-character field limit long before twenty plain ones do,
and the people reading them mostly cannot follow a link into Modbot anyway.

### 4.3 A calendar post

The world is a linked name rather than an id. The event's own picture, when the moderators gave one,
is an ordinary address on a host that serves anybody, so it is linked as it is; only the world's
picture goes through the message. The group's name heads the card, as on an instance.

Scheduled violet, open green, finished dark, cancelled red.

### 4.4 An alert

A figure, what normal is, the stretch of time, and a link to the page it came from. Amber. **No
picture and no author line**: an alert is about a number, not about a person or a place, and the one
thing a moderator does with it is open the page. It gained Modbot's mark beside the footer.

### 4.5 An insight

The kind and the days as the title, the model's words as the body, violet. It gained Modbot's mark
too — it was the one thing the bot posted that did not look like it came from Modbot.

### 4.6 `/lookup`

```
[icon] The Kingdom                  ← the group they represent, with its icon
jessie                              ← their name, linked to their popup   [thumbnail: their face]
18+ verified: Yes, since 4 Sep      Profile last refreshed: 2 hours ago
Ban status: Banned 2 hours ago
Bans · kicks · warns: 1 · 0 · 2
Recent moderation events: <t:…> **Banned** — [jessie](…) by [E-Ray](…)
[banner across the bottom]
```

Red when they are banned, violet otherwise. The id line under the title is gone (§2.1). Somebody
Modbot has never read a profile for is titled by their id and says so in one sentence.

### 4.7 `/recent` and the several-people-match list

Every name a link. The match list keeps its ids, for the reason in §2.1.

### 4.8 The link prompt

A direct message, not an embed: a welcome naming the server and a **Link VRChat account** button.
Unchanged, except that the server's name is now escaped as a name rather than as free text — a
server called `**everyone**` should not make the greeting shout, and one with a `]` in it should
not be able to break out of anything.

### 4.9 A giveaway post

The giveaway card was built in parallel with this work and against the style it replaced, and was
moved onto the helpers once both had landed. It is the worked example of §5: the winners are linked
names, the colours come from `CardColour`, the group's name and Modbot's mark come from `CardStyle`,
and the group's icon is fetched through `CardPictures` — `AddAsync` on the first post,
`ReferenceAsync` on every rewrite, so a card rewritten every twenty seconds is paid for once.

Two things are particular to it, and both are recorded in the giveaways design §7.1 and §7.3:

- **The winners list is built against the 1024-character field limit rather than cut to it.** A
  linked name is several times the length of a plain one, so twenty of them no longer fit where
  twenty plain ones did; the card lists as many as fit and ends with "and N more". This is the same
  arithmetic that keeps the instance card's names plain (§4.2) — the giveaway names fewer people, so
  it could afford the links.
- **The line the bot posts when a draw is made keeps plain names.** It is message text rather than
  an embed, and this document is about embeds.

Open violet, closed dark, drawn green, cancelled red.

## 5. What a new card adopts

Anything added later takes three things and gets the rest — the giveaway card (§4.9) was the first
to do it:

- **`CardStyle`**: where Modbot is, whose group it moderates, and the mark beside a footer. One
  value, built once per pass by the poster.
- **`CardLink`**: any person, world, instance or Discord account as a linked name.
- **`CardPictures.ForMessage(on)`**: fetches a picture and hands back what the embed calls it, plus
  the list of files to give the gateway. `AddAsync` for a first post, `ReferenceAsync` for a
  rewrite.

And **`CardColour`** for the palette. It has one entry per meaning, and the point of putting it in
one place was that the cards had drifted to three reds, two greens and two greys — a banned card and
a cancelled card were different shades of the same idea for no reason anybody could have named.

## 6. Non-goals

- **No new settings.** Nothing here is configurable; the one switch it obeys
  (`VRChatImagesProxied`) already existed.
- **No picture on a card that does not want one.** Alerts and insights stay plain.
- **No linking of the names on an instance card** (§4.2).
- **No group card.** Nothing today is *about* a group; the giveaway post is about a giveaway and
  wears the group's name and icon the way an instance card wears them (§4.9).
