# Modbot — Linking Discord and VRChat accounts

- **Date:** 2026-09-15
- **Status:** Built
- **Covers:** M5 §2 (account linking) for group members, the roles given on link, and prompting new
  Discord members to link
- **Depends on:** M5 §2 and §7, accounts and access §4.3 (the bio code), user profile sync §4 (the
  18+ flag), the Discord server's channel and role lists (commit cdd0fd1)

---

## 1. What this adds

The maintainer asked for:

> Discord linking should require OAuth client ID and OAuth Client secret for VRChat Account ->
> Discord and vice versa linking. There should be a setting called "Prompt new joiners to link their
> VRChat account" and it sends them a Discord DM and a configurable backup channel they can ping them
> in to notify them DMs are closed. Set up a setting to select a role to assign to them when accounts
> are linked, and optionally a second role if their VRChat Profile is 18+ and it should give them
> their Discord bot joining link to easily add the bot to their Server.

Staff accounts already link a VRChat account (accounts and access §4.3). This is the same idea for
**any member of the community**, with no Modbot account: a Discord account and a VRChat account are
recorded as the same person once both have been proved.

## 2. What proves each side

M5 §2.1: verification, never guessing.

| Side | Proof |
|---|---|
| Discord | Signing in with Discord: OAuth2 authorization code, `identify` scope only, with a `state` value and PKCE (S256). The Discord user id comes from `GET /users/@me` with the token just issued. |
| VRChat | The bio code of accounts and access §4.3: Modbot gives a `modbot-XXXXXX` code, the person puts it in their VRChat bio, and Modbot reads the profile through `IVRChatGate` on `users.read`, interactive priority, `GetUserWithHttpInfoAsync`. |

The bio check is **one piece of code** (`VRChatBioCheck`), used by the staff link and by this one.
Its rate limit question was answered when `users.read` was added (foundation §4.2.5); no new VRChat
endpoint is used.

The Discord access token is used for that one `users/@me` read, then revoked. It is never stored.

A slash command's caller is not taken as proof of the Discord side for a link. `/link` only answers
with the link page's address, and the page asks for Discord sign-in: one path for every starting
point, so there is one thing to secure.

## 3. The member flow

The page is `/link` in the web app. It needs no Modbot account and works on a deployment whether or
not the person visiting is signed in to Modbot.

**Starting from Discord** — from `/link` in Discord, the DM sent to a new member, or the backup
channel mention:

1. Sign in with Discord.
2. Enter their VRChat profile address or user id (never validated, foundation §3.1.1).
3. Modbot shows a code. They put it in their VRChat bio.
4. Check. The code is found, the link is saved, the roles follow (§6).

