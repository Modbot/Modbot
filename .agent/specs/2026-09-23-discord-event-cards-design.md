# Discord event cards

**2026-09-23.** How one Modbot event becomes one Discord card, and why there is a card per kind of
event rather than one card for all of them.

This narrows the Discord moderation log design (2026-09-14 §5), which said a fact becomes "a card"
and left the card one shape.

---

## 1. What is wrong now, exactly

Two things, and the second is a consequence of the first.

**The payload is thrown away before a card is built.** `ModerationEventView.ReadPayload` opens the
event's `data` and reads exactly two names out of it: `actorDisplayName` and `description`.
Everything else goes in the bin. That is not a small loss, because
`AuditLogEntryMapper.Lift` deliberately put the interesting parts there under known names, and only
where a real sample showed them:

| Event | Lifted, and discarded |
|---|---|
| Role given, taken, changed | `roleId`, `roleName` |
| Instance opened, closed | `groupAccessType` |
| Announcement | `title`, `message` |
| Group post | `title`, `text`, `authorId`, `visibility` |
| Calendar event | `title`, `type`, `accessType` |
| Anything that changed | `changed`, a before-and-after object |
| Everything | `auditData`, VRChat's own payload, verbatim |

**So every card is the same card.** `ModerationEventEmbed.For` gives all of it one shape: the
person on the author line, the type's label as the title, VRChat's own sentence as the description,
`By` and `When` as the only two fields, and the colour as the only thing that tells two kinds of
event apart. A role change and a ban differ by hue. Neither says which role, or why.

A moderator reading the channel is therefore told that something happened to somebody, and has to
open Modbot to find out what. The card is a notification pretending to be a record.

## 2. The converter

`EventCard.For(view, style, picture)` — a fact in, a card out, no I/O, as today.

It dispatches on `view.Type` to a builder for that kind of event, and **falls back to today's shape
for any type without one**. That fallback is the whole safety of this design: there are more than
fifty sendable types, they arrive from VRChat's audit log rather than from us, and a type nobody
has written a builder for must still post a card rather than throw or post nothing.

Purity is kept for the reason it was kept before: the poster's loop stays small, and a card can be
checked without a gateway. Pictures keep arriving already resolved.

## 3. The payload has to survive the trip

`ModerationEventView` gains the lifted fields, read by name, **only the names §1 lists**. Nothing
guesses at a name it has not seen, for the reason the mapper gives: a field read under a guessed
name is one that two producers and a query then depend on.

`changed` is read as a list of before-and-after pairs, because that is the one shape several types
share and the one a card can draw without knowing the type.

The verbatim `auditData` stays unread by cards. It is the record; it is not display.

## 4. What each kind of card says

The rule for every row: **the title says what happened including the thing it happened to, and the
fields carry what a moderator would otherwise open Modbot to find.**

| Event | Title | Beyond By and When |
|---|---|---|
| Banned, unbanned | Banned from the group | The reason, when Modbot has one |
| Kicked from the group | Kicked from the group | The reason, when Modbot has one |
| Warned, kicked from an instance | Warned in an instance | The reason, and the instance |
| Role given, taken | Given the **Moderator** role | The role, named, not its id |
| Role changed | The **Moderator** role changed | What changed, before → after |
| Announcement | The announcement's own title | The message as the description |
| Group post | The post's own title | The text as the description, and who may see it |
| Instance opened, closed | Instance opened | Who it was open to |
| Calendar event | The event's own title | What kind, and who may come |
| Profile changed | Name changed | Before → after, from `changed` |
| Avatar changed | Changed avatar | The avatar, as a picture |
| Anything else | Today's shape, unchanged | — |

Where the event carries its own title — an announcement, a post, a calendar event — **that title is
the card's title**, because a channel of those should read as the things themselves rather than as
a list of the word "Announcement".

## 5. Components V2, and where it does not go

Discord's V2 components are available in the library Modbot already uses (Discord.Net 3.20.1:
`ContainerBuilder`, `SectionBuilder`, `TextDisplayBuilder`, `MediaGalleryBuilder`,
`SeparatorBuilder`, `ThumbnailBuilder`, and `MessageFlags.ComponentsV2`).

