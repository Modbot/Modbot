# Modbot — The join gate (Discord)

- **Date:** 2026-10-02
- **Status:** Proposed. Not built. The choices marked **(owner)** in §12 are open.
- **Covers:** a gate for people joining the Discord server: what they see, what they must do to get
  in, the waiting time and the removal, the staff list, join spikes, running beside an older captcha
  bot, permissions, Discord's limits, what is stored, and what happens when Modbot is down
- **Depends on:** Discord account linking (2026-09-15), `/me` (2026-09-30), AI alerts ("People
  joining Discord" watcher), AutoMod's runaway guard (2026-09-17), M5 §7 (bot permissions)
- **Narrows:** the "Not built yet" page's "captcha / join gate" line, which this replaces once built

---

## 1. Why

A real server (the 2026-10-02 review of a 759-member community) runs three things to let people in:
a captcha bot that DMs every joiner, a second bot that @-mentions them in a public `do-this-now`
channel, and a kick after 30 minutes. About half of the joiners in a 38-hour window left again within
the hour. None of it knows the person's VRChat account, and 0 of the 759 are linked to one.

Modbot already has every piece but the gate itself: it sees joins (Server Members intent), DMs a
joiner with a button (`LinkPrompt`), proves a VRChat account (the bio code), knows VRChat's 18+ mark,
gives roles (`LinkedRoleService`), and watches join counts (alerts). The gate joins them up.

## 2. What Discord already does, and why it is not enough

| Discord feature | What it does | Gap |
|---|---|---|
| Verification level ([docs][guild]) | Verified email, account older than 5 min, in the server 10 min, or a verified phone | Says nothing about who the person is |
| Rules Screening ([FAQ][rules]) | Joiner is `pending` (cannot talk, react or DM members) until they accept the rules. When they do, a member update arrives with `pending: false` ([docs][guild]) | One click, no link, no 18+ |
| Onboarding ([blog][onboard]) | Questions that hand out roles and channels | Cannot wait on anything outside Discord |
| Apply to Join ([support][apply]) | A form staff approve by hand before the person is in | Manual for every joiner |
| Raid Protection, Security Actions ([support][raid]) | Detects raids, asks suspicious joiners for a CAPTCHA for an hour, lets staff pause invites and DMs | Only during a detected raid |
| Linked Roles ([docs][linked]) | An app stores up to 5 values per user; a role can require them | The member must find and claim the role themselves; it gates a role, not entry |

So the gate is still Modbot's job, but it **stacks on top** of these. Discord's CAPTCHA and
verification level keep catching scripted accounts; Modbot's gate is about *who* the person is.
A button alone is not a captcha, and this design does not pretend it is.

## 3. The shape: Modbot gives the member role

The operator picks two things on a new **Join gate** card (Settings → Discord):

- **Member role**: the role that opens the server (on the reviewed server, "Villager").
- **Gate channel**: the one channel `@everyone` can see.

The operator sets channel permissions in Discord: `@everyone` sees the gate channel only, the member
role sees the rest. Modbot never rewrites channel permissions; the card checks them and says which
channel is wrong.

Modbot **gives the member role when the steps are done**. It does not put a "waiting" role on people
and take it off. The difference is what happens when Modbot is down (§10): with Modbot giving the
member role, new joiners wait and nobody is let in unchecked. **(owner, Q3)**

Everyone who was in the server when the gate was switched on is left alone. A member who already
holds the member role is never gated.

## 4. What a new member sees

1. They join. If Rules Screening is on, Discord shows its rules first; Modbot starts only once
   `pending` is false.
2. **A DM** from the bot (the one `LinkPrompt` sends today, widened): the server's name and a
   **Get in** button. DMs closed (error 50007): one mention of only that member in the gate channel,
   the same as the linking backup channel. No other pings. No public "do this now" roll call.
3. **The gate channel** holds one message Modbot posts and keeps: the operator's own text (their
   rules) and a **Get in** button.
4. **Get in** answers privately (ephemeral) with the steps still to do, each a label and a button:
   - **Rules**: **I agree**
   - **VRChat account**: **Link** (opens the existing `/link` page)
   - **18+**: shows "Not verified on VRChat" until VRChat's 18+ mark is on the linked account
5. When the last step is done: the member role, and a private "You're in." Rejoining a server they
   already passed, with an active link: straight in, no steps.

No explanation text in any of these (CLAUDE.md "controls, not explanations"); the operator's own
text in the gate message is theirs.

## 5. The steps

| Step | Done when | Cost |
|---|---|---|
| **I agree** | The button is pressed | Nothing |
| **Link VRChat** | An active `discord_account_link` exists for the Discord id | The existing bio-code check and its limits (30 checks a minute across the deployment) |
| **18+ on VRChat** | The linked VRChat account has the 18+ verified flag (the same flag the 18+ role follows) | Nothing new: the bio check already records the profile |

The operator ticks which steps the gate needs. **I agree** is always on; 18+ needs the link step.
**(owner, Q1)**

## 6. Not finishing

Three settings: **Remind after** (hours), **Remove after** (hours, or **Never**), and the gate's
on/off. Default proposal: one reminder DM at half time, removal at 24 hours. **(owner, Q2)**

- Removal is a **kick**, never a ban: they can come back. The kick carries `X-Audit-Log-Reason`
  "Did not finish the join gate" ([docs][guild]).
- **Never removed when the hold-up is Modbot's**: the bot cannot give the member role, VRChat checks
  are failing (a 429 cold stop, VRChat down), or the bot was offline for part of their time. Their
  clock restarts when the fault clears.
