# Thy Kingdom batch 3: build plan (2026-10-13)

Ideas, findings and the user's answers: `.agent/research/2026-10-13-thy-kingdom-ideas-batch-3.md`.
Checked against origin/staging 613b8bb4. Each step is one builder (delegate skill, Sonnet 5.5 High).

## New from VRChat / Discord

| Call | New? | Notes |
|---|---|---|
| AddGroupMemberRole / RemoveGroupMemberRole (step A, D) | No | Already used by role sync. |
| CreateInstance (step B) | No | `CalendarOpener` already uses it (`instances.create` lane). |
| World capacity (step B) | No | Stored on `VRChatWorld.Capacity` / `RecommendedCapacity`. |
| Discord: delete a channel (step A) | **Yes (Discord)** | Needs Manage Channels. Only a Modbot ticket thread or a channel inside the category the server picks; never any other channel. |
| Discord: start a public thread on a message (step C) | **Yes (Discord)** | Needs Create Public Threads. |
| Discord: add/remove role, post with buttons, slash commands | No | `AddRoleAsync`, `RemoveRoleAsync`, `PostWithActionsAsync`, `RegisterCommandsAsync`. |

No new VRChat endpoint, so no rate-limit question.

## Steps

| Step | Idea | What | Migration | Size |
|---|---|---|---|---|
| A | 1 | 18+ check. Per-server settings: Discord roles to give, Discord roles to remove, VRChat group role to give, delete the ticket after (on/off), ticket category. `/verify member method:(badge/photo)` typed in a ticket, and the existing 18+ button on the profile, both: set the 18+ flag, give/remove the roles, log method, date and checker (nothing else kept), then delete the ticket if switched on. Staff only. | Yes | Medium |
| B | 2 | Second-instance alert: setting "alert at N people" or "at N% of the world's capacity". Posts in the alerts channel with an "Open second instance" button (staff only; same world and settings as the full one). Existing filling/unwatched alerts stay. | Yes (settings) | Small–medium |
| C | 3 | Event recap (Discord only): when a calendar event's instances have closed, post in a chosen channel: people who came, peak head count, a photo thread started on the post, and the next date of the same event. Setting on/off + channel. | Yes | Medium |
| D | 4 | Milestones: count events each person came to (instance joins in calendar-opened instances). Profile marks for staff (Nth event, back after N days). Optional "Regular" Discord role at the Nth event (needs a linked account). Optional post in a chosen channel for each moment. Defaults: 1st and 5th event, back after 60 days. | Yes | Medium |
| E | 5 | Partner tracker (staff only): Partners page with group, contacts, last collab, Envoy, notes. New permission. | Yes | Medium |
| F | 6 | `/term` and `/rule`: Terms and Rules lists staff type into a Modbot page; the commands answer from them with autocomplete. Own Commands-card switches, off by default. | Yes | Small–medium |
| G | 7 | Yearly report + appeals (staff only): log each appeal (person, received, upheld / turned down, linked ban). Report page per year: warnings, kicks, bans, unbans, appeals received / upheld / turned down; download as CSV. | Yes | Medium |

User's answer (2026-10-13): a member with no linked account: `/verify` takes their VRChat profile link
(required then) and gives both the Discord and the VRChat roles (A).

Command names (user, 2026-10-13): the 18+ command is `/ageverified` (staff only, its own Commands-card switch,
off by default) and the existing `/verify code:` becomes `/verifyaccount` (behaviour unchanged).

## Shared files (rebase carefully)

| File | Steps |
|---|---|
| `src/Modbot.Core/Data/Migrations/ModbotContextModelSnapshot.cs` | all: regenerate the migration after rebasing, never hand-merge the snapshot |
| `src/Modbot.Web/src/lib/nav.ts`, `api.ts` | A C D E F G |
| `src/Modbot.Discord/Gateway/IDiscordGateway.cs`, `DiscordNetGateway*.cs` | A B C D F |
| Slash command registration + Commands card | A F |
| Settings contracts / Settings page | A B C D F |
| Event attendance query | C writes it, D reuses it (D builds after C) |
| `docs/content/docs/` | every step |

## Order (three builders at a time)

1. Wave 1: A, E, G
2. Wave 2: B, C, F
3. Wave 3: D (on top of C)

Each builder: compile checks, rebase onto staging, diff review, localhost + Chrome check, push to staging.
Tests written, not run; one testing pass after wave 3 takes CI green on master.
The first builder also commits this plan and the batch-3/4/5 answer files.

## Notes

- Thy Kingdom's live bot lacks Manage Roles, so role steps in A and D fail there until it is given; the
  card shows the missing permission.
- Milestones and recap turnout only see people in instances where a moderator's Modbot app was present.