**They cannot be used for the moderation log, and this is not a preference.** Discord's own
reference says a message carrying the `IS_COMPONENTS_V2` flag has `content` and `embeds` disabled
outright, and allows forty components in total. The log deliberately batches up to ten cards into
one message (`EmbedsPerMessage`), which is what keeps a backlog inside the channel's rate limit.
Ten V2 cards would be ten containers plus their children — at or over the forty — and the flag
would take the embeds away from every card that did fit. Turning the log into one message per event
to get richer cards would trade the rate limit for decoration.

So the split is:

- **The batched moderation log: classic embeds, one shape per kind of event.** This is where the
  complaint in §1 actually lives, and §4 is the whole of the fix. Nothing about it needs V2.
- **Messages that are already one card: V2 where the shape earns it.** A `Section` holding the text
  with the person's picture as its thumbnail accessory, and the link to Modbot as a button, is a
  better card than an embed with an author line — the picture sits beside the words rather than
  above them, and the button is a button. An avatar change is a `MediaGallery`, because the avatar
  is the point of the card and an embed thumbnail is 80 pixels of it.

The candidates for the second are the ones that already send alone: the `/lookup` reply, alerts,
and the instance card. **A card must render as an embed either way.** V2 is an alternative
rendering of a card that already exists, never the only way a kind of event can be shown, so a
deployment that hits any limit, or a Discord that changes its mind about V2, still has a channel
that works.

## 6. What does not change

The safety rules are the ones that were already right, and none of them relax:

- Names are text somebody chose. The author line and the title are slots Discord prints literally,
  so control characters are stripped and nothing is escaped; fields are markdown, so what goes in
  them is escaped, and mentions are disabled at send.
- Times use Discord's own timestamp markup, so every reader sees their own zone.
- An address that is not plain `https` is left off rather than allowed to lose the whole post.
- No ids in the body. `CardLink` says why; the author line stays the one place an id can appear,
  and only when Modbot has never read a profile.
- Every string is cut to Discord's limit, never mid-escape-sequence.

## 7. What the data turned out to hold

Three rows of §4 assumed something the recorded facts do not carry. Built as the data allows, not as
the table promised:

**A rejected join request and a blocked one keep their own words.** §4 gave both the one title
"Join request turned away". That was wrong twice over: Modbot keeps the two apart everywhere else,
and being blocked is not being turned down — somebody blocked cannot ask again. Their existing
labels are already plain words, so both fall through to the ordinary card and keep them.

**There is no reason to show, and no field for one.** VRChat's ban, unban, group kick, instance kick
and instance warn entries carry an empty `auditData` or nothing but `location` (audit-log research
§6), and their `description` is VRChat's own template — `AuditLogEntryMapper` says so outright.
Modbot's own actions (`modbot.action.*`) do carry the reasons a moderator picked, and already write
them into that same `description`, which every card quotes. So a **Reason** field would be empty on
every card built from VRChat's log and a duplicate on every card built from Modbot's own, and there
is none.

**There is no avatar to show.** `vrchat.avatar.change` is the client's own report and carries a
display name and an avatar's name — neither of them a picture, and neither of them a name §1 lifts.
The poster resolves one address per card, the person's own face, so there is nothing for a picture
slot to hold. The kind falls through to §2's fallback.

**A card about a location is not a card about a person.** A role change, an instance opened or
closed, an announcement, a post and a calendar entry all name a role, a location or a notification
in `subject_id`, never a person. The one shape put that string on the author line and linked it to a
person's popup, which was wrong on every one of them. Those cards are headed by the group's name
instead, the way an instance announcement and a calendar post already are (Discord embeds design
§4.2, §4.3), and link the world where the event names one.

Two smaller things: the role is named in the title and not repeated in a field, because the same
thing twice on one card is noise; and a warn or an instance kick names its instance by the world's
name and the instance's number, which needs the world to have been read — one Modbot has never read
leaves the field off rather than printing an id.

## 8. What this does not do

It does not add an event type, change which types may be sent, or change routing. It does not read
`auditData`. It does not make the log post more messages than it does now.

## 9. Modbot's own facts and Discord's (added 2026-10-02)

Three cards from a live server showed only an id where the subject should be: "Planned event
changed" with the event's id and nothing to say which event or what changed; "Event failed to
publish" with the id and no word of where or why; "Joined a voice channel" with a Discord user id and
no channel. All three fell through to §2's fallback, which looks the subject up among VRChat profiles
and prints the id when it finds nobody. A calendar event's id, a Discord account's id and a list's id
are never in that table.

