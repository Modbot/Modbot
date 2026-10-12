# Modbot — Smart welcome (Discord)

- **Date:** 2026-10-12
- **Status:** Built with this document. The owner approved the shape on 2026-10-12 (§2); everything else
  was the builder's call and is open to the owner.
- **Covers:** three next steps sent to a new Discord member, picked by rules the admins set, when the
  member finishes Discord's onboarding (or joins, on a server with none)
- **Depends on:** the join gate (2026-10-02), the link prompt (Discord account linking, 2026-09-15),
  the Server Members intent (already requested), member roles as stored (`discord_member.roles`)
- **Does not change:** the join gate's own welcome message (`JoinGate.WelcomeAsync`); the two are
  separate and a member may get both

---

## 1. Why

Discord's onboarding hands out roles from the answers a new member gives, and then leaves them in a
server with no hint of what to do first. Admins know which first step suits which answer ("if they
picked *Creator*, show them the avatar channel"). Modbot already stores each member's roles, so it
can pick the steps itself.

## 2. What was agreed

| # | Decision |
|---|---|
| Where | Admins choose a direct message from Modbot, a post in a welcome channel, or both |
| Rules | "If they hold this role, show this step." A step is a short text and an optional link. Up to 3 steps are sent, in the admins' order |
| Defaults | When fewer than 3 rules match, up to 3 default steps fill the rest, in their order |
| When | Once per member, when Discord says they finished onboarding. A server with no onboarding: when they join, or when the join gate lets them in |
| Default | Off |

Words: the page and the message say "welcome" and "steps". "Onboarding" appears only where it names
Discord's own feature; Modbot's first-run setup is a different thing that shares the word.

## 3. When a welcome is sent

Three events can lead to a welcome. All of them end in one place (`SmartWelcome.SendAsync`), which
claims the member first (§5) so that however the events race, a member is welcomed once.

| Event | Source | Condition |
|---|---|---|
| Finished onboarding | `GuildMemberUpdated`: the member's flags (`GuildUserFlags.CompletedOnboarding`) gained that bit | Feature on, member joined after it was switched on (§3.1), not a bot |
| Joined, server has no onboarding | `UserJoined`, after the join gate and the link prompt have run | Feature on, not a bot, the server's onboarding is off (§3.2), and the join gate did not take them |
| Let in by the join gate | `GuildMemberUpdated` for a member marked waiting | The gate is no longer holding them: it is not on, or the member now holds its member role |

The gateway intents already include `GuildMembers`, which member updates need; nothing is added. The
`CompletedOnboarding` bit rides on the same `GUILD_MEMBER_UPDATE` event that carries roles.

### 3.1 Who counts

The library does not keep every member in memory (`AlwaysDownloadUsers` is off), so the "before"
half of a member update is often missing and "the bit just turned on" cannot be read from it. Two
guards replace it:

- If the "before" half is there and already had the bit, the update is ignored. This is only a
  saving of one query.
- A member must have **joined after the feature was switched on** (`DiscordWelcomeStartedAt`, set when
  the switch goes from off to on, cleared when it goes off). Without this, switching the feature
  on would welcome every old member the next time they changed a nickname. It is the same idea as
  the join gate's "who is gated" clock. A member whose join time Discord did not send is not welcomed
  by the onboarding path.

### 3.2 Does the server have onboarding

One request (`GET /guilds/{id}/onboarding`, `IGuild.GetOnboardingAsync`), made on a join and only
when the feature is on. `enabled: false`, or a failed request, count as **no onboarding**: a
member then gets the welcome on joining. A failed request is therefore a welcome that may come
before the member has picked roles, never a welcome that is never sent, and it is logged. The
once-per-member claim means the finish-onboarding event cannot send a second one.

### 3.3 Joining while the join gate is on

`JoinGate.JoinedAsync` returns true when it took the member and sent its own message. Then the
welcome is not sent on joining but written down as **waiting**. The next member update that shows the
gate is done with them (§3 table) sends it. If the gate is turned off while someone waits, the
next update of that member sends it. Nothing is added to the join gate itself.

## 4. Picking the steps

1. Walk the rules in their order. A rule matches when the member's roles (from the event, which has
   them as they are after the change) hold the rule's role. Each matching rule adds its step.
