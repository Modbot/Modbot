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
| Join request rejected, blocked | Join request turned away | — |
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