**Modbot's own payloads are read by name too.** §3 keeps the card to the names the audit-log mapper
lifts. The calendar, the Discord member recorder and the page saves are Modbot's own producers, whose
names are written in this repository, so the card reads theirs as well (`ModbotDetails`), and only in
the cards for those kinds of event.

**A calendar card is titled by the event.** Every calendar fact carries the event's title; it heads
the card, linked to the event on the calendar page (`/calendar?event=`, which the calendar feed links
to as well), and what happened sits on the author line where the group's name would be. A fact with no
title reads "A calendar event". A deleted event links nowhere, as there is nothing left to open.

- A change lists what changed, in the calendar form's own words for each field, before and after, up
  to the same eight lines a group change lists. Times are Discord's timestamp markup, which each reader
  sees in their own time zone, as on every other card. An id or an address is named as changed and
  not printed. A change with nothing visible in it says "No visible change": the route asked for every
  change, and a missing card reads as a channel that stopped working.
- A failure says **Where** (VRChat calendar, Discord event, Discord channel post) and **Why**:
  VRChat's or Discord's own words, then the fix when the producer knew it. Discord's refusals already
  name the permission the bot needs; a VRChat refusal for a group permission Modbot's account lacks now
  writes that permission's sentence into the fact as `fix`. Older facts have no place or error, and
  their cards say only which event.
- Created, opened, finished, one date changed or cancelled, an instance opened, a world picked: the
  event's name and the one or two facts that matter (the start, the date, the world).

**A Discord account is never a bare id.** The name comes from Modbot's member list, else from the
name the fact kept when it was written; with neither, the card names the person with Discord's own
mention `<@id>` in a **Who** field, which Discord draws as their name in the server. Mentions in a
card ping nobody: every message the bot sends turns them off. Channels and roles are named the same
way (`<#id>`, `<@&id>`), and a move shows where from and where to.

**A thing Modbot keeps is titled by its name.** A list, a role, a giveaway, a webhook: the name the
payload carries heads the card, under what happened. One with no name in its payload (the calendar
feed, a settings change) is headed by the group with the event's label as its title.

## 10. Discord's reasons, Modbot's decisions, and Discord pictures (TASK-039, merged 2026-10-03)

§7's "there is no reason to show, and no field for one" was true of VRChat's log and wrong for
everything else. TASK-039 was built beside §9 from an older base and also gave Discord accounts a
card of their own. When the two met, §9's Discord cards were kept as they are (the name from the
member list, then the name on the fact, then Discord's mention in **Who**; channels and roles by
mention or name; **Until** and **Messages**), and only what §9 did not have was taken from TASK-039:

**Discord's reason.** `DiscordEventRecorder` writes the audit log's `reason`. `ModbotDetails` reads
it under that name, and the Discord ban, unban, kick, timeout and timeout-removed cards show
**Reason** beside §9's fields.

**A Discord member's picture.** A card headed by a Discord member's name carries their picture,
which is Discord's own address: Discord loads it itself, so it is neither uploaded nor covered by
the switch for fetching VRChat pictures. The name and picture come from the server's member list,
then from its ban list for somebody who has left (`DiscordPeople`). A card with no name (§9's
mention) has no picture either: Discord draws a picture only beside an author line.

**Who decided a ban made from Modbot.** VRChat's entry says Modbot's account did it. Modbot's own
`modbot.action.*` fact for the same press carries the moderator and the reasons picked, and the two
are one decision by `LinkedActions`' rule (same person, within its window, actors not disagreeing).
The poster pairs them, and VRChat's card gains **Decided by** and **Reason**. The
`modbot.action.*` card itself shows **Reason** as a field and leaves off the description, which
said the same things as one sentence. A Modbot account in `By` or **Decided by** opens that account
(`?subject=account:…`); an id that is not a Modbot account is named without a link.

**The note from a ban, kick or unban stays off its card.** The action's `note` is one moderator's
free text, which the web app shows only to people who may read the audit log. A channel has no such
gate and no route setting chooses to send that note with the action, so the view does not read it at
all. A note reaches a channel only one way, unchanged by this: a route that takes "Note added"
(`modbot.note.add`) posts that event's own card, which quotes its text, because the operator chose
that type for that channel.

TASK-039 also filled messages by size (`CardSize`); `EmbedSize`, built on staging for the same
refusal, does that job and TASK-039's copy was dropped.

The names read are, again, only the ones a producer writes: `reason` from `DiscordEventRecorder`,
`reasonLabels` from `ModerationActionService`, both in `ModbotDetails` beside §9's names.