2. Stop at 3 steps.
3. If fewer than 3, walk the defaults in their order and add them until there are 3.
4. No steps at all (nothing matched, no defaults): nothing is sent, the member is **not** claimed, and
   no fact is written. Saving the settings with the feature on needs at least one rule or default, so
   this is only a rule's role that nobody holds.

A role that was deleted in Discord simply never matches.

## 5. Sending

```
Welcome to **Server name**! Your next steps:
1. Say hi in the lobby
2. Pick your avatar world
```

- A step with a link also gets a link button (`Step 1`, `Step 2`, ...) under the message. A button
  needs an https address, which is checked when the rule is saved, and the gateway drops anything
  else.
- The text is the admin's own and is sent as written. Nothing in it can ping anybody: direct
  messages use no mentions at all, and the channel post pings only the member.
- **Direct message** uses `SendDirectMessageAsync`. **Channel** uses `MentionAsync` on the welcome
  channel (the member is pinged, nobody else). **Both** does both.
- When the direct message fails because the member's DMs are closed, the channel post still goes
  out if the setting includes it; if it does not, the failure is recorded and nothing else happens.
  No failure raises an exception out of the handler. Nothing is retried: a welcome is not worth a
  second DM to someone whose DMs just failed.

### 5.1 Once per member

Table `discord_welcome` holds one row per member: `state` is `waiting` or `sent`. Sending first
**claims** the member: an update of a waiting row to `sent`, or an insert of a `sent` row, and the
primary key (the Discord user id) makes the insert fail for the second of two racing events. Only
the caller that claimed sends. The claim is made before the send, so a crash between the two loses a
welcome rather than sending it twice, which is the better way round for a direct message.

A member who leaves and comes back is not welcomed a second time.

## 6. What is recorded

One fact per welcome, as the link prompt does it: `discord.welcome.send`, subject the Discord
account, no actor, `Source = Modbot`. Payload: `via` (`dm`, `channel`, `both` or `none`), `steps`
(how many), and `error` when something failed. `none` means nothing could be delivered.
Category Operational (plumbing, like the link prompt) and short retention. Saving the settings
writes the usual "settings changed" entry (`discordWelcome`).

## 7. Settings

Settings → Discord → **Welcome steps** card, saved with its own button to its own endpoint
(`/api/settings/discord-welcome`, `ManageSettings`).

| Control | Notes |
|---|---|
| Switch | Welcome new members. Off by default |
| Send as | Direct message, Channel, Both |
| Channel | A channel picker; needed when Send as includes a channel; the bot must be able to post |
| Rules | A list: role picker, text (80 characters, a button label's limit), link (optional). Up to 25. Ordered; up/down buttons |
| Default steps | A list: text, link. Up to 3. Ordered |

No explanatory text on the card: a label names a control (repo rule).

Stored: four columns on `settings` (`discord_welcome_enabled`, `_via`, `_channel_id`, `_started_at`)
and a table `discord_welcome_step` (`kind` is `rule` or `default`, `role_id` for rules, `text`, `link`,
`position`). Saving replaces all steps in one transaction.

## 8. Limits and failure

| Case | What happens |
|---|---|
| Member's DMs closed | The post goes to the channel when the setting has one; else a `none` fact with the error |
| Channel missing, or the bot cannot post there | A `none`/`dm` fact with the error; the page's channel picker already warns about permissions |
| Discord down | Same, with the error; no retry |
| Member left before the welcome | Discord refuses the DM; recorded as an error |
| Onboarding roles arrive in a separate update after the flag | The welcome picks steps from the roles it had. **Not verified against live Discord**: localhost has no Discord. If it happens, the fix is a short wait before picking |
| Two Modbots in one server | Each Modbot answers only the server in its settings, as the rest of the bot does |

## 9. What is not built

- Retrying a failed welcome, or sending it again by hand.
- Welcoming members who joined before the feature was switched on.
- Rules on anything but a role (a question answer is a role in Discord already).
- Erasing a member's `discord_welcome` row on request: it holds a Discord id and two times, like the
  join gate's rows, and the erase tool is built around VRChat accounts.
