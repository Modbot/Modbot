# Repeats in Discord event channels

- **Date:** 2026-10-02
- **Covers:** writing a repeat of the same change into the post before it, instead of posting it again
- **Builds on:** [Discord event routes](2026-09-15-discord-event-routes-design.md) §5 (delivery) and
  [Discord event cards](2026-09-23-discord-event-cards-design.md)
- **Does not cover:** a switch per channel rule (see §6)

## 1. Why

A real server's event channel was about half "Group details changed", one every five minutes,
because the group-info poll writes one each time the online count moves. The rest was "Profile
changed". A ban or a join in between was easy to miss, and a channel that is mostly noise is a
channel staff stop reading. Unticking the event type was the other way out; the maintainer chose
this instead (2026-10-02), so a channel can keep the changes without drowning in them.

## 2. What counts as a repeat

An event is a repeat of the post before it when all of these hold:

1. **Its type folds repeats** (`DiscordEventTypes.FoldsRepeats`). These are changes to one thing whose
   subject *is* that thing: group details (`vrchat.group.update`), the group profile edited in
   Modbot, a VRChat role's settings, a group instance, a recurring calendar entry, a person's
   profile, a person's avatar, a Discord nickname, a Discord channel and a Discord role.
2. **Same type, same subject, same person who did it, same fields.** Two moderators editing the same
   role are two posts, so `By` on the card is never wrong. Two events nobody did (the poll's readings)
   match. The subject and the person each count with their platform, since an id is opaque text and
   two systems' ids may read alike.
   The **fields** are the names the change touched (`EventCard.ChangedFields`: lower-cased, in one
   fixed order, without the bookkeeping fields a group card leaves out). The card draws the latest
   change only, so a run that mixed different fields would show the last and hide the rest: a Rules
   or description change must never vanish into "12 times" of online-count readings. A reading that
   touched other fields is a post of its own, and what follows it goes into that one, not the earlier.
3. **The post would cover no more than the window**, from the first event in it to the latest
   (§3).
4. **The post is still the newest message in the channel** (§4).

Left out on purpose:

- **Every action**: bans, kicks, warns, joins, leaves, role given or taken, notes, case files. Each
  is something a moderator reads the channel for, and a count would hide it.
- **Modbot's own group role and group post edits.** Their subject is the group, not the role or the
  post, so two different roles would fold into one card.
- **Modbot's accounts, roles and settings.** Who changed what in Modbot is a record people may need
  to read line by line.

## 3. The window

One hour (`ModerationLogOptions.RepeatWindow`), counted from the **first** event in the post to the
latest, not from the latest. With a sliding window a poll every five minutes would keep one post
going for days, and its count would say nothing about when. With a fixed one the channel still shows
roughly when things happened: a group whose count moves every five minutes is one post an hour
instead of twelve.

## 4. Only the newest message

A repeat is written into a post only while nothing has come after it. Otherwise the edit would
change something higher up the channel, and the channel would no longer read in order; a new post is
never wrong, an edit out of order is.

- **Modbot's own posts.** The channel's row remembers the one post repeats may still go into, and
  only when the newest message the poster sent there is a single card of a type that folds. Any
  other message the poster sends to that channel clears it. A message with several cards (a backlog)
  is never edited.
- **Everyone else's.** Before an edit the poster reads the channel's messages after the post (one
  request, `ReadMessagesAsync` with `afterId`). Anything there -- a person's message, another bot's,
  another of Modbot's own features' (instance cards, calendar posts) -- and the repeat is posted on
  its own. A read that fails counts as "something came after".
- **Inside one pass**, repeats that follow each other are one card. A ban between two group changes
  keeps them apart.

**Read Message History.** Discord answers a bot without it with no messages rather than a refusal,
and an edit needs the bot to fetch its own message, which needs the same permission. Without it the
edit fails, the poster forgets the post and posts the repeat on its own. Folding then simply does not
happen in that channel; nothing is lost.

## 5. The card and the edit

- The card is the **latest** event's, drawn exactly as a single card would be. Its title gains
  "` · N times in <length>`", the length in `TimeWords` units (`5m`, `1h 5m`). No "last 5m ago":
  that is wrong a minute after the edit, and the card's `When` already gives the latest time in
  each reader's own clock, kept up to date by Discord.
- An edit counts as a message for pacing: the 1.2-second gap and the five messages per pass apply
  to it, because Discord counts edits against the channel's rate limit.
- The pictures are sent again with an edit, so a person whose picture changed is shown with the new
  one.
- **A post deleted by hand**, or one the bot can no longer edit: Discord refuses for good, the
  poster forgets it and posts the repeats as a new post, counted from this pass.
- **Any other refusal** (a rate limit, Discord being down) is a refused post as before: the channel
  waits 30 seconds, its place does not move, and the same edit is tried again.

## 6. What is stored

On `discord_event_channel`, so a restart carries on editing the same post:

| Column | Holds |
|---|---|
| `repeat_post_id` | The message repeats may go into, or null |
| `repeat_type`, `repeat_subject_platform`, `repeat_subject_id`, `repeat_actor_platform`, `repeat_actor_id`, `repeat_fields` | What it is about and which fields it touched, to match the next event against |
| `repeat_count` | How many events it stands for |
| `repeat_first_at`, `repeat_last_at` | When the first and latest happened, for the window and the title |

The row goes when the channel is turned off, so turning it back on starts fresh, as its place does.

**No switch per rule.** A switch would need a column on `discord_event_route`, a control in the
rule window and the API, for a behaviour that only ever applies to changes. If a channel does want
every reading, that is a follow-up.
