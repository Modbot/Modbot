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
  the channel: the channel's own overwrite for @everyone if it sets View Channels, else @everyone's
  server-wide permission; Administrator on @everyone sees everything. Kept as
  `discord_channel.everyone_can_view`, null until the bot reads the channel again.

Without a last message the row says why instead: **Can't read** (the bot lacks View Channels or Read
Message History; anything stored from before is not shown, since "quiet since then" would be wrong),
**Still reading** (a read-back for the channel or one of its threads is unfinished), else **No
messages**.

**Order:** the API sends no messages, then longest quiet, then still reading, then can't read;
Discord's order between equals. The page re-sorts that list itself (see Sorting).

**Sorting:** every column sorts, ascending and descending, by clicking its header (Channel, Category,
Last message, Quiet for), kept in the address as `?sort=quiet-desc`. On a phone, where the table
becomes a list, a Sort box and a direction button do the same. Rows with nothing to sort by
(Last message: none, still reading or can't read; Quiet for: still reading or can't read; Category:
none) always come last, whichever way it runs; a channel with no messages is the longest quiet. The rules
are pure functions in `lib/discordTidyUp.ts`.

**Filtering:** the page's filter bar, so the choices live in the address (`f=`) and combine:

| Filter | Choices |
|---|---|
| Seen by | is / is not, one or more roles (see 3.1) |
| Quiet for | at least 7, 30, 60, 90, 180 or 365 days |
| Category | each category, and No category |
| Type | text, announcement, forum |
| Status | No messages, Still reading, Can't read |
| Staff-only | yes / no; offered only with Change settings |
| Name | search box |

### 3.1 Seen by

The question is what someone holding only that role would see. The rule (`DiscordTidyUp.RoleCanView`)
combines @everyone's and the role's server-wide permissions; Administrator on either sees everything.
Otherwise the channel's overwrite for @everyone applies (Deny hides, Allow shows), then the role's
(same). A synced channel already holds its category's overwrites on Discord's side, so there is no
separate inheritance step. Member-specific overwrites are not counted: the question is about the role.

Modbot used to keep only whether @everyone can see a channel, so the overwrites are now kept as
`discord_channel.view_overwrites`, a plain text column listing the roles whose View Channel is Allow
and the roles whose View Channel is Deny. It is filled from the gateway cache when the bot reads a
channel, so nothing new is asked of Discord. It is empty until the bot next signs in or resumes, and
a channel or role not yet read matches neither "is" nor "is not": Seen by narrows to nothing until
then, rather than guessing.

**Who sees staff-only names:** only someone who also has **Change settings** (`ManageSettings`, the
permission that guards every Discord setting). A staff-only channel's name can say what the staff talk
about, and See analytics is given to people who are not staff. Everyone else gets the public channels
and one line, "N staff-only channels", with nothing about them but the count (`staffOnlyHidden`);
the Staff-only filter changes nothing for them.

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
- `GET /api/discord/reports/quiet-channels?seenBy=&notSeenBy=` → `QuietChannelList`. Both
  parameters take role ids and repeat for more: `seenBy` keeps channels at least one of the roles can
  see, `notSeenBy` channels none of them can see. The answer also carries `roles` and `categories`
  for the filter pickers, and each row a `categoryId`. `hideStaffOnly` is gone: the Staff-only filter
  on the page replaces it (the hidden-staff-channel count and the Change settings rule are
  unchanged). Everything else (quiet for, category, type, status, name, sort) is done by the page on
  the one list.

Both under See analytics. The rules are a pure class (`DiscordTidyUp`) so they are tested without a
database.

## 5. Left for later

- Acting on a row (delete a role, archive a channel) from Modbot. Read only on purpose for now.
- A "Remove ..." opt-out role mark, which the review also named: there is no rule that tells one apart
  from any other role without reading its name.