**Starting from VRChat** — "vice versa". Somebody who arrives from the VRChat side (a link in the
group's description or a group post, or who simply opens the page) can do the VRChat part first:

1. Enter their VRChat profile address or user id. Modbot shows the code; they put it in their bio.
2. Sign in with Discord.
3. Check.

The two orders share every step and every proof; only the order differs. The one rule that makes
the second order safe: **Check needs Discord sign-in first**, whichever order the person took.
Handing out a code costs nothing; reading a VRChat profile costs budget, and an anonymous visitor
must not be able to spend it. So before Discord sign-in, the VRChat id and code live only in the
page's cookie; on sign-in they move into `discord_link_code` under the Discord user id, where the
check limits count.

### 3.1 Limits

The staff flow's limits, per Discord account: a code lasts thirty minutes, six checks per code, one
check every ten seconds. On top of that, **thirty checks a minute across the whole deployment**,
because anyone can make a Discord account. A check refused by a limit costs no VRChat request. A
429 from VRChat is a cold stop and is reported, never retried.

### 3.2 Cookies

Two cookies, both encrypted with ASP.NET Core Data Protection, `HttpOnly`, `Secure`,
`SameSite=Lax` (the callback is a top-level navigation back from discord.com, which Lax allows):

| Cookie | Holds | Lasts |
|---|---|---|
| `modbot.link-signin` | `state`, PKCE verifier, when it was made | 10 minutes |
| `modbot.link` | Discord user id and name once signed in; the VRChat id and code before sign-in | 1 hour |

Times inside both are stamped from `IModbotClock` and checked against it, not the cookie's own
expiry, which comes from the machine clock (foundation §4.4).

The callback refuses a missing cookie, an old one, or a `state` that does not match (compared in
fixed time). The sign-in cookie is deleted on every callback, used or refused.

### 3.3 One to one

One active link per Discord account and one per VRChat account.

- A VRChat account already linked to a **different** Discord account is refused. The person unlinks
  it from that Discord account, or a moderator does.
- A Discord account that links a **different** VRChat account replaces its old link: the old one is
  ended (kept, §7) and the new one takes over the roles Modbot had given, so they are not removed
  and given back.

## 4. Settings

On the Discord settings tab, as one card ("Account linking"):


| Setting | Stored as | Notes |
|---|---|---|
| OAuth client id | `discord_oauth_client_id` | The application id from the Developer Portal. |
| OAuth client secret | `discord_oauth_client_secret_encrypted` | Encrypted like the bot token; never returned. Forgotten when the client id changes without a new secret, so a secret is only ever sent with the id it was saved for. |
| Redirect URL | — | Shown to copy: `{public address}/api/discord-link/callback`. Built from the public address setting only (accounts and access §4.2). No public address, no redirect URL, and linking is off. |
| Bot invite link | — | Built from the client id (§5). |
| Prompt new joiners to link their VRChat account | `discord_link_prompt_new_members` | Off by default. |
| Backup channel | `discord_link_backup_channel_id` | Needs View Channel and Send Messages. |
| Linked role | `discord_linked_role_id` | Must be a role the bot can assign, when the bot has read the role list. |
| 18+ role | `discord_eighteen_plus_role_id` | Optional. Same rule. |

Linking is available when the client id, the secret and the public address are all set.

## 5. The bot invite link

`https://discord.com/oauth2/authorize?client_id={client id}&scope=bot+applications.commands&permissions={n}`

The client id **is** the application id, so no separate setting. The permissions are the ones the
features that are on need, per M5 §7:

| Permission | Why |
|---|---|
| View Channel, Send Messages, Embed Links, Read Message History | Moderation log, instance cards (fetched by id before rewriting), backup channel mention |
| View Audit Log | Catching up what the bot missed (M5 §5.1) |
| Manage Roles | Only when a linked role or an 18+ role is set |

Adding a feature that needs another permission means one line in `DiscordInvite`.

## 6. Roles

The linked role is given to every linked member. The 18+ role is given to a linked member whose
VRChat record has the **18+ verified flag** (user profile sync §4), and removed when it does not.

The flag rather than the profile's `ageVerificationStatus` as it reads today, on purpose: the flag
is set when any fetched profile shows `18+` (or `ageVerified` true), `hidden` never clears it, and
only a moderator clears it. So the role follows the same answer the person popup shows, and a
moderator who clears the flag takes the role away with it. The bio check records the profile it
fetched, so a person who is 18+ verified has the flag by the time the link is saved.

**One job keeps roles right** (`LinkedRoleService`), rather than code at each place a role could
change. Every link row remembers which role ids Modbot gave and believes the member still holds.
Once a minute, and at once when something changes (a link saved or ended, a member joining), it
compares that with what should be held — the settings' role ids, the flag, whether the link is
still active — and adds or removes the difference. That covers every way the answer can move:

- a link saved or ended;
- profile sync setting the flag, or a moderator clearing it;
- the operator changing or clearing a role setting (the old role is removed, the new one given);
- a member leaving and rejoining the server (Discord takes their roles; the rows are reset on join).

Seeing a member join needs the Server Members intent. If Discord refused it, a linked member who
left and came back gets their roles again by unlinking and linking again on the link page. Taking
away or giving a role the member does or does not already hold changes nothing in Discord, so the
round trip is safe. Proving an existing link a second time resets the rows the same way.

Who is in the server comes from the stored member list, `discord_member`, once the bot has read it
(`discord_server.members_listed_at`). Linking keeps no member list of its own. With the list, a
linked person who is not in the server is not asked about at all, and any roles recorded as given
to them are forgotten, since leaving took them. Before the list has been read, the job finds out
from Discord's Unknown Member as below.


Modbot only removes a role it gave. A role a moderator hands out by hand is not Modbot's to take.

A member who is not in the server (Discord's Unknown Member) is marked and skipped for a day, or
until they join. At most fifty changes a pass. A refused change (the bot lost Manage Roles, or the
role moved above the bot's) is reported on the bot status and tried again next pass.

## 7. Unlinking

M5 §2.4. The member unlinks from the link page; a moderator with **Manage Discord links** unlinks
from the person popup. Either way:

- the row is kept with `unlinked_at` and who ended it;
- the roles Modbot gave are removed by the role job;
- nothing about the person's history changes.

## 8. New members

Members joining are seen through the **Server Members** intent. The intent is privileged, so it has
to be turned on in the Developer Portal.

**Changed 2026-09-15.** This section first said the intent was asked for only while the switch was
on. Storing the server's messages and members (M5 §5.1) now needs the intent on every session, so
the bot always asks for it; turning the switch on or off still reconnects, which asks again for an
intent Discord refused before.

If Discord refuses it (close code 4014), the bot does not stop: it reports that the intent is off in
the Developer Portal and reconnects without it, so the moderation log keeps posting.

On a member joining:

1. Bots are skipped. A member with an active link is not prompted; their role rows are reset so the
   role job gives the roles back.
2. The bot sends a DM: one line and a **Link** button to `{public address}/link`.
3. If Discord says the member does not accept DMs (error 50007) and a backup channel is set, the bot
   posts in that channel, mentioning only that member: one line and the same button.
4. A `discord.link.prompt` fact records how the prompt went.

## 9. Facts

| Type | Subject | Actor | When |
|---|---|---|---|
| `discord.link.create` | VRChat user | the Discord user | A link was saved. Payload: Discord id and name, VRChat display name, which side they started from, the link it replaced if any. |
| `discord.link.remove` | VRChat user | the Discord user, or the Modbot moderator | A link ended. Payload: Discord id, by whom (`member`, `moderator`, `replaced`). |
| `discord.link.role.grant` | Discord user | none (Modbot) | The role job gave a role. Payload: role id and name, `linked` or `18+`, the VRChat user. |
| `discord.link.role.remove` | Discord user | none (Modbot) | The role job removed a role it had given. |
| `discord.link.prompt` | Discord user | none | A new member was sent the prompt. Payload: `dm`, `channel` or `none`, and the error if any. |

The link facts are about the VRChat person, so they are in that person's history and open their
popup. The role and prompt facts are about the Discord account. The prompt facts are plumbing and
take the short retention class; the rest are moderation history.

## 10. The person popup

M5 §5.3. Where a VRChat person is shown, a **Discord** card shows the linked Discord account: name,
id, linked since, and the roles Modbot gave. It needs See profiles. **Unlink** needs Manage Discord
links.

## 11. Permission

`ManageDiscordLinks` (bit 22; bit 21 went to AI Chat): end somebody else's link. Its own flag
rather than part of Manage users (which is about Modbot's own accounts) or Change settings: ending a
link takes a member's roles away, which is a moderation decision about that member. Not added to the
built-in roles.

## 12. Developer Portal steps

1. **OAuth2 → Redirects:** add the redirect URL shown in settings.
2. **OAuth2:** copy the client id and client secret into settings.
3. **Bot → Privileged Gateway Intents:** turn on **Server Members Intent**. The prompt for new
   joiners does nothing without it.
4. Invite the bot with the invite link from settings, so it has Manage Roles.
5. In **Server Settings → Roles**, keep the bot's role above the linked and 18+ roles.

## 13. Most people are not linked

**Unlinked is the normal case.** Most members will never link, many VRChat group members are not in
the Discord server, and many Discord members are not in the VRChat group.

- Nothing outside linking requires a link. Only the linked role and the 18+ role depend on one.
- A Discord-only person and a VRChat-only person each keep their own full history under their own
  platform's id. Linking joins the two into one view; unlinking splits them back into two, and
  neither history loses anything, because no fact is ever rewritten on link or unlink.
- Linking never checks membership on either side. A person in the Discord server but not the VRChat
  group can link; their VRChat account is simply not a group member.

`discord_account_link` is the one place that connects a Discord user id to a VRChat user id.
Other features read it through `AccountLinkLookup` in `Modbot.Core.Discord`:
`ActiveAccountLinks()`, `LinkedVRChatUserIdAsync(discordUserId)` and
`LinkedDiscordUserIdAsync(vrchatUserId)`. A null answer means two separate people.
