# Privacy policy

Last updated 17 September 2026.

Modbot is a moderation tool for VRChat groups. Anyone can run a copy of it on their own server, and
most copies are run by the moderators of one group, not by us.

That makes two very different questions, so this document answers both.

- **You are in a group whose moderators use Modbot.** Start at
  [If you are in a group that uses Modbot](#if-you-are-in-a-group-that-uses-modbot).
- **You run Modbot yourself.** Start at [If you run Modbot](#if-you-run-modbot).

Everything here describes what the code actually does today. Where something is planned but not
built, it says so.

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
and the database is theirs. We have no access to it and cannot read it, search it or delete from it.

If you want to know what a group holds about you, or want it removed, ask that group's moderators.
We cannot do it for you and we cannot make them.

Modbot is open source, which means you can read exactly what the software is able to record. What a
particular group has switched on is up to them.

## What does Modbot record about me?

It depends on what the group has set up. At most:

**From VRChat**

- Your display name, and the names you had before.
- Your VRChat user id, your bio, your status and status message, your pronouns.
- Your profile picture and current avatar picture addresses.
- The date you joined VRChat, your account tags, and the platform you were last on.
- Whether VRChat says you are age-verified.
- VRChat's raw answer about your profile, stored as it arrived.

**About the group**

- That you are a member, when you joined, which roles you have, and whether you left.
- Any moderator notes VRChat holds on your membership.
- Bans and unbans, when they happened and who did them.

**Where you have been**

- Which of the group's instances you joined and left, and when — so how long you were in each.
- When you changed avatar, and the avatar's name.
- Which instance you were in when something happened, and who else was there at the time.

This comes from two places: the group's own instance list, and the Windows client some moderators
run, which reads the VRChat log file on their PC. That log names everyone in the instance with them,
which is how a group learns you were there even if no moderator interacted with you.

**Moderation**

- Every warning, kick, ban and unban, with the reason and the moderator's name.
- Case files: a written record of why someone was banned.
- Evidence a moderator attached — screenshots, clips, files.
- Flags raised by the group's AI rules, including the exact text or picture that matched.

**Discord, if the group connected a Discord server**

- **Your messages, in full.** Their text, their attachments, who you replied to, when you edited
  them and when you deleted them. An edit keeps the old text. A delete keeps the message.
- Your Discord account id, username and nickname, your roles, and when you joined.
- **Voice presence, not voice.** When you joined, moved between and left a voice channel, and how
  many minutes that adds up to. **Modbot's servers do not record or transcribe audio.** Nothing that
  Modbot runs touches a Discord voice stream, and **nothing anybody records on their own PC is ever
  sent to a Modbot server or to us**.

  Two things happen on a moderator's own PC and are described under
  [What does the companion send?](#what-does-the-companion-send), because both are things that PC
  does rather than things we receive. A moderator can switch on **Clips**, and a clip carries the
  sound VRChat is playing — which in an instance is the voices of the people around them. It stays
  on their PC. And a moderator can switch on **Listening**, which opens their microphone to hear one
  phrase; nothing it hears is recorded, kept or sent. Both are off unless that person turns them on.
- If you linked your Discord and VRChat accounts through Modbot, the link between them.

## Is any of that sent anywhere else?

Three places, and each is a choice the operator made.

**To an AI provider, if the group turned AI on.** AI is off when Modbot is installed. The operator
picks the provider and the model — OpenRouter, xAI, Anthropic, OpenAI, or any compatible endpoint
including one running on their own machine. What is sent depends on the feature:

- **Moderation rules.** The text being checked — a Discord message, a display name, a bio, a status
  line or pronouns — up to 4,000 characters, together with the rule's own instructions. **Your name
  and id are not sent.** If the rule is set to read the conversation, the messages before yours in
  that channel are sent too, and those carry their authors' names. If the rule is set to look at
  pictures (off unless switched on for that rule), up to four pictures go with it — Discord
  attachments, Discord avatars, VRChat profile pictures.
- **Chat.** When a moderator asks Modbot's assistant a question, the assistant looks things up and
  sends what it found to the provider. That can include names, bios, ban history and who was in a
  instance.
- **Insights and alerts.** Counts and world names only. No person's name, id or message.

Word lists are matched on the group's own server. Nothing is sent to a provider for those.

**To Modbot Cloud, if the group's moderators run the Windows client.** The client reads the VRChat
log on that moderator's PC and sends us the events in it: someone joined an instance, someone was
seen there, someone left, someone changed avatar. Each event carries the person's VRChat id, their
display name, the world and the instance — **including you, if you were in an instance with that
moderator, and including instances that have nothing to do with the group.** No raw log lines, no chat,
no friends list. See [What the companion sends](#what-does-the-companion-send) for what it
is and how it is turned off. We keep these events for 365 days.

**To modbot.co, if the group left the open instances setting on.** A group whose Modbot has this on
sends us the instances it has open **that anyone can join**, so they can be listed on
[modbot.co/instances](https://modbot.co/instances). That is the world, the join link, the region, when the
instance opened, and the group's name and pictures. **Nobody is counted and nobody is named.** An instance
limited to group members, or to members and their friends, is never sent. See
[What does my server send to Modbot Cloud?](#what-does-my-server-send-to-modbot-cloud)

## How long is it kept?

**As long as the operator wants.** Modbot ships with no deletion schedule at all: moderation
records, presence records and stored Discord messages are kept forever unless an operator sets a
window. Case files and evidence are never deleted by any automatic rule.

The one exception is the AI call log, which Modbot trims to 30 days by default.

## How do I get my information removed?

**Ask the group's moderators.** They control the database.

Being honest about what the software can do for them today: **Modbot has no "delete everything about
this person" button.** An operator who wants to erase someone has to do it with database commands.
What is built is narrower — a moderator can destroy a piece of evidence (the file goes, a record
that it existed and who destroyed it stays), and an account link can be ended (the link is marked
ended, the row stays).

If the law where you live gives you a right to have your data erased, that right is against the
group's operator, who decides what is kept. We cannot act on it for you, because we do not hold it.

If the group sent events to Modbot Cloud through a moderator's Windows client, those copies are
ours, and you can write to **me@bin.moe** about them.

## Who can see it?

People the operator has given an account on their Modbot, with whatever permissions they gave them.
Nothing in Modbot is public by default. The one thing a group can choose to publish is its open
public instances, described above, and that names nobody.

---

# If you run Modbot

This half is about what your own copy sends out, and what you are choosing when you connect
something to it.

## What does my server send to Modbot Cloud?

**One thing today: the instances your group has open that anyone can join.** That is the feature behind
[modbot.co/instances](https://modbot.co/instances).

What each report carries:

| | |
|---|---|
| Your group | Its VRChat id, its name, its icon and its banner |
| Each open public instance | The world's id, name and picture; the VRChat join link; the region; when the instance opened |

What it does not carry: any head count, any member count, any person's name or id, your server's
address, any moderation record, and any instance that is not open to everyone. An instance set to group
members, or to members and their friends, is filtered out before the report is built.

A report is sent every five minutes, and again whenever an instance opens or closes. It replaces the
whole list, so an instance that closes leaves the page on the next report; a Modbot that stops reporting
drops off within twenty minutes and is forgotten after seven days.

**How to turn it off.** Settings → Integrations → Modbot Cloud, "List this group's public instances on
modbot.co". It is on when Modbot is installed. Turning it off asks Cloud to drop what it has
straight away.

**Nothing else goes to Cloud from the server.** Usage reporting and analytics are designed but not
built, and nothing in Modbot sends them. Shipping your application logs to Cloud is designed but not
built either — there is no setting for it, because there is nothing to switch. If that changes, this
document changes with it.

## What about `MODBOT_CLOUD_DISABLED`?

Set `MODBOT_CLOUD_DISABLED=1` (or `true`, `yes`, `on`) and your server talks to Modbot Cloud not at
all, whatever any setting says. It beats the open instances setting.

It applies to your server only. **It does not reach the companions paired with it** — a client's
Cloud settings live on the moderator's own PC and your server has no say in them.

**One thing it does not stop: asking what the newest Modbot release is.** That request sends
nothing at all — no id, no group, no version, no account — and everyone receives the same answer
from one cached copy, so there is nothing about you for that variable to protect. Like any web
request it reveals your server's IP address to us. Modbot never updates itself; it tells you a
newer version exists and you decide. Turn it off with the **Check for updates** switch under
**Settings**, **Host & Database**.

## What about my.modbot.co?

`my.modbot.co` is the page that remembers which Modbot deployments you use, so you can pick one.

**Your server never calls it.** What happens is that Modbot offers you a button — during setup, and
on your account page — that opens `my.modbot.co/register?url=<your deployment's address>` **in your
browser**. If you click it, that page records your deployment's address, the address your browser
came from, and when it was seen. That is all it records: no group, no version, no moderation data,
no account.

The IP address is what makes the list work without an account, which also means everyone behind one
office or household address shares one list.

**How to avoid it:** do not click the button. Nothing else contacts `my.modbot.co` except the term
list download below.

## What about the word lists?

If you use Modbot's AI moderation, it can download the project's shared word lists from
`my.modbot.co/termlists/`. That is a plain download: your server asks for a file and receives it. It
sends no data about your group, and no key. Like any web request it reveals your server's IP address
to us and which list it asked for.

This download is a separate setting from `MODBOT_CLOUD_DISABLED`, and is not stopped by it today.
Don't import a shared list and nothing is downloaded.

## What does the companion send?

The Windows client is a separate program a moderator installs on their own PC. **By default it sends
every instance event it reads to Modbot Cloud** — someone joined, someone was seen, someone left,
someone changed avatar — with each person's VRChat id and display name, the world and the instance.

Two things worth being blunt about:

- **It is every instance, not just your group's.** A public world the moderator wandered into, a
  friends-only instance, a private one: if the VRChat log names it, the client reports it.
- **It names other players.** People who have never heard of your group end up in these events
  because they were in an instance with someone running the client.

It does not send raw log lines, chat, the friends list, avatar ids, instance secrets, file paths,
machine names or anything about the PC itself. An install is a random id; we do not store the address
it registered from.

**The client's window does not mention this backup, which is why it is written down here.** Its
Events page shows what the client observed and how each event's own group's server took it; it does
not name the backup, show how the backup is getting on, or let anyone filter by it, and a backup
that cannot reach us raises no warning. Two consequences worth stating plainly: **pausing a paired
server does not stop the backup** — pausing stops reporting to that server, and this is a separate
flow — and a moderator who wants to see what the backup did has to look outside the window, in the
client's own log file and in `%APPDATA%\Modbot\sent.jsonl`, which records every line it wrote.
Turning it off is below, and it is the only thing that stops it.

**It does not send a recording of anyone's screen or anyone's voice, ever.** Since 19 September 2026
the client *can* record: if the person using that PC switches **Clips** on in its settings, it keeps
the last two to five minutes of **VRChat's own window, with VRChat's own sound**, while VRChat is
running, so they can save those minutes as a video file when something happens. That is off unless
they switch it on, it records nothing while VRChat is not running, it records nothing outside
VRChat's window — while they are working in another program it holds the last picture of VRChat
rather than recording what they moved to — and **the recording never leaves that PC**: not to us,
not to Modbot Cloud, not to the Modbot server they paired with. There is no way for the client to
upload one. A saved clip is a file in their own Videos folder, and if it ever becomes evidence on a
case it is because they chose that file in a browser, the same as any other attachment.

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
while VRChat is running, and it shares the microphone the way a voice chat program does rather than
taking it. **Nothing it hears is recorded, kept or sent**: the sound is checked against four short
phrases and thrown away as it arrives. Nothing is written to a file, nothing reaches us, Modbot
Cloud or the Modbot server they paired with, and the thing doing the checking is a small phrase
matcher that has no ability to produce a transcript of anything. The client shows on screen that
the microphone is open for as long as it is open.

**How to turn it off — on that PC, by the person using it.** Your server cannot do it for them.
Either:

- put `"cloud": { "disabled": true }` in that PC's `settings.json`, or
- set `MODBOT_CLOUD_DISABLED=1` in that PC's environment.

The environment variable wins over the file. Turning it off also deletes anything the client had
queued to send.

Turning it off does **not** stop the client checking for a newer version of itself. That check asks
Modbot Cloud, sends nothing about the PC, the install or the version it is on, and is recorded
nowhere; the download comes from GitHub. Put `"checkForUpdates": false` in that PC's
`settings.json` to stop it.

We keep events sent this way for 365 days by default.

## What else does my Modbot talk to?

Everything below is something you connect, with credentials you provide. Modbot stores them
encrypted in its own database.

| | What it is for | What Modbot sends |
|---|---|---|
| **VRChat** | The service account Modbot acts as | API calls as that account, identifying itself in the User-Agent with the project's repository address |
| **Discord** | The bot, and account linking | Bot calls to Discord for the server you name |
| **An AI provider** | AI moderation, chat, insights and alerts | See [Is any of that sent anywhere else?](#is-any-of-that-sent-anywhere-else) — off until you switch it on, and you choose the endpoint |
| **An SMTP relay** | Sending invitations and reset links | The emails it sends |
| **S3-compatible storage** | Evidence files, if you choose it over disk | The evidence files themselves |
| **Seq** | A durable copy of the logs, if you set `SEQ_URL` | Modbot's own application logs |

You are the one handing data to each of these. Their privacy policies are theirs.

## What does modbot.co itself collect?

- **modbot.co** serves pages and an open feed of the instances described above. It sets no cookies and
  runs no analytics script. The one thing kept in your browser is which theme you picked.
- **docs.modbot.co** serves documentation. Same: no cookies, no analytics script.
- **my.modbot.co** stores what [What about my.modbot.co?](#what-about-mymodbotco) describes,
  including visitor IP addresses.
- **cloud.modbot.co** stores companion registrations (a random id, a version, the word
  `windows`) and the events those clients send, for 365 days, and the open instances reports described
  above.

Like any web server, these keep short-lived request logs.

## Changes

This document is in the repository at `PRIVACY_POLICY.md` and its history is the history of this
page. Meaningful changes will be noted in the release notes.

## Asking about any of this

Write to **me@bin.moe**.

If your question is about what a particular group holds, ask that group — we cannot see it.
