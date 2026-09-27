# Settings: a UI and UX review

- **Date:** 2026-09-27
- **Looked at:** every Settings tab and sub-tab on a local self-hosted install (2026.9.0, dense, desk
  width 1028px, dark), plus the code under `src/Modbot.Web/src/pages/Settings.tsx` and
  `src/Modbot.Web/src/components/settings/`. Phone width was checked at 390px for Server and Discord.
  VR was not checked in a headset; the VR notes below come from the code.
- **Reader in mind:** a VRChat group owner who is not technical, opening Settings to change one
  thing.
- **Follows:** `2026-09-25-ux-review.md` findings 17-19. The tab names in finding 19 have been fixed
  (IAM → People and roles, VRChat Service Account → Modbot's VRChat login, Host & Database →
  Server). The panel and field words it lists are mostly still there.

## Summary

Settings has grown one tab per feature, and it shows. There are 14 top-level tabs, and three of them
open a second row of tabs, which makes 27 places to look. They sit in one flat row with no grouping.
On a phone only 3 fit and nothing says there are more. The same tab mixes four ways of saving. A
dozen actions that remove things or reach outside Modbot run on one click. The words are clean at
the tab level, but inside the panels they are still an engineer's: Guild id, OAuth client secret,
Use TLS, SOCKS5, Pacing floor, Jitter, Range reads, tokens.

## 1. Finding a setting

### 1.1 Fourteen tabs in one row, in no order an owner would guess

The order today is Server, People and roles, Modbot's VRChat login, Integrations, Discord,
Moderation, AutoMod, Evidence, Sync, AI, API, VRChat proxy, Auto-invites, Purge a person
(`Settings.tsx:29-44`).

- The first tab is **Server**: storage charts, retention and machine usage. It is the tab an owner
  needs least, and it opens by default.
- Things that belong together are far apart:
  - **Moderation** (tab 6) and **Purge a person** (tab 14).
  - **Discord** (tab 5) and the Discord **channel** for AI alerts (AI → Alerts).
  - **Modbot's VRChat login** (tab 3) and **VRChat proxy** (tab 12).
- Owner-level tabs sit next to developer tools: **API** has keys, webhooks and a WebSocket tester,
  and **VRChat proxy** has a raw request playground.
- At desk width about 9 tabs show. The other 5 need a sideways scroll on a thin scrollbar
  (`ui/tabs.tsx:42`).
- At phone width, 3 tabs show: Server, People and roles, and Modbot's VRChat login.

### 1.2 Tabs inside tabs

These tabs open a second tab row:

- **AI** has 7: Base, Insights, Alerts, Chat, MCP, Limits and Call log.
- **API** has 3: Keys, Webhooks and Events.
- **People and roles** has 2: Users and Roles.

AI alone has more pages than most apps' whole settings. Three of them are not settings at all:

- Insights and Alerts are features that happen to use AI.
- The Call log is a log.

### 1.3 Same name, different thing (and the reverse)

| Word | Where it appears | What it means there |
|---|---|---|
| Modbot Cloud | Server | Signing in to Modbot Cloud |
| Modbot Cloud | Integrations | Listing public instances on modbot.co |
| Alerts | Integrations ("Health alerts") | Emails when a service breaks |
| Alerts | AI → Alerts | Discord posts about the group |
| Limits | AI → Chat (a card) | Limits on a chat reply |
| Limits | AI → Limits (a sub-tab) | Money limits |
| Addresses | API → Webhooks | Whether private addresses are allowed |
| Addresses | API → Events | Web addresses to copy |
| proxy | VRChat login ("Use a SOCKS5 proxy") | Going out through another address |
| proxy | VRChat proxy tab | Letting other apps reach VRChat through Modbot |

### 1.4 The Sync tab has nothing to set

The Sync tab is read-only and opens with "These cannot be changed here yet."
(`SyncSection.tsx:37`). It holds 26 timing numbers. It belongs with Health, or behind a
disclosure, not as a Settings tab.

### 1.5 Credits hides inside Server

"Credits" is a lone button between the Deployment and Public address panels
(`DataSection.tsx:155`). It floats outside any panel. It is also in the footer of every page, so
this button is not needed.

## 2. Words

The tab names are fixed. The words inside the panels are the next layer. None of these would mean
anything to a volunteer:

