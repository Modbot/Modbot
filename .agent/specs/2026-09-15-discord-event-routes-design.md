# Discord event routes

- **Date:** 2026-09-15
- **Covers:** sending events to Discord channels by rule, with filters
- **Replaces:** the single moderation log channel of foundation §9 (`discord_log_channel_id`,
  `discord_log_event_types`, `discord_log_posted_through`)
- **Depends on:** the Discord channel and role index (`discord_channel`, `discord_role`)
- **Does not cover:** instance announcements, which keep their own channel and settings

## 1. What the maintainer asked for

> Press "Add channel" → select the channel → select the event types I want sent to that channel,
> and voila. Filters for events not just by event type but by subject, actor, VRChat role and
> Modbot role. The instance live channel is different and is handled separately.

The one log channel with a tick list becomes any number of **routes**. The settings page calls
the card **Channels**, because that is what a moderator is choosing; "route" is only the code's word.

## 2. A route

| Field | Meaning |
|---|---|
| `channel_id` | The Discord channel. Opaque text, never parsed. |
| `name` | Optional label shown in the list. |
| `enabled` | Off keeps the route and sends nothing. |
| `event_types` | The fact types to send. The API asks for at least one. |
| `subject_ids` | VRChat user ids. Empty means anyone. |
| `actor_ids` | VRChat user ids. |
| `actor_automatic` | Also match events nobody did (no actor): sync inferences, reviews Modbot opened, snapshots. |
| `subject_vr_chat_role_ids` | VRChat group role ids. |
| `actor_vr_chat_role_ids` | VRChat group role ids. |
| `actor_modbot_role_ids` | Modbot role ids. |
| `position` | List order. New routes go last. |

Lists are `jsonb` arrays, like `modbot_one_time_link.role_ids`.

## 3. Matching

An event goes to a route when **every filter that is set matches**. Inside one filter, **any of
the listed values** is enough. An empty filter matches everything.

- **Event type** — the fact's type is in `event_types` *and* is sendable (§4).
- **Subject** — the subject, read as a VRChat user id (below), is in `subject_ids`.
- **Actor** — the actor, read as a VRChat user id, is in `actor_ids`; or the fact has no actor
  and `actor_automatic` is on. The actor filter is set when either part is.
- **Subject VRChat roles** — the subject is a current member of the managed group holding any of
  the roles.
- **Actor VRChat roles** — the same, for the actor. An event with no actor never matches.
- **Actor Modbot roles** — the actor's Modbot account holds any of the roles. An event with no
  actor, or whose actor has no Modbot account, never matches.

**Reading a subject or actor as a person.** Facts name people on three platforms. A VRChat id is
used as it is. A Modbot account id is read as the VRChat account that account linked (accounts and
access §4.3). A Discord user id is read through the Modbot account that stored that Discord id. A
subject that is not a person — a group, a location, a channel, `settings` — reads as nobody, so it
never matches a person or role filter, and always matches a route without one.

The Modbot account for a Modbot role filter is found the same way: by id, by linked VRChat id, or by
stored Discord id.

**Roles are read when the event is sent, not when it happened.** Facts do not record the roles a
person held, and `group_member` is current state. An event caught up after a long outage is
therefore matched against roles as they are now. That is accepted: the alternative is a roles
history nobody has asked for, and the difference only shows for someone whose roles changed
between an event and its post.

**One message per channel.** When several enabled routes send to the same channel, the channel
receives each event once if any of them matches. Two routes to two channels send it twice, once
to each.

## 4. Which event types can be sent

The list is **every type in `FactType`**, grouped for the picker, minus a short list that is
never sent. A type added to `FactType` later appears on its own, in the group its name puts it in.

Groups, by the start of the type name, first rule that fits:

