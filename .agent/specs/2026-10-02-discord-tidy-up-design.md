# Modbot — Tidying up a Discord server: the Roles and Channels tabs

- **Date:** 2026-10-02
- **Status:** Built
- **Covers:** two read-only reports on the Discord page: every role with its member count and what is
  worth a look about it, and the text, announcement and forum channels by how long they have been
  quiet
- **Depends on:** the server index (`discord_server`, `discord_channel`, `discord_role`, M5 §7), the
  Discord member list, the message store and its read-back (M5 §5.1)
- **Comes from:** the Thy Kingdom review (2026-10-02), ideas 1 and 2: a big server with 238 roles,
  dozens nobody holds, and a dozen channels quiet for one to five years, and no way to see which
  without clicking through Discord's settings role by role

---

## 1. Where they live

Two tabs in the Discord page's row, **Channels** and **Roles**, between Events and Members, where
Discord's own server column has "Channels & Roles". `/discord/channels` and `/discord/roles`, off the
sidebar and lighting Discord, like Members. Both are read only: nothing on them renames, archives or
deletes anything. Tidying up is done in Discord.

**Permission: See analytics** (`ViewAnalytics`), the one the Discord page itself needs. Both reports
are counts and dates about the server -- how many hold a role, when a channel was last written in --
and name nobody, which is what the Overview tab shows too. See members would have tied a report with
no people in it to the permission for lists of people.

**Nothing is asked of Discord.** Both are built from what the bot already keeps, so they cost no
request and need no rate limit of their own.

## 2. Roles

Every role but @everyone and roles since deleted, with the number of current members holding it,
counted from `discord_member.roles` the same way the member list's role filter counts. Until the
member list has been read whole (`members_listed_at`), counts are null and nothing is marked "No
members": a partial list would mark half the server's roles empty.

| Mark | Rule |
|---|---|
| **No members** | the count is 0 |
| **Same name as another role** | another role's name matches, ignoring case and spaces at either end |
| **Same permissions and colour as another role** | another role has the same permission bits and the same colour |
| **Bot role** | `managed`: Discord owns it for a bot or an integration (bot roles, booster, subscriber roles) |

**Same permissions and colour leaves plain roles out:** a role with no colour and either no
permissions or exactly @everyone's. Self-picked roles (pronouns, time zones, hobbies) are all like
that, so every one matched every other and buried the pairs that matter. Bot roles are left out of it
too (they are marked already, and thirty bots with the same permissions are not thirty duplicates),
and so is a role whose permissions are not read yet. The other roles behind the mark are named on
hover.

**Order:** most marks first, then fewest members, then Discord's own order.

This needed the role's permissions, which the index did not keep: `discord_role.permissions`, the
bits as Discord sends them in a `bigint`, only ever compared whole.

## 3. Channels

Text, announcement and forum channels not deleted, each with:

- **Last message:** the newest message Modbot has stored for the channel or any of its threads
  (`discord_message.channel_id` is the parent for a thread's messages), one look down the existing
  `(channel_id, sent_at desc)` index per channel. Deleted messages count: somebody wrote there then.
- **Quiet for:** how long ago, against the server's clock, in Modbot's units (y, mth, d, h, m, s; no
  weeks), as every other age is written.
- **Staff-only:** @everyone cannot see it, Discord's lock beside the name. Worked out when the bot reads
  the channel: the channel's own overwrite for @everyone if it sets View Channel, else @everyone's
  server-wide permission; Administrator on @everyone sees everything. Kept as
  `discord_channel.everyone_can_view`, null until the bot reads the channel again.

Without a last message the row says why instead: **Can't read** (the bot lacks View Channel or Read
Message History; anything stored from before is not shown, since "quiet since then" would be wrong),
**Still reading** (a read-back for the channel or one of its threads is unfinished), else **No
messages**.

**Order:** no messages, then longest quiet, then still reading, then can't read; Discord's order
between equals. **Filter:** All channels, or Hide staff-only (`hideStaffOnly=true`).

**Who sees staff-only names:** only someone who also has **Change settings** (`ManageSettings`, the
permission that guards every Discord setting). A staff-only channel's name can say what the staff talk
about, and See analytics is given to people who are not staff. Everyone else gets the public channels
and one line, "N staff-only channels", with nothing about them but the count (`staffOnlyHidden`);
Hide staff-only changes nothing for them.

**Still reading** is looked up in one query for every unfinished read-back in the server (a thread's
counts for its channel), not one look per channel.

### Why the message store and not `last_message_id`

Discord's channel objects carry `last_message_id`, a snowflake whose top bits are its time, and that
was the first idea. Discord.Net keeps it in its internal models and never exposes it, so reading it
would mean a request of Modbot's own to Discord, outside the library's rate limiting. The message
store already holds the same answer for free: the read-back reads every channel and thread the bot
can see back to its first message, catch-up fills the time the bot was away, and new messages arrive
live. It also counts replies in forum posts, which `last_message_id` on a forum does not (it is the
newest post started). The cost is that a channel the bot cannot read has no time, and that message
retention, when an operator sets one, makes a channel quiet for longer than it read as "No messages".

## 4. API

- `GET /api/discord/reports/roles` → `RoleReport`
- `GET /api/discord/reports/quiet-channels?hideStaffOnly=` → `QuietChannelList`

Both under See analytics. The rules are a pure class (`DiscordTidyUp`) so they are tested without a
database.

## 5. Left for later

- Acting on a row (delete a role, archive a channel) from Modbot. Read only on purpose for now.
- A "Remove ..." opt-out role mark, which the review also named: there is no rule that tells one apart
  from any other role without reading its name.