| Tab | Now | Plainer |
|---|---|---|
| Server | Facts recorded, Per fact, facts/day | Records, Per record, a day |
| Server | Retention; Moderation facts / Presence facts (days) | Keep for (days); Moderation / Who was where |
| Server | Version commit, Release branch, Host | (fold into one "Version" line) |
| Server | cloud.modbot.co answered 404. | Could not check for updates. |
| VRChat login | Two-factor secret | Authenticator key |
| VRChat login | Use a SOCKS5 proxy; `socks5://host:port` | Go out through another address |
| Integrations | Host, Port, Use TLS | Mail server, Port, Encrypted |
| Integrations | Off (MODBOT_CLOUD_DISABLED) | Off on this install |
| Discord | Guild id | Server id |
| Discord | Bot token | Bot password (Discord calls it a token) |
| Discord | OAuth client id / secret, Redirect URL | App id / App secret, Return address |
| Discord | Manage Roles, Ban Members, Kick Members | Manage roles, ban members, kick members |
| Moderation | Repeat offender threshold | Counts as a repeat offender after |
| Moderation | Nuisance rank | (name what it counts) |
| Evidence | S3-compatible object storage, Bucket, Endpoint, Region, Path-style URLs | Kept as the cloud names them, behind "Cloud storage" |
| Evidence | Store marker, Delivery, Range reads | Drop from the panel; Health covers "is it working" |
| Evidence | Deployment (MB) | Whole install (MB) |
| Sync | Pacing floor, Back-off per quiet poll, Jitter, Re-read window | Move to Health (1.4) |
| AI | Base | Connection |
| AI | Endpoint, Fallback model | Address, Backup model |
| AI | Reply length (tokens), Tool calls per reply | Longest reply, Steps per reply |
| AI | Top Chat users this month | Top chat users this month |
| API | Signing secret, Roll secret, Cursor, Long polling, WebSocket | Developer words; fine only if API moves under a "For developers" heading |
| VRChat proxy | Base URL, Method, Path, Body | As above |
| Purge a person | Id | VRChat id / Discord id (follow the Account choice) |

Explanatory text that the house rule bans is still in a few places:

- "Facts past the retention window are destroyed permanently." (`DataSection.tsx:296`)
- "You have plenty of storage for the foreseeable future" (`DataSection.tsx:134`)
- "This estimate is based on 2 days of data." (under the storage chart)
- "Disabling ends their sessions straight away." (`Users.tsx:470`)
- "Allows everything. Cannot be changed." (`Roles.tsx:185`)
- "Per report and deployment: 0 means no limit." (`EvidenceSection.tsx:386`)
- The Evidence headline says the same thing twice: "No evidence store has been configured." and
  then "No evidence store has been configured yet."

"0 keeps forever." is a borderline case. It tells you how to use a field, but a Forever tick box or
a placeholder would say it without a sentence.

## 3. Saving

Four patterns are in use, often inside one tab:

1. **Save in the panel footer.** Most panels.
2. **Instant switch or row action.** Ban reasons, Discord channels, Updates, public instances,
   Allow private addresses, and the paired-role rows.
3. **Saves as you type, to this browser only.** "Cost per GB/mo" and "Disk size" on Server.
4. **A verb that saves.** "Verify and store", "Test and save", and "Test connection", which
   saves the proxy.

The same control acts differently from one panel to the next:

