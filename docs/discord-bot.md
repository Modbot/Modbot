# The Discord bot

Modbot can run a small Discord bot for your community's server. It does two things:

- **Posts events to the channels you choose** — bans, kicks, joins, role changes, case files and
  more, as they happen, each with a link to the person in Modbot. Each channel gets the events you
  pick for it, and can be narrowed to certain people or roles.
- **Answers three slash commands** — `/lookup`, `/recent` and `/modbot` — so a moderator in Discord
  can check somebody's record without opening the web app.

It also keeps a copy of your server's messages — reading back each channel's history when it is
first set up — so the server's activity can be charted. It does nothing until you give it a token.
Without one, the rest of Modbot is unaffected.

It can also **link members' Discord and VRChat accounts** and give linked members a role — see
[Account linking](#account-linking) below. Ban sync, role sync and auto-invites are not part of
this bot yet.

## 1. Create the application

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and click
   **New Application**. Name it whatever you like — "Modbot" is fine.
2. Under **Bot**, click **Reset Token** and copy the token. You will paste it into Modbot in a
   moment; the portal will not show it again.
3. Still under **Bot**, under **Privileged Gateway Intents**, switch **on**:
   - **Server Members Intent** — for joins, leaves and role changes.
   - **Message Content Intent** — for the text of messages.

   Leave **Presence Intent** off. If either of the two is off, the bot connects without it and
   **Settings → Health** names the one to switch on.

## 2. Invite it to your server

Under **OAuth2 → URL Generator**:

- Scopes: **`bot`** and **`applications.commands`**. The second is what lets the slash commands
  appear; without it the bot connects but nobody can run anything.
- Bot permissions: **View Channels**, **Send Messages**, **Embed Links**, **Read Message History**
  and **View Audit Log**. Read Message History lets it read back each channel's messages; View Audit
  Log lets it see who banned, kicked or timed somebody out, including while it was offline. Do not
  give it Administrator.

Open the generated link, pick your server, and confirm.

## 3. Find the server id

Turn on **Developer Mode** in Discord (User Settings → Advanced), then right-click your server's
icon → **Copy Server ID**. This is the **guild id**.

## 4. Enter them in Modbot

In Modbot, go to **Settings → Discord**. On the **Bot** card, fill in:

| Setting | What it is |
|---|---|
| **Bot token** | From step 1. Encrypted when stored; never shown again. Leave the field blank later to keep the one you have; save it empty to turn the bot off. |
| **Guild id** | Your server's id. The slash commands are registered on this server only. |

You also need a **Public address** (**Settings → Integrations**) if you want the links in each
post and in `/lookup` to work. Modbot builds those links from that setting and nothing else.

Save. The bot connects within about ten seconds; it also notices any later change to these
settings by itself, so nothing needs restarting. **Settings → Health** shows whether it is
connected, how many commands it registered, when it last posted, and the last problem it hit.

## 5. Choose where events go

On the **Channels** card, press **Add channel**:

1. **Channel** — pick it by name. A channel where the bot is missing View Channel, Send Messages
   or Embed Links is marked with what is missing; fix that in Discord's channel settings.
2. **Events** — tick the events to send, one by one or a whole group at a time.
3. **Filters**, all optional. Leave them empty to send every event of the ticked kinds.

| Filter | Sends an event only when |
|---|---|
| **Happened to** | it happened to one of these people. |
| **Happened to someone with these VRChat roles** | the person it happened to holds one of these group roles now. |
| **Done by** | one of these people did it. Tick **Modbot (automatic)** to include events nobody did, such as what Modbot noticed on its own. |
| **Done by someone with these VRChat roles** | the person who did it holds one of these group roles now. |
| **Done by someone with these Modbot roles** | the Modbot account of the person who did it holds one of these roles. |

When several filters are set, an event has to pass all of them. Inside one filter, any of the
people or roles listed is enough. A moderator's actions in Modbot count as done by the VRChat
account they linked.

You can add as many channels as you like, and more than one rule for the same channel. If two
rules for one channel both match an event, the channel still gets it once.

Each row has an on/off switch, **Edit** and **Delete**.

**Posting starts from the moment a channel is turned on.** Nothing that happened before then is
posted, so adding a channel, or turning one back on, never replays your group's history into
Discord. A channel the bot cannot post in waits and tries again every thirty seconds without
holding up the others; **Settings → Health** lists it with the reason.

Sign-ins, failed sign-ins, password changes, reset links and contact-detail changes are never
posted to Discord, whatever is ticked, and are not offered.

Instance announcements have their own card on the same tab.

## 6. Link moderators' Discord accounts

The bot only answers people it can match to a Modbot account. Each moderator adds their **Discord
user id** on their own **Account** page in Modbot (or an administrator adds it on the Users page).
Right-click your own name in Discord → **Copy User ID** to find it.

Anyone else who runs a command is told to link their account first, and nothing more.

## The commands

All replies are visible only to the person who asked.

| Command | What it does | Needs |
|---|---|---|
| `/lookup user:<id or name>` | The stored profile: display name, whether they have been seen as 18+ verified and since when, when the profile was last refreshed, ban status, counts of bans, kicks and warns, and the last few moderation events. Names are matched against what Modbot has already stored, never searched on VRChat. | *See profiles* |
| `/recent [count]` | The latest moderation events, newest first. 1 to 25; 10 by default. | *See the audit log* |
| `/modbot` | Whether the bot is connected, whether it is posting events to any channel, and the web app's address. | a linked account |

Permissions are the same ones the web app uses, held through Modbot roles. An administrator may use
everything.

## What gets recorded

Every command, answered or refused, is written to Modbot's operational log as *Discord command
used* — who ran it, which command, and which person it looked up. Each batch posted to a channel
is recorded as *Posted to the Discord log channel*. Adding, changing or deleting a channel on the
**Channels** card is recorded as *Settings changed*, with who did it. None of these carries the
token.

## Account linking

Members of your community can prove which Discord account and which VRChat account are theirs, on
a page Modbot serves at `/link`. They sign in with Discord, put a short code Modbot gives them in
their VRChat bio, and press **Check**. They can do the VRChat part first if they came from VRChat.
Nobody needs a Modbot account for this.

Once linked, the bot gives them the **linked role**, and also the **18+ role** if their VRChat
record is marked 18+ verified. The 18+ role is taken away again if a moderator clears that mark.
Unlinking — by the member on the same page, or by a moderator with *Manage Discord links* from the
person's popup — takes away the roles Modbot gave. The record of the link stays.

### Set it up

1. In the Developer Portal, open **OAuth2**. Copy the **Client ID** and **Client Secret**.
2. In Modbot, go to **Settings → Discord → Account linking**, paste them, and save. A
   **Public address** must be set under **Settings → Integrations**.
3. Copy the **Redirect URL** Modbot now shows, and add it under **OAuth2 → Redirects** in the
   Developer Portal.
4. Pick a **Linked role** and, if you want one, an **18+ role**. The bot needs **Manage Roles**, and
   its own role must sit above both in **Server Settings → Roles**. The **Bot invite link** in the
   same card asks for Manage Roles once a role is set; use it to re-invite the bot if needed.

Members can reach the page with `/link` in Discord, which anyone can run.

### Prompt new joiners

Switch on **Prompt new joiners to link their VRChat account** to have the bot send each person who
joins your server a direct message with a button to the link page. People who are already linked
are not asked.

This needs the **Server Members Intent** from step 1. If it is off, the bot keeps working without
it, and **Settings → Health** says so.

If somebody does not accept direct messages, the bot mentions them in the **Backup channel**
instead. Leave it empty to skip that. The bot needs View Channel and Send Messages there.

## If something is wrong

Check **Settings → Health** first; the bot's card says what it last ran into.

| It says | What to do |
|---|---|
| Discord rejected the bot token | Reset the token in the Developer Portal and paste the new one. The bot stops trying until the settings change. |
| The bot is not in that server | The guild id is wrong, or the invite was never completed. Check both. |
| A channel with Missing … or a refusal beside it | Give the bot View Channel, Send Messages and Embed Links in that channel, or pick another. |
| Commands do not appear in Discord | The invite link was missing the `applications.commands` scope. Re-invite with it; nothing else needs changing. |
| Reconnecting | The connection dropped and is being retried. This usually clears by itself within a minute. |
| Intents off in the Developer Portal | Switch the named intents on under **Bot → Privileged Gateway Intents**. Until then the bot connects without them; it asks for them again when Modbot restarts or the Discord settings change. |
| Could not change a linked member's role | Give the bot Manage Roles and move its role above the linked and 18+ roles. |
| Sign in with Discord fails on the link page | Check the Redirect URL in the Developer Portal matches the one in Modbot exactly, and that the client secret is current. |