| Group | Types |
|---|---|
| Moderation | bans, unbans, group kicks, instance kicks and warns, rejected and blocked join requests; `modbot.report.*`, `modbot.evidence.*`, `modbot.review.*`, `modbot.user-profile.*`, `modbot.ai-moderation.*` |
| Members | `vrchat.group.member.*`, `vrchat.group.role.*`, `vrchat.group.request.*`, `vrchat.group.invite.*`, the member and ban snapshots, `vrchat.user.*` |
| Instances | `vrchat.group.instance.*`, `vrchat.instance.*`, `vrchat.avatar.*` |
| Group | the rest of `vrchat.group.*`: details, posts, calendar |
| Discord | `discord.*` |
| Access and settings | `modbot.user.*`, `modbot.role.*`, `modbot.apikey.*`, `modbot.settings.*`, `modbot.ban-reasons.*` |
| Other | everything else |

**Never sent**, whatever a route says, checked again at send time so a hand-edited row changes
nothing:

- sign-ins and failed sign-ins (`modbot.user.login*`) — a failed sign-in carries the address it came from;
- passwords, reset links and sign-out-everywhere (`modbot.user.password*`, `modbot.user.sign-out-everywhere`);
- contact details (`modbot.user.contact*`);
- Modbot's own plumbing: `modbot.discord.posted` (sending it would post about posting),
  `modbot.migration.applied`, `modbot.partition.created`, `modbot.retention.pruned`.

This keeps the guarantee the accounts design leans on — a reset link never goes anywhere but to
the person it is for — while letting a team send staff and settings changes to a private channel.

Labels are `FactLabels`, moved from the API to Core so the bot's embed titles and the picker use
the same words as the audit log.

## 5. Delivery

The existing poster and its hosted service do the work. What changes is that **each channel keeps
its own place in the fact log** (`discord_event_channel.posted_through`), rather than one number on
the settings row.

- **Why per channel.** A channel that lost its permissions refuses every post. With one shared
  place, every other channel would wait behind it. With one each, the broken channel waits and the
  rest carry on.
- **A channel no enabled route sends to is forgotten**: its row is deleted, so turning a route back
  on starts from that moment, as clearing the old channel did. A new channel starts from the newest
  fact and posts none of the history before it.
- **At least once, in order, nothing skipped.** Facts are read in id order after the channel's
  place; posts go out ten embeds to a message, at most one message every 1.2 seconds per channel
  and five per channel per pass. A refused post stops that channel's pass with its place at the
  last event that went out.
- **A refused channel is left alone for 30 seconds** (`retry_at`), and the refusal is kept on the
  row (`last_error`, `last_error_at`) until a post succeeds.
- Each posted batch is still recorded as `modbot.discord.posted`, subject the channel.

## 6. Health

The Health page's Discord card lists each channel an enabled route sends to that has a problem:

- the bot lacks View Channel, Send Messages or Embed Links there, from `discord_channel`;
- the channel was deleted in Discord;
- the last post was refused, with Discord's reason.

The first two are known before anything is sent, which is M5 §7's "reported at configuration
time"; the settings picker shows the same missing permissions by name.

## 7. Settings page

Discord gets its own top tab, `#discord`, with three cards: **Bot** (token and server id),
**Channels** (the routes) and **Instance announcements** (moved unchanged). Integrations keeps
email and the public address; `#integrations` still opens it.

Channels card: one row per route — channel, name, how many events, on/off switch, Edit, Delete —
and **Add channel**, which opens the editor: channel picker (needs View Channel, Send Messages,
Embed Links), event types by group, then filters. People are found by name or id among Modbot's
stored VRChat profiles; the actor list also offers **Modbot (automatic)**. VRChat roles come from
the group's last-read roles; Modbot roles from the role list.

All of it is under `ManageSettings`, like every other Discord setting. Every create, change and
delete is recorded as `modbot.settings.change`.

## 8. Moving an existing deployment over

One migration:

1. Creates `discord_event_route` and `discord_event_channel`.
2. If `discord_log_channel_id` is set, inserts one enabled route to it with the types the log was
   sending: all nine moderation types when `discord_log_event_types` is null, otherwise the listed
   ones that were on the old closed list, in that list's order. An empty choice becomes a route with
   no types, which sends nothing, as before.
3. If `discord_log_posted_through` is set too, inserts that channel's place with the same value, so
   the first pass after the upgrade neither re-posts nor skips anything.
4. Drops the three old columns.

The integrations save loses `logChannelId` and `logEventTypes`, and the status response loses the
matching fields.
