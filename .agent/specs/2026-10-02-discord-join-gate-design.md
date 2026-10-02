# Modbot — The join gate (Discord)

- **Date:** 2026-10-02
- **Status:** Built with this document. The owner answered the four open choices on 2026-10-02
  (§12); everything else was the builder's call and is open to the owner.
- **Covers:** a gate for people joining the Discord server: what they see, what they must do to get
  in, the waiting time, the warning and the removal, the staff list, join spikes, running beside an
  older captcha bot, permissions, Discord's limits, what is stored, and what happens when Modbot is
  down
- **Depends on:** Discord account linking (2026-09-15), `/me` (2026-09-30), AI alerts ("People
  joining Discord" watcher), M5 §7 (bot permissions), API conventions §8 (Discord member actions)
- **Related:** the 2026-10-02 review of a real server, which listed a join gate among what Modbot
  does not do. No docs page named it as missing, so none changes for that

---

## 1. Why

A real server (the 2026-10-02 review of a 759-member community) runs three things to let people in:
a captcha bot that DMs every joiner, a second bot that @-mentions them in a public `do-this-now`
channel, and a kick after 30 minutes. About half of the joiners in a 38-hour window left again within
the hour. None of it knows the person's VRChat account, and 0 of the 759 are linked to one.

Modbot already has every piece but the gate itself: it sees joins (Server Members intent), DMs a
joiner with a button (`LinkPrompt`), proves a VRChat account (the bio code), knows VRChat's 18+ mark,
gives roles, and watches join counts (alerts). The gate joins them up.

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

The two support pages refused to be fetched while this was written; what is said about them comes
from Discord's own search snippets. The developer docs were read directly.

## 3. The shape: Modbot gives the member role

A **Join gate** card on Settings → Discord holds:

| Setting | Stored as | Notes |
|---|---|---|
| Join gate | `discord_gate_mode` | **Off**, **Watch only** or **On**. Off by default |
| Member role | `discord_gate_member_role_id` | The role that opens the server (on the reviewed server, "Villager"). Must be one the bot can assign |
| Gate channel | `discord_gate_channel_id` | The one channel `@everyone` can see |
| Message | `discord_gate_message` | The operator's own words (their rules), above the button in the gate channel |
| Link VRChat account | `discord_gate_needs_link` | A step, off by default |
| 18+ on VRChat | `discord_gate_needs_eighteen_plus` | A step, off by default; needs the link step |
| Remove after | `discord_gate_remove_after_minutes` | **Never**, 30 minutes, 1, 6, 12 or 24 hours, 2, 3 or 7 days. Never by default |
| Hold new joiners on a join spike | `discord_gate_hold_on_spike` | Automatic protection, off by default (§8) |
| Allow pausing invites | `discord_gate_pause_invites` | Off by default; needs Manage Server (§8) |

The operator sets channel permissions in Discord: `@everyone` sees the gate channel only, the member
role sees the rest. Modbot never rewrites channel permissions.

Modbot **gives the member role when the steps are done**. It does not put a "waiting" role on people
and take it off. So when Modbot is down, new joiners wait and nobody is let in unchecked (§10).

**Who is gated.** Only people who join while the gate is on, and who do not hold the member role.
Everyone already in the server is left alone. Somebody who joined before the gate was on and presses
**Get in** anyway is gated from that moment.

## 4. What a new member sees

1. They join. If Rules Screening is on, Discord shows its rules first. Modbot gives the member role
   only once Discord says `pending` is false.
2. **A DM** from the bot: "Welcome to **server**!" and a **Get in** button. DMs closed (error
   50007): one mention of only that member in the gate channel, with the same button. No other
   pings. The link prompt is not sent as well while the gate is on: one message, not two.
3. **The gate channel** holds one message Modbot posts and keeps up to date: the operator's message
   and a **Get in** button. Changing the message or the channel rewrites or moves it.
4. **Get in** answers privately with the steps, each a line and, while not done, a button:
   - **Rules**: **I agree**
   - **VRChat account**: **Link VRChat account** (opens the existing `/link` page)
   - **18+**: "Not verified on VRChat" until VRChat's 18+ mark is on the linked account
   - and **Check** to read the steps again after linking on the page.
5. When the last step is done: the member role, and a private "You're in." The steps are also
   checked once a minute, so somebody who links on the page and never comes back still gets in.
6. Back again after passing once, with the steps still done (an active link, for a gate that needs
   one): straight in, with no steps to repeat.

No explanation text in any of these (CLAUDE.md "controls, not explanations"); the operator's message
is theirs.

## 5. The steps

| Step | Done when | Cost |
|---|---|---|
| **I agree** | The button is pressed | Nothing |
| **Link VRChat account** | An active `discord_account_link` exists for the Discord id | The existing bio-code check and its limits |
| **18+ on VRChat** | The linked VRChat account has the 18+ verified flag (the one the 18+ role follows) | Nothing new |

Each server picks its steps (owner, Q1: C). **I agree** is always one of them; 18+ needs the link
step. A server that is not set up for linking cannot save the link step.

## 6. Not finishing

**Remove after** is the operator's (owner, Q2), with **Never** among the choices.

- **One warning, halfway.** At half the removal time the bot DMs once: "You have not finished
  getting in to **server**. Get in by <time>, or you will be removed." with **Get in**. DMs closed:
  one mention in the gate channel. No warning when removal is Never.
- Removal is a **kick**, never a ban: they can come back. Discord's audit log says "Modbot: did not
  finish the join gate".
- **Time counts only while the hold-up is theirs.** Each open entry keeps the minutes it has been
  counted. A pass adds the minutes since the last pass, at most two, and only when:
  - the bot is connected (a pass needs a ready session at all);
  - the bot can give the member role: no refusal in the last ten minutes, and the bot's stored role
    list does not say it cannot assign it (a refusal nobody tried again since ages out, so a fixed
    role is found without anybody having to finish first);
  - VRChat is answering, for a gate with the link step (no rate-limit stop in the last hour);
  - linking is set up, for a gate with the link step;
  - the person has steps left to do (somebody done and waiting on a hold is waiting on staff).
  A Modbot down for a day counts as two minutes. Nobody is removed while Modbot is the reason.
- **Runaway guard.** At most 10 removals in a pass, and none once 30 people were removed in the last
  hour; the gate then says so on Health and stops removing until the hour has passed.

## 7. What staff see

- **Discord → Members** opens with an **At the gate** card while the gate is not off: who is
  waiting, joined when, which steps are done, when they will be removed (or "would be", in Watch
  only), and **Let in** and **Remove** (both need **Manage the join gate**, a new permission; seeing
  the card needs See members). The card also says whether new joiners are held, with **Hold new
  joiners**, **Lift hold** and, when allowed, **Pause invites**.
- **Giving the member role by hand in Discord counts as let in.** Modbot sees the role arrive and
  closes the entry as "Let in in Discord".
- Facts, which event channels and the audit log can show: **Passed the join gate**, **Let in at the
  join gate**, **Removed at the join gate**, **Warned at the join gate**, **New joiners held**,
  **Hold lifted**, **Discord invites paused**.
- Health (the Discord bot card) says when the gate cannot do its job: the member role cannot be
  given, the gate message cannot be posted, removals stopped by the runaway guard.

## 8. Join spikes

The trigger is the existing **People joining Discord** alert watcher, which compares the hour with the
same hour on past days (no fixed number, as the alerts design requires). The alert itself is posted
as before, and the gate keeps handling people as usual (owner, Q4). Protection is not automatic
unless the operator asks for it:

- **Hold new joiners on a join spike** on: when the watcher fires, new joiners are held — nobody gets
  the member role by themselves until staff lift the hold. With **Allow pausing invites** on as
  well, Modbot also pauses the server's invites through Discord's incident actions for 24 hours, the
  most Discord allows ([docs][guild]).
- Off (the default): the alert's Discord post carries **Hold new joiners**, and **Pause invites**
  when allowed. Staff press them.
- **Lift hold**: on the bot's reply after a hold in Discord, and on the At the gate card. The Join
  gate card says whether new joiners are held. Lifting lets everyone who is done in on the next
  pass. Changing the gate's mode ends a hold too.

A press in Discord is taken as the Modbot account that proved that Discord account (accounts and
access §4.6), and needs **Manage the join gate**; anybody else is told so. Every hold, lift and pause
is a fact naming who did it, or "join spike" when it was automatic. Removals keep running during a
hold for people with steps left, as they would without one.

## 9. Running beside the old captcha bot

1. **Watch only**: Modbot records who joined, which steps they did not do, when it would have warned
   and removed them, and whether they got the member role from somebody else — and does nothing in
   Discord: no DM, no gate message, no role, no kick, no hold. The old bot keeps working. The At the
   gate card shows "Would remove" beside each person.
2. **On**: point the gate at the same member role the old bot gives, then turn the old bot's
   verification off in its own dashboard. Remove its "Needs to do Captcha" role and the `do-this-now`
   ping bot.
3. Turning the gate off stops Modbot at once; nobody loses a role. Entries made under one mode are
   closed when the mode changes, so Watch only never turns into removals.

## 10. When things fail

| Failure | What happens |
|---|---|
| Modbot or the bot is down | New joiners stay in the gate channel. Nobody is let in, nobody is removed, and the time does not count (§6). On reconnect a pass reads the stored member list: anybody who joined after the gate went on and has no member role and no entry gets one, from that moment |
| Bot loses Manage Roles, or the member role moves above the bot's | No one can pass; time stops counting; Health says why; staff can still let people in by hand in Discord |
| VRChat checks failing | Time stops counting for a gate with the link step |
| Gate channel deleted or unreadable | Health says so; DMs still carry the button |
| Server Members intent refused | The gate cannot see joins or roles: Health says the intent is off, which it already does |

Chosen by the owner (Q3: A): people wait rather than walk in, and staff giving the role by hand
counts as let in.

## 11. Permissions, Discord limits, privacy

**Bot permissions** (`DiscordInvite.PermissionsFor`, only while the gate is on): Manage Roles (the
bot's role above the member role); View Channel, Send Messages, Embed Links and Read Message History
in the gate channel (the message is fetched by id before each rewrite); Kick Members while **Remove
after** is not Never; Manage Server only while **Allow pausing invites** is on. **Intent:** Server
Members, already asked for.

**Modbot permission:** **Manage the join gate** (bit 51): let somebody in, remove them at the gate,
hold, lift the hold, pause invites. Not in the built-in Moderator or Viewer roles; Administrator holds
it. Changing the gate's settings is Change settings, as for every other card.

**Discord limits:**

- 50 requests a second for the whole bot, plus per-route limits; 10,000 refused requests (401, 403,
  429) in 10 minutes gets the bot's address blocked ([docs][rates]). A refused role change stops time
  counting and is not retried until the next pass; at most 10 removals and 25 role changes a pass.
- A button press must be answered within 3 seconds; the steps are read from stored rows only.
- DMs to strangers are what spam looks like: one DM on join and one warning, never more.
- **Checked during the test, not from docs:** whether a role can be given to a `pending` member (the
  gate waits for `pending` to be false either way), and whether a member held by verification level
  "in the server 10 minutes" can press buttons.

**What is stored** (`discord_gate_entry`, one row per person per time through the gate): Discord id
and name, when they joined, Watch only or not, when they agreed, minutes counted, when they were
warned, how it ended (passed, let in, let in in Discord, removed, left, gate changed) and when, who let
them in or removed them, the last problem. Nothing about the person beyond what linking already keeps.
The warning facts take the short retention class; the rest are moderation history.
`WhatModbotKeeps` and the `/me` docs gain one line, "Whether you got in through the server's join
gate, and when". The privacy policy on modbot.co lives outside this repository and needs the same
line.

## 12. The owner's answers (2026-10-02)

1. **Q1 Steps:** C. Each server picks: agree, plus optionally link VRChat, plus optionally 18+.
   Thy Kingdom would start on agree only.
2. **Q2 Not finishing:** the removal time is a per-server setting with Never; a warning before
   removal (halfway, §6); nobody removed while Modbot is the reason.
3. **Q3 Modbot down:** A. Joiners wait; nobody let in or removed; a role given by hand counts.
4. **Q4 Join spike:** alert as before; protection is a setting, off by default; otherwise buttons on
   the alert; a clear Lift hold; the buttons need a Modbot permission and are recorded.

## 13. What else changes

- Docs: `discord/join-gate.mdx`; the `bot-setup.mdx` permission table; `roles-and-permissions.mdx`;
  `WhatModbotKeeps` and the matching docs list.
- `LinkPrompt` stays as it is while the gate is off. While the gate is on, the gate's DM replaces it.

[guild]: https://docs.discord.com/developers/resources/guild
[rates]: https://docs.discord.com/developers/topics/rate-limits
[linked]: https://docs.discord.com/developers/resources/application-role-connection-metadata
[rules]: https://support.discord.com/hc/en-us/articles/1500000466882-Rules-Screening-FAQ
[apply]: https://support.discord.com/hc/en-us/articles/29729107418519-Server-Member-Applications
[raid]: https://support.discord.com/hc/en-us/articles/17439993574167-Activity-Alerts-Security-Actions
[onboard]: https://discord.com/blog/community-onboarding-welcome-your-new-members