- The **Public address** panel has a Save button. The switch in the same panel ("Show the owner's
  email address") saves at once (`DataSection.tsx:572`).
- **AutoMod on** waits for Save. The switch on each rule row, one panel down, saves at once
  (`AutoModSection.tsx:118-140, 230`).
- **Role and ban sync** has "Save sync". Its paired-role rows ("Who decides", On, the bin) save at
  once (`SyncCard.tsx:282-299`).
- **AI → Chat** draws the same Save in three panels, and each one saves all three
  (`AiChatSettings.tsx:109, 127, 135`).

The button's name differs as well: Save, Save retention, Save logs, Save integrations, Save
alerts, Save account linking, Save sync, Save limits, Test and save, and Verify and store.

**Rule to adopt:**

- A panel of fields has one **Save** in its footer, and every control in that panel waits for it.
- A list whose rows each have an on/off switch saves each row at once, and that list has no Save
  button.
- The button always says "Save".

## 4. Dangerous actions without a confirm

Good today:

- Delete account asks you to type the name.
- Purge a person asks you to type the id.
- Deleting a Discord channel, term list or AI topic takes two steps.
- Turning AI on asks for consent.

One click, no confirm:

| Action | Where | What it takes away |
|---|---|---|
| Revoke | API → Keys (`ApiKeysPanel.tsx:122`) | Breaks every app using the key |
| Delete | API → Webhooks (`WebhooksPanel.tsx:204`) | The webhook and its delivery history |
| Roll secret | API → Webhooks (`:181-189`) | The receiver's verification, until it is updated |
| Remove key | AI → Base (`AiBaseSettings.tsx:183`) | AI stops |
| Disconnect | AI → MCP (`AiMcpSettings.tsx:162`) | A connected app's access |
| Delete role | People and roles → Roles (`Roles.tsx:250`) | The role |
| Disable this account | Users drawer (`Users.tsx:459`) | Signs the person out at once |
| Take back | Users → invites (`Users.tsx:147`) | The invite link |
| Copy what is different | Discord → Role and ban sync (`SyncCard.tsx:121`) | Bans, unbans and role removals on **both** platforms |
| Remove pair | Discord → Role and ban sync (`SyncCard.tsx:297`) | A role pairing |
| Save retention (with a lower number) | Server (`DataSection.tsx:289`) | Older records, **permanently** |
| Send (DELETE/PUT/POST) | VRChat proxy → Playground (`VRChatProxySection.tsx:116`) | Anything the VRChat API allows |
| Verify and store | VRChat login | Replaces Modbot's VRChat account |

"Copy what is different" and lowering retention are the two that matter most. One click can undo
bans across two platforms, and the other deletes history for good.

Use the console spec §12.1 shapes:

- **Row actions** (Revoke, Delete, Roll secret, Disconnect, Remove pair, Take back): the two-step
  swap from §12.1.1.
- **Copy what is different:** always show the preview, with Copy as the dialog's action (§12.1.3).
- **Lowering retention:** a dialog that names how many records go (§12.1.3).

## 5. Empty, loading and failed

- **Email settings.** A failed load is swallowed (`IntegrationsSection.tsx:78`). The limit box goes
  grey with no message.
- **Machine usage.** On error it disappears with no trace (`MachineUsageCard.tsx:66-75`).
- **Repeat offenders, Flagged and Auto-invites.** While they load, the forms show default values
  that look real. A failure shows only in the footer.
- **Updates.** The error is the raw "cloud.modbot.co answered 404." in red.
- **Credits → People.** A load failure reads "Nothing to show."
- Empty lists are consistent and fine: "No keys.", "No webhooks.", "No channels", "No term lists".

## 6. Phone and VR

- **Phone.**
  - The tab row shows 3 of 14 tabs, with no edge fade or arrow to say there are more.
  - Retention is three columns at every width (`DataSection.tsx:298`), so its labels wrap to two
    lines at 390px.
  - The wide tables scroll sideways as the spec allows:
    - API keys, 9 columns
    - AI limits, 7 columns
    - AI call log, 11 columns
  - `min-w-[16rem]` fields (test email, playground path) push their rows.
- **VR** (from the code only).
  - Settings uses the shared density tokens, so the targets grow.
  - The 14-tab row gets longer still, and a sideways scroll in a headset is awkward.
  - A side list scrolls up and down, which suits a headset better.

## 7. Clutter

- **Server** has 8 panels and runs about 1.6 screens tall. It mixes:
  - things an owner changes: retention, logs and the public address
  - things only a host operator reads: version commit, release branch, machine CPU, memory and
    disk charts, and per-record byte size
- **Discord** has 5 panels and 4 differently named Save buttons. Account linking is a long column
  next to a shorter one.
- **Evidence** leads with a status box and a "What this store is doing" panel of six internal
  facts before the limits.
- **Moderation.** Every ban-reason row repeats "↑ ↓ Switch off" at the right. That is 21 controls
  for 7 rows.

## 8. Suggested direction

1. **Group the tabs.** Replace the row with a side list under a few headings, like Discord's and
   GitHub's settings. On a phone it becomes a list you tap into.
   - *Your group:* Moderation, AutoMod, Auto-invites, Evidence
   - *People:* People and roles, Purge a person
   - *Connections:* Modbot's VRChat login, Discord, Email and alerts, AI
   - *This install:* Server, Updates
   - *For developers:* API, VRChat proxy, AI apps (MCP)

   Sync moves to Health. Credits leaves Server.
2. **One save rule** (§3), and every button says "Save".
3. **Confirm the dangerous actions** (§4).
4. **Plain words in the panels** (§2), and remove the banned explanatory lines.
5. **Slim the Server tab.**
   - Owner settings go first: public address, and how long to keep records and logs.
   - Host facts go in one "This install" panel.
   - Machine usage and the storage estimate go behind a "Show usage" disclosure.
