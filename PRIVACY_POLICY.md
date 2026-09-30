# Privacy policy

Last updated 30 September 2026.

Modbot is a moderation tool for VRChat groups. Anyone can run a copy of it on their own server, and
most copies are run by the moderators of one group, not by us.

That makes two very different questions, so this document answers both.

- **You are in a group whose moderators use Modbot.** Start at
  [If you are in a group that uses Modbot](#if-you-are-in-a-group-that-uses-modbot).
- **You run Modbot yourself.** Start at [If you run Modbot](#if-you-run-modbot).

Everything here describes what the code actually does today, and each statement was checked against
the source code on the date above. Where something is planned but not built, it says so.

## Who is behind Modbot

Modbot is written and operated by **Sarmad Wahab (bin)**.

- Email: **me@bin.moe**
- Web: **https://bin.moe**
- Source: **https://github.com/binn**

The services the project itself runs are `modbot.co` (this site), `docs.modbot.co`, `my.modbot.co`
and `cloud.modbot.co`. Every copy of Modbot that a group runs is someone else's.

---

# If you are in a group that uses Modbot

## Who holds my information?

**The group's operator does — not us.** They chose to run Modbot, they own the server it runs on,
and the database is theirs. We have no access to that database and cannot read it, search it or
delete from it.

A few things a group's Modbot sends to us are described under
[Is any of that sent anywhere else?](#is-any-of-that-sent-anywhere-else). Those copies are ours.

If you want to know what a group holds about you, or want it removed, ask that group's moderators.
We cannot do it for you and we cannot make them.

Modbot is open source, which means you can read exactly what the software is able to record. What a
particular group has switched on is up to them.

## What does Modbot record about me?

It depends on what the group has set up. At most:

**From VRChat**

- Your display name, and the display names, bios and status messages you had before.
- Your VRChat user id, your bio, your status and status message, your pronouns.
- The addresses of your profile picture, your user icon, your profile banner and your current avatar
  picture. Modbot stores the links, not the pictures.
- The date you joined VRChat, your account tags, and the platform you were last on.
- Whether VRChat says you are age-verified, and whether a moderator has marked you 18+ verified by
  hand.
- The group you show on your profile: its id, name and icon.
- VRChat's raw answer about your profile, stored as it arrived, except the fields that say where you
  are, the private note an account can keep about you, and your friend key. Those are removed first.

**About the group**

- That you are a member, when you joined, which roles you have, and whether you left.
- Any moderator notes VRChat holds on your membership.
- Bans and unbans, when they happened and who did them.

**Where you have been**

- Which of the group's instances you joined and left, and when — so how long you were in each.
- When you changed avatar, and the avatar's name.
- Which instance you were in when something happened, and who else was there at the time.

This comes from the Windows client some moderators run, which reads the VRChat log file on their PC
(and from old records a moderator imports). That log names everyone in the instance with them, which
is how a group learns you were there even if no moderator interacted with you. A paired client only
sends its group's server events from instances that belong to a group, never from ones no group owns.

The group's own instance list, which the server reads from VRChat, says which instances are open and
how many people are in them. It does not say who. VRChat's group log adds instance kicks and
instance warnings, with the place they happened.

**Moderation**

- Kicks, bans and unbans done through Modbot, with the reasons picked, the moderator's note and the
  moderator's Modbot username. When moderators act on VRChat directly, VRChat's group log tells
  Modbot who did it, to whom and when. That log carries no reason. Modbot has no warning feature of
  its own.
- Notes a moderator wrote about somebody: the text, who wrote it and when. Taking a note back keeps
  its text in the history.
- Case files: a written record of why someone was banned, with a copy of that person's profile,
  group membership and ban entry as they were at the time.
- Evidence a moderator attached: screenshots, clips, files.
- Flags raised by the group's word lists and AI topics: which rule matched, the words that matched
  (up to 1,000 characters) or, for a picture, its address and a label, and where it was. The AI call
  log keeps the whole prompt and answer for a call that produced a flag, and for a call a person
  started by hand.

**Discord, if the group connected a Discord server**

- **Your messages, in full.** Their text, who you replied to, when you edited them and when you
  deleted them. An edit keeps the old text. A delete keeps the message. For attachments, Modbot
  keeps the file's name, type, size and link, not the file.
- Your Discord account id, username, display name, nickname, avatar link and roles, when you joined
  and left, whether you are boosting the server, and any timeout you were given.
- If you are banned on the Discord server: your username, display name, avatar link and the reason.
- **Voice presence, not voice.** When you joined, moved between and left a voice channel, and how
  many minutes that adds up to. **Modbot's servers do not record or transcribe audio.** Nothing that
  Modbot runs touches a Discord voice stream, and **nothing anybody records on their own PC is ever
  sent to a Modbot server or to us**.

  Two things happen on a moderator's own PC and are described under
  [What does the companion send?](#what-does-the-companion-send), because both are things that PC
  does rather than things we receive. A moderator can switch on **Clips**, and a clip carries the
  sound VRChat is playing — which in an instance is the voices of the people around them. It stays
  on their PC. And a moderator can switch on **Listening**, which opens their microphone to hear a
  few short phrases; nothing it hears is recorded, kept or sent. Both are off unless that person
  turns them on.
- If you linked your Discord and VRChat accounts through Modbot, the link between them, with both
  names.
- If you entered a giveaway, your entry, with your name and account ids.

**If you have an account on the group's Modbot**

- Your username, your email address if you gave one, and your linked Discord and VRChat accounts.
- Every sign-in, with the address you signed in from, and every failed attempt, with the username
  that was typed and the address it came from (never the password). These are kept with the other
  history records.
- What you did: the actions you took and the settings you changed, with your name.

## Is any of that sent anywhere else?

Yes, in these ways. Each is a choice someone made, or a default they can turn off.

**To an AI provider, if the group turned AI on.** AI is off when Modbot is installed. The operator
picks the provider and the model — OpenRouter, xAI, Anthropic, OpenAI, or any compatible endpoint
including one running on their own machine. Before AI can be switched on for the first time, Modbot
shows the operator the list below and records who confirmed it. What is sent depends on the feature:

- **Moderation rules.** The text being checked — a Discord message, a display name, a bio, a status
  line or pronouns — up to 4,000 characters, together with the rule's own instructions. **Your name
  and id are not sent with it.** Word lists are matched on the group's own server and send nothing.
- **The conversation, if the rule is set to read it.** The messages before yours in that channel or
  thread, and the message yours replied to, each with its author's name and cut to 500 characters.
- **Pictures, if the rule is set to look at them** (off unless switched on for that rule). Up to four
  pictures per check: a Discord message's pictures and its author's Discord avatar, or a VRChat
  profile picture, user icon, profile banner and avatar picture. OpenRouter, OpenAI and xAI are given
  a link to each picture. Any other endpoint is given the picture itself: Modbot downloads it first,
  from public addresses only and at 4 MB or smaller. **A Discord avatar link contains the person's
  Discord id.**
- **An opinion on a flag, when a moderator asks for one.** The rule's name and instructions, the
  words or picture that matched and why, and the flagged text, with the messages before it and their
  authors' names when the rule read them.
- **Chat.** When a moderator asks Modbot's assistant a question, the assistant looks things up and
  sends what it found to the provider, with the whole conversation so far. That can include names,
  ids, bios, history, case files, bans, audit log entries, Discord messages and members, flags, who
  was in an instance and the group's figures. After the first reply, the first question and answer
  are sent once more to name the conversation.
- **Insights.** Counts for the period and the one before it, and the busiest worlds and instances by
  world name. No person's name, id or message.
- **Alerts.** What was counted, the period it covered and what is normal, and for the two instance
  alerts the world's name (or its id when Modbot has no name). No person's name, id or message.
- **Testing the connection.** One short message, and a request for the list of models.

Moderators can also connect an AI app of their own (Claude, ChatGPT and others) to Modbot's MCP
server. It is off when Modbot is installed. An app that is connected receives what the Chat lookups
return, as the person who connected it. Where that app sends it is up to the app.

**To Modbot Cloud, from the group's server.** Four things, each described in
[What does my server send to Modbot Cloud?](#what-does-my-server-send-to-modbot-cloud):

- *Open instances.* The instances the group has open that anyone can join, for
  [modbot.co/instances](https://modbot.co/instances). **Nobody is counted and nobody is named.** On
  by default.
- *A usage report every six hours.* Facts about the server and the group: the server's address, the
  Modbot version, the operating system, the group's id, name, description, icon and banner, whether
  Discord and AI moderation are on, and some counts. **No member, no moderation record.** On by
  default.
- *Modbot's own log.* The lines Modbot writes about its own work. **They can carry VRChat user ids
  and can name members and the actions taken on them.** Credentials are removed first; names and ids
  are not. On by default.
- *An email address,* only if someone ticked the box to receive news from Modbot when making their
  account.

All four can be turned off by the operator, or by setting `MODBOT_CLOUD_DISABLED`.

**To Modbot Cloud, from a moderator's Windows client, if the group's moderators run it.** The client
reads the VRChat log on that moderator's PC and sends us the events in it: someone joined an
instance, someone was seen there, someone left, someone changed avatar. Each event carries the
person's VRChat id, their display name, the world and the instance — **including you, if you were in
an instance with that moderator, and including instances that have nothing to do with the group.** No
raw log lines, no chat, no friends list. See
[What does the companion send?](#what-does-the-companion-send) for what it is and how it is turned
off. We keep these events for 365 days.

**Where the operator points it.** Modbot can post events to Discord channels the operator picks, send
emails, call webhooks the operator sets up, and keep a copy of its logs in a Seq server the operator
runs. Those leave the server only where the operator pointed them.

## How long is it kept?

**As long as the operator wants.** Modbot ships with no deletion schedule for moderation records,
presence records or stored Discord messages: they are kept forever unless an operator sets a window.
Case files and evidence are never deleted by any automatic rule.

What does have a schedule when Modbot is installed:

- The AI call log is trimmed to 30 days.
- Modbot's own log is kept 180 days, and never more than two million lines.
- Sent and failed emails in the queue, and old webhook deliveries, are cleared on their own.

## How do I get my information removed?

**Ask the group's moderators.** They control the database.

Being honest about what the software can do for them today: **Modbot has a "Purge a person" tool, and
it does not delete everything about a person.** An administrator types a VRChat or Discord id, sees
what would go and what would stay, types the id again, and it is done. It cannot be undone.

*What a purge erases:* every history record that is about that person — joins, leaves, avatar
changes, bans, notes, and the records of flags raised about them — the counts kept only per person,
the records that say something about them was imported from a file, their Discord messages with
every earlier version of them, their giveaway entries, and their name and ids from past giveaway
draws.

*What a purge keeps:*

- Records where that person was the one acting, such as a moderator's bans. They are somebody else's
  moderation history.
- Their case files and the evidence attached to them.
- The rows Modbot holds about who someone currently is: their VRChat profile, group membership, ban
  list entry, Discord member row and account link. Modbot's next sync can also write a current
  member's profile again.
- Flags themselves, with the words that matched, reviews, and entries in the AI call log.
- Other people's messages that mention or reply to them.
- Modbot's own log, log files, backups, and anything already sent to Modbot Cloud or an AI provider.

A purge leaves one record that it happened: which account did it, when, which platform and how much
was erased, never who it was about.

Other removals are narrower. Evidence cannot be destroyed from Modbot's web app, and a file attached
to a case file cannot be destroyed at all today. An account link can be ended, which marks the link
ended and keeps the row.

If the law where you live gives you a right to have your data erased, that right is against the
group's operator, who decides what is kept. We cannot act on it for you, because we do not hold it.

If the group sent events, logs or reports to Modbot Cloud, those copies are ours, and you can write
to **me@bin.moe** about them.

## Who can see it?

People the operator has given an account on their Modbot, with whatever permissions they gave them.

A few things are open to anyone who can reach the server, without signing in:

- **Who the server is.** The group's name, id, icon and banner, the Modbot version, the server's
  public address if one is set and — unless the operator turned it off in Settings → Server — the
  email address of the group's oldest enabled administrator account that has one. Any website can
  read it.
- **The setup status page the web app reads before sign-in.** It carries the username and display
  name of the VRChat account Modbot signs in as, the address and username of the proxy if one is set,
  the Discord server and channel ids, the instance announcement text, the mail server's host name and
  the group's name and pictures. It carries no password or key.
- **The calendar feed,** to anyone holding the link a moderator made, which lists the group's events.
- **The instances a group chose to publish,** described above, which name nobody.

Everything about people needs an account.

---

# If you run Modbot

This half is about what your own copy sends out, and what you are choosing when you connect
something to it.

## What does my server send to Modbot Cloud?

**Four things.** Each has its own switch, and `MODBOT_CLOUD_DISABLED` turns all four off at once.

| | When | How to turn it off |
|---|---|---|
| **Open instances** | Every five minutes, and when an instance opens or closes | Settings → Server, the **This install** card: *List this group's public instances on modbot.co* |
| **A usage report** | Two minutes after Modbot starts, then every six hours | Settings → Server, the **This install** card: *Send usage report to Modbot Cloud* |
| **Modbot's own log** | About once a minute, in batches | Settings → Server, the **Keep for (days)** card: *Send logs to Modbot Cloud*, then **Save** |
| **An email address** | Once, when someone makes an account and ticks the box | Do not tick it |

All three switches are on when Modbot is installed. The email box is off.

### Open instances

The instances your group has open that anyone can join. That is the feature behind
[modbot.co/instances](https://modbot.co/instances).

| | |
|---|---|
| Your group | Its VRChat id, its name, its icon and its banner |
| Each open public instance | The world's id, name and picture; the instance's VRChat location and join link; the region; when the instance opened |

What it does not carry: any head count, any member count, any person's name or id, your server's
address, any moderation record, and any instance that is not open to everyone. An instance set to
group members, or to members and their friends, is filtered out before the report is built.

A report replaces the whole list, so an instance that closes leaves the page on the next report; a
Modbot that stops reporting drops off within twenty minutes and is forgotten after seven days.
Turning the switch off asks Cloud to drop what it has straight away.

### The usage report

Your server registers with Modbot Cloud the first time it reports, and then sends this:

| | |
|---|---|
| Your server | Its public address, if you set one; the Modbot version; the operating system and processor it runs on, such as `linux-x64` |
| Your group | Its VRChat id, name, description, icon and banner |
| Connections | Whether a Discord bot is connected, and whether AI moderation is on |
| Word lists | The ids of the shared word lists you imported — never their contents |
| Trouble | How many times Modbot stopped sending to VRChat after a rate limit, and how many times VRChat's firewall blocked it, since the last report |

What it does not carry: any count of members or of people, any person's name or id, any moderation
record, any log line, any credential. There is no field for them: the report is a fixed list, and a
test fails when a field is added, so that this page is changed with it.

**What Cloud keeps.** Each report is stored as its own row, with the address it came from, beside
one row for your server that holds the latest values and the address of its last call. Nothing
deletes these rows on a schedule; only a Modbot Cloud administrator removes them. Cloud
administrators can read them.

**The switch.** *Send usage report to Modbot Cloud* is on when Modbot is installed. With it off, no
report is built or sent and the **Last report** row reads *Off*. A Modbot that has not registered
yet stays unregistered, except when somebody presses **Get link code**: that registers it, sending
only the address, the version and the operating system, and then shows a code. The code is made here;
Cloud is told only its SHA-256.

The switch does not turn off the other three. Modbot's log is sent under the id Cloud gave your
server when it registered, so a server that never registered sends no log, and a server Cloud has
forgotten sends none until the usage report is switched back on.

### The log

The lines Modbot writes about its own work, as they are stored on the **Modbot's log** page: the
time, the level, the message, the part of Modbot that wrote it, the exception if there was one, the
values the line carried, and the Modbot version. Information and above, unless `LOG_LEVEL` asks for
more; if it does, the extra lines go too, including database queries.

**Lines can name people.** Modbot writes about its own work and that work is about people, so a line
can carry a VRChat user id and may carry a name or an action taken on someone. Credentials are
removed before a line is stored — a property named like a password, a secret, a token, an API key,
an authorisation header or a cookie, and a bearer token or connection-string password written into a
message. Names and ids are not removed.

What is not in the log page, and so never sent: the requests Modbot makes to VRChat and Discord.
Those go to their own files and to Seq.

Cloud keeps these lines 180 days by default, dropping whole months at a time, so the real limit is
somewhat longer. A Cloud administrator can read them, and so can the Cloud account that claimed your
server with a link code. A server Cloud cannot reach keeps its place and carries on later; if more
than 50,000 lines pile up it gives up on the oldest and counts them.

### Your email address

On Modbot's setup, and when somebody invited to your Modbot makes their account, there is a box
*Receive emails from Modbot about new features and updates*. It is unticked. If it is ticked, your
server sends that account's email address to Cloud, and nothing else about the person. If your
server has registered, the request says which server it came from. Cloud keeps the address, where it
came from, an unsubscribe token, and when it was first and last seen, and no address of the sender.
Only Cloud administrators can read the list. With `MODBOT_CLOUD_DISABLED` the box is not shown.

## What about `MODBOT_CLOUD_DISABLED`?

Set `MODBOT_CLOUD_DISABLED=1` (or `true`, `yes`, `on`) and your server talks to Modbot Cloud not at
all, whatever any setting says. It beats all four switches above, which are greyed out in Settings.
It also stops the word list download, the list of sponsors and contributors the credits page reads,
and the **Get link code** button.

It applies to your server only. **It does not reach the companions paired with it** — a client's
Cloud settings live on the moderator's own PC and your server has no say in them.

**One thing it does not stop: asking what the newest Modbot release is.** That request is one plain
web request for a file. It sends no id, no group, no version and no account, and everyone receives
the same answer from one cached copy. Like any web request it reveals your server's IP address to us.
Your server asks every six hours. Modbot never updates itself; it tells you a newer version exists and
you decide. Turn it off with the **Check for updates** switch in Settings → Server, on the **This
install** card.

## What about my.modbot.co?

`my.modbot.co` is the page that remembers which Modbot deployments you use, so you can pick one.

**Your server never calls it. Your browser does, and it then calls your server.** Modbot opens
`my.modbot.co/register` in a new browser tab for the person who is setting it up (when they finish or
skip the last setup step) and for anybody invited to your Modbot when they make their account. The
Account page has an **Add to my.modbot.co** link for the same page. The link in that tab carries your
Modbot's address and its group's id, name, icon and banner.

When that page, or `my.modbot.co/` or `/go`, is opened with your Modbot's address in it, my.modbot.co
does two things:

- It asks that address for `/api/server`, which answers anyone (see
  [Who can see it?](#who-can-see-it)) and includes the owner's email address unless you turned that
  off in Settings → Server. It passes the group's id, name, icon and banner, and the email address,
  on to Modbot Cloud.
- Modbot Cloud records the address, the visitor's IP address, when it was seen and how many times.
  Only Cloud administrators can read the owner's email address. We have no rule in the code that
  deletes these rows.

The IP address is what makes the list work without an account, which also means everyone behind one
office or household address shares one list. Your server itself sends nothing to my.modbot.co.

**How to avoid it:** turn off *Show the owner's email address* in Settings → Server to keep the email
address out of it. Your Modbot's address is still recorded whenever a browser opens that page with
it, and your group's name and pictures are still readable at `/api/server`. Your browser keeps your
saved Modbots in its own storage, not in a cookie.

## What about the word lists?

Modbot can download the project's shared word lists. It asks `cloud.modbot.co`, for
`termlists/index.json` when somebody opens the list of shared word lists, and for each list you
imported, which it asks for again every six hours. (`my.modbot.co/termlists/` only forwards to
Cloud.) It works whether or not AI is on. A request sends no data about your group and no key. Like
any web request it reveals your server's IP address to us and which list it asked for.

`MODBOT_CLOUD_DISABLED` stops this. Don't import a shared list and nothing is downloaded in the
background.

## What does the companion send?

The Windows client is a separate program a moderator installs on their own PC. **By default it sends
every instance event it reads to Modbot Cloud** — someone joined, someone was seen, someone left,
someone changed avatar — with each person's VRChat id and display name (and, for an avatar change,
the avatar's name), the world and the instance.

Two things worth being blunt about:

- **It is every instance, not just your group's.** A public world the moderator wandered into, a
  friends-only instance, a private one: if the VRChat log names it, the client reports it.
- **It names other players.** People who have never heard of your group end up in these events
  because they were in an instance with someone running the client.

It does not send raw log lines, chat, the friends list, avatar ids, instance secrets, file paths or
machine names. About the PC it sends only the client's version, the time its clock shows and how far
that is from Cloud's. An install is a random id; Cloud does not store the address it registered from
and uses it only to limit how fast one address can register. The instance is sent without its secret
parts.

What it sends a Modbot server it is paired with is the same kind of event, only for instances that
belong to a group, with the same version and clock fields.

**The client's window does not mention this backup, which is why it is written down here.** Its
Events page shows what the client observed and how each event's own group's server took it; it does
not name the backup, show how the backup is getting on, or let anyone filter by it, and a backup
that cannot reach us raises no warning. Two consequences worth stating plainly: **pausing a paired
server does not stop the backup** — pausing stops reporting to that server, and this is a separate
flow — and a moderator who wants to see what the backup did has to look outside the window, in the
client's own log file and in `%APPDATA%\Modbot\sent.jsonl`. That file keeps one line per event and
one more as each place takes it, Modbot Cloud included, and only the last 1,500 lines. Turning the
backup off is below, and it is the only thing that stops it.

**What else the client contacts,** each a plain request that sends nothing about the person:

| | Why | `MODBOT_CLOUD_DISABLED` stops it? |
|---|---|---|
| Modbot Cloud, for the time | To measure how far the PC's clock is out, about every two hours while there is something to send | Yes |
| Modbot Cloud, for the credits | The names of sponsors, early adopters and contributors, every six hours | Yes |
| Modbot Cloud, for updates | See below | No |
| GitHub, for the listening model | About 17 MB, once, when **Listening** is switched on | No |
| GitHub, for the voice model | Once, when the spoken voice is used | No |
| Picture addresses | Icons for the group and credits pictures, fetched straight from their own hosts, which see the PC's IP address | The credits pictures stop; the group's icon does not |

**It does not send a recording of anyone's screen or anyone's voice, ever.** Since 19 September 2026
the client *can* record: if the person using that PC switches **Clips** on in its settings, it keeps
the last two to five minutes of **VRChat's own window, with VRChat's own sound**, so they can save
those minutes as a video file when something happens. That is off unless they switch it on. It works
out whether VRChat is running from VRChat's own log, and stops recording once that log has been
silent for ten minutes. It records nothing outside VRChat's window — while VRChat is closed or they
are working in another program it holds the last picture of VRChat rather than recording what they
moved to — but **anything drawn on top of VRChat's window while it is in front, such as a chat
overlay or a notification, is in the clip**. **The recording never leaves that PC**: not to us, not to Modbot
Cloud, not to the Modbot server they paired with. There is no way for the client to upload one. A
saved clip is a file in a folder on their own PC (by default a *Modbot Clips* folder inside Videos),
and if it ever becomes evidence on a case it is because they chose that file in a browser, the same
as any other attachment.

**A clip has sound in it, and in a VRChat instance that sound is other people's voices.** Until 19
September 2026 a clip was silent, and this policy said so; that is no longer true and this paragraph
is the replacement. The sound in a clip is **VRChat's own**, and **Discord's** if the person using
that PC ticked a second box that is off by default. **Nothing else that PC is playing is ever
recorded** — not music, not a browser, not another chat program, not the operating system's own
sounds — because Windows is asked for one named program's sound rather than for what the speakers
are playing, and the route that hands over the speakers does not exist anywhere in the client. **No
microphone is opened for a clip.** As with everything else on this page about the companion, none of
it leaves that PC.

**It does not send anything your microphone heard, ever.** Since 19 September 2026 the client *can*
open a microphone: if the person using that PC switches **Listening** on in its settings, saying
"Modbot, clip that" out loud saves a clip, which is there for the times they are wearing a headset
and cannot reach a keyboard. That is off unless they switch it on, it only opens the microphone
while VRChat's log shows it running (and for up to ten minutes after), and it shares the microphone
the way a voice chat program does rather than taking it. **Nothing it hears is recorded, kept or
sent**: the sound goes through a speech model that is asked only whether one of a few short phrases
was said — the name "Modbot", and after it "clip that", "clip this", "show overlay" or "hide
overlay" — and is thrown away as it arrives. The client reads which phrase matched and never asks the
model for words. Nothing is written to a file, nothing reaches us, Modbot Cloud or the Modbot server
they paired with. The client shows on screen, in its own window, that the microphone is open for as
long as it is open.

While **Listening** is on, the client also asks Windows which microphones that PC has, so the
person using it can pick between a headset microphone and a desk one. That list is a name and an id
for each device, it is used to fill a list on that PC's own settings screen, and it is never sent
anywhere. With **Listening** off, no microphone is listed or opened. The client does always look at
which speakers and headphones the PC has, for its own notification sounds and spoken announcements;
that list also stays on the PC.

**How to turn the backup off — on that PC, by the person using it.** Your server cannot do it for
them. Either:

- put `"cloud": { "disabled": true }` in that PC's `settings.json`, or
- set `MODBOT_CLOUD_DISABLED=1` in that PC's environment.

The environment variable wins over the file. Restart the client after changing it. Turning it off
also deletes anything the client had queued to send.

Turning it off does **not** stop the client checking for a newer version of itself. That check asks
Modbot Cloud for a short file list. The update library the client uses puts the operating system, the
processor type and the client's own version in the request's address. Cloud's code ignores them and
its own log keeps only the method, path, status and time, so Modbot Cloud does not keep them, but they
are sent. The download comes from GitHub for the project's own releases. Put `"checkForUpdates": false`
in that PC's `settings.json` to stop it.

We keep events sent this way for 365 days by default.

## What else does my Modbot talk to?

Everything below is something you connect or set up. Modbot stores the credentials you give it
encrypted in its own database.

| | What it is for | What Modbot sends |
|---|---|---|
| **VRChat** | The service account Modbot acts as | API calls as that account. VRChat is told who runs this Modbot: the User-Agent carries Modbot's version and **the email address of the group's oldest enabled administrator account that has one** (the developer's address when there is none), and two headers carry the developer's contact email and address |
| **Discord** | The bot, and account linking | Bot calls to Discord for the server you name, and the sign-in exchange when somebody links their accounts |
| **An AI provider** | AI moderation, chat, insights and alerts | See [Is any of that sent anywhere else?](#is-any-of-that-sent-anywhere-else) — off until you switch it on, and you choose the endpoint. While AI is on and the provider is OpenRouter, Modbot also asks `openrouter.ai` once a day for its list of model prices, with no data about your group. Picture downloads for AI come from Discord and VRChat addresses, public ones only |
| **An AI app you connect through MCP** | Using Chat's lookups from another app | What the lookups return, for the person who connected it. Off until you switch MCP on |
| **An SMTP relay** | Sending invitations and reset links | The emails it sends |
| **S3-compatible storage** | Evidence files, if you choose it over disk | The evidence files themselves |
| **Seq** | A durable copy of the logs, if you set `SEQ_URL` | Modbot's own application logs |
| **Webhook addresses you set** | Telling your own tools about events | The events you chose, as signed JSON |
| **Web addresses an AI app names** | When an AI app signs in to MCP by giving an address | Modbot fetches that one public address, follows no redirects, and sends nothing about you |

You are the one handing data to each of these. Their privacy policies are theirs.

## What does modbot.co itself collect?

- **modbot.co** serves pages and an open feed of the instances described above. It sets no cookies and
  runs no analytics script. The one thing kept in your browser is which theme you picked. The
  instances page shows pictures from whatever address each group's server reported, usually
  VRChat's, so your browser contacts those hosts when you open it.
- **docs.modbot.co** serves documentation. Same: no cookies, no analytics script.
- **my.modbot.co** keeps the list of Modbots you saved in your browser's own storage, and sets no
  cookie. What it passes on to Cloud is described under
  [What about my.modbot.co?](#what-about-mymodbotco).
- **cloud.modbot.co** stores:
  - *Companion installs:* a random id, a hash of its secret, the client's version, the word `windows`,
    and when it registered and was last seen. And the events those clients send, for 365 days by
    default, each with the client's version, the time its clock showed and how far out it was.
  - *Servers:* what [the usage report](#the-usage-report) carries, each report as its own row with
    the address it came from. Nothing deletes these on a schedule.
  - *Logs:* what [Modbot's own log](#the-log) carries, for 180 days by default.
  - *The open instances reports,* until they are dropped.
  - *The mailing list:* the addresses described above.
  - *Visits from my.modbot.co:* what [that section](#what-about-mymodbotco) describes, including
    visitor IP addresses.
  - *Accounts:* an email address and a password hash, for people who sign in to Cloud to claim a
    server.

  Cloud's own request log keeps the method, path, status and time of a request. The hosting service
  Cloud runs on may keep logs of its own, which this document cannot speak for. Cloud
  administrators can read the servers, logs, mailing list and visit records; a Cloud account can also
  read the logs of the server it claimed.

## Changes

This document is in the repository at `PRIVACY_POLICY.md` and its history is the history of this
page. Meaningful changes will be noted in the release notes.

## Asking about any of this

Write to **me@bin.moe**.

If your question is about what a particular group holds, ask that group — we cannot see it.
