# The Discord bot

Modbot can run a small Discord bot for your community's server. It does two things:

- **Posts moderation events to a channel** — bans, unbans, kicks, warns, join-request rejections
  and role changes, as they happen, each with a link to the person in Modbot.
- **Answers three slash commands** — `/lookup`, `/recent` and `/modbot` — so a moderator in Discord
  can check somebody's record without opening the web app.

It reads no messages, needs no privileged intents, and does nothing until you give it a token.
Without one, the rest of Modbot is unaffected.

Ban sync, role sync, account linking and auto-invites are not part of this bot yet.

## 1. Create the application

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and click
   **New Application**. Name it whatever you like — "Modbot" is fine.
2. Under **Bot**, click **Reset Token** and copy the token. You will paste it into Modbot in a
   moment; the portal will not show it again.
3. Still under **Bot**, leave every **Privileged Gateway Intent** switched **off**. The bot uses
   only the `Guilds` intent, which is not privileged. It never asks for message content.

## 2. Invite it to your server

Under **OAuth2 → URL Generator**:

- Scopes: **`bot`** and **`applications.commands`**. The second is what lets the slash commands
  appear; without it the bot connects but nobody can run anything.
- Bot permissions: **View Channels**, **Send Messages**, **Embed Links**. That is all it needs.
  Do not give it Administrator.

Open the generated link, pick your server, and confirm.

## 3. Find the ids

Turn on **Developer Mode** in Discord (User Settings → Advanced). Then:

- Right-click your server's icon → **Copy Server ID**. This is the **guild id**.
- Right-click the channel you want moderation events posted in → **Copy Channel ID**. This is the
  **log channel id**. Make sure the bot can see that channel and send messages in it.

## 4. Enter them in Modbot

In Modbot, go to **Settings → Integrations → Discord** and fill in:

| Setting | What it is |
|---|---|
| **Bot token** | From step 1. Encrypted when stored; never shown again. Leave the field blank later to keep the one you have; save it empty to turn the bot off. |
| **Guild id** | Your server's id. The slash commands are registered on this server only. |
| **Post moderation events to this channel** | The channel id from step 3, or blank to post nothing. |
| **Which events to post** | Tick the kinds you want. Everything is ticked by default. |

You also need a **Public address** (further down the same page) if you want the links in each post
and in `/lookup` to work. Modbot builds those links from that setting and nothing else.

Save. The bot connects within about ten seconds; it also notices any later change to these
settings by itself, so nothing needs restarting. **Settings → Health** shows whether it is
connected, how many commands it registered, when it last posted, and the last problem it hit.

**Posting starts from the moment you save the channel.** Nothing that happened before then is
posted, so turning the channel on — or switching to a different one — never replays your group's
history into Discord.

## 5. Link moderators' Discord accounts

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
| `/modbot` | Whether the bot is connected, whether it is posting to a channel, and the web app's address. | a linked account |

Permissions are the same ones the web app uses, held through Modbot roles. An administrator may use
everything.

## What gets recorded

Every command, answered or refused, is written to Modbot's operational log as *Discord command
used* — who ran it, which command, and which person it looked up. Each batch posted to the channel
is recorded as *Posted to the Discord log channel*. Neither carries the token, and nothing from
sign-ins, reset links or account changes is ever posted to Discord, whatever event types are ticked.

## If something is wrong

Check **Settings → Health** first; the bot's card says what it last ran into.

| It says | What to do |
|---|---|
| Discord rejected the bot token | Reset the token in the Developer Portal and paste the new one. The bot stops trying until the settings change. |
| The bot is not in that server | The guild id is wrong, or the invite was never completed. Check both. |
| The bot may not post in that channel | Give it View Channel, Send Messages and Embed Links in that channel, or pick another. |
| Commands do not appear in Discord | The invite link was missing the `applications.commands` scope. Re-invite with it; nothing else needs changing. |
| Reconnecting | The connection dropped and is being retried. This usually clears by itself within a minute. |