- **Runaway guard**, as AutoMod's: at most 10 removals a minute, and if removals in an hour pass a
  share of joins that is far above the server's normal, removals stop and staff get an alert.

## 7. What staff see

- **Discord → Members**, a new filter **At the gate**: joined, time left, steps done, and two
  actions: **Let in** (gives the member role; needs a new permission, Manage the join gate) and
  **Remove**.
- **Giving the member role by hand in Discord counts as let in.** Modbot sees the role change and
  closes the gate entry with "let in by hand".
- New event types for event channels and the audit log: **Passed the join gate**, **Let in at the
  join gate**, **Removed at the join gate**, **Reminded at the join gate**.
- A count on **Health** (Discord bot card): how many are waiting, and any reason the gate is stuck.

## 8. Join spikes

The trigger is the existing **People joining Discord** alert watcher, which already compares the
hour with the same hour on past days (no fixed number, as the alerts design requires). When it
fires, the gate can, on top of the alert **(owner, Q4)**:

| Option | What happens | Needs |
|---|---|---|
| A | Alert only | nothing new |
| B | **Hold**: nobody new gets the member role by themselves until staff lift the hold; removals keep running | nothing new |
| C | B, and pause the server's invites through Discord's incident actions (at most 24 hours) | **Manage Server**, a big permission |

Staff lift a hold with one button on the Join gate card or on the alert.

## 9. Running beside the old captcha bot

1. **Watch only** (like AutoMod's trial): Modbot records who would have passed, been reminded and
   been removed, and does nothing in Discord: no DM, no role, no kick. The old bot keeps working.
   The At the gate list shows "would have" outcomes beside what the old bot did.
2. **On**: point the gate at the same member role the old bot gives, then turn the old bot's
   verification off in its own dashboard. Remove its "Needs to do Captcha" role and the `do-this-now`
   ping bot.
3. If anything goes wrong, turning the gate off stops Modbot at once; nobody is stripped of a role.

Two bots must never both kick: Watch only is the only mode allowed while another bot does the job.

## 10. When things fail

| Failure | What happens |
|---|---|
| Modbot or the bot is down | New joiners stay in the gate channel. Nobody is let in, nobody is removed. On reconnect Modbot reads the member list, finds joiners without the member role who joined after the gate was switched on, and starts their clock from the reconnect. |
| Bot loses Manage Roles, or the member role moves above the bot's | No one can pass; removals stop; Health says why; staff can still let people in by hand in Discord |
| VRChat checks failing | Link and 18+ steps cannot finish; removals stop until checks work again |
| Gate channel deleted or unreadable | Health says so; DMs still carry the button |
| Discord intent for members refused | The gate cannot see joins: the card refuses to turn on |

Chosen: **people wait rather than walk in** when Modbot is down, and **nobody is removed for
Modbot's fault**. **(owner, Q3)**

## 11. Permissions, Discord limits, privacy

**Bot permissions** (one line each in `DiscordInvite.PermissionsFor`, only while the gate is on):
Manage Roles (bot's role above the member role), View Channel, Send Messages, Embed Links in the gate
channel, Kick Members only while **Remove after** is not **Never**, Manage Server only for option C.
**Intent:** Server Members, already asked for.

**Discord limits:**

- 50 requests a second for the whole bot, plus per-route limits read from the response headers;
  10,000 refused requests (401, 403, 429) in 10 minutes gets the bot's address blocked ([docs][rates]).
  So a 403 stops that action and is reported, never retried in a loop.
- A button press must be answered within 3 seconds; the steps reply is built from stored rows only.
- DMs to strangers are what spam looks like: one DM on join and one reminder, never more.
- **To check during the build** (not confirmed in Discord's docs): whether a role can be given to a
  `pending` member, and whether a member held by verification level "in the server 10 minutes" can
  press buttons. Test both on the test server before relying on them.

**What is stored** (new table `discord_gate_entry`, one row per join while the gate is on): Discord
id, joined at, each step's time, the outcome (passed, let in by whom, removed, left), and reminder
time. Nothing about the person beyond what linking already keeps. Facts as in §7; reminders take the
short retention class. The privacy policy, `WhatModbotKeeps` and the `/me` docs list gain one line:
"Whether you passed the Discord server's join gate, and when". `/me` shows nothing new.

## 12. Open for the owner

1. **Q1** Which steps can an operator require: agree only, agree + link, agree + link + 18+?
2. **Q2** Remove people who do not finish, and after how long, or never?
3. **Q3** When Modbot is down: new joiners wait (Modbot gives the member role) or walk in (Modbot
   takes a waiting role away)?
4. **Q4** On a join spike: alert, hold, or hold and pause invites?

## 13. What else changes

- Docs: a new `discord/join-gate.mdx`; the captcha line comes off "Not built yet"; `bot-setup.mdx`
  permission table; privacy page and `WhatModbotKeeps` (the docs test enforces the match).
- `LinkPrompt` is widened rather than duplicated: one DM on join, with **Get in** when the gate is on
  and **Link VRChat account** when only linking is.

[guild]: https://docs.discord.com/developers/resources/guild
[rates]: https://docs.discord.com/developers/topics/rate-limits
[linked]: https://docs.discord.com/developers/resources/application-role-connection-metadata
[rules]: https://support.discord.com/hc/en-us/articles/1500000466882-Rules-Screening-FAQ
[apply]: https://support.discord.com/hc/en-us/articles/29729107418519-Server-Member-Applications
[raid]: https://support.discord.com/hc/en-us/articles/17439993574167-Activity-Alerts-Security-Actions
[onboard]: https://discord.com/blog/community-onboarding-welcome-your-new-members
