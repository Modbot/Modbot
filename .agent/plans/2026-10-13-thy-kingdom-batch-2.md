# Thy Kingdom batch 2: build plan (2026-10-13)

Ideas, findings and the user's answers: `.agent/research/2026-10-13-thy-kingdom-ideas-batch-2.md`.
Checked against origin/master b703abf0. Each step is one builder (delegate skill, Sonnet 5.5 High).

## New from VRChat / Discord

| Call | New? | Notes |
|---|---|---|
| AddGroupGalleryImage (step G) | **Yes** | Shares `groups.gallery.write`, 0.1 req/s with removing (user approved). Never retry a 429. |
| GetGroupGalleryImages with `approved: false` (step G) | No | Same call Modbot already makes; the SDK (2.21.x) has an `approved` filter. |
| GetGroupCalendarEvents (step A) | No | Already read by `CalendarVRChatReader`. |
| Discord: role/permission events, slash commands, buttons, select menus, forum/thread create | No new limits | Gateway already gets RoleUpdated / GuildMemberUpdated; slash commands registered in `DiscordNetGateway.cs`. |

## Steps

| Step | Idea | What | Migration | Size |
|---|---|---|---|---|
| A | 1 + 2 | Calendar save warnings: empty VRChat tags or language; title differs from the template's; same title + start time (within 15 min) already in Modbot or on VRChat's group calendar. All warn, all let you save. | No | Small–medium |
| B | 6 | Requests list: each row shows past bans, staff notes and 18+ status, from stored data (no new VRChat reads). | No | Small |
| C | 8 | Permission watch: when a role or bot gains Administrator or another dangerous permission, alert staff (mod log + staff channel). Setting to remove it automatically, off by default. | Yes (setting) | Small |
| D | 7 | Rank label on each role pair; one "out of step" view listing members whose VRChat and Discord roles disagree, grouped by rank. | Yes (column) | Small–medium |
| E | 3 | "We're live" drafts: setting for which instances (calendar-opened / any group instance / opened from Modbot). Draft goes to the Marketing drafts; the Host gets a Discord ping with a link. Setting to post automatically instead (no draft, no ping). Replaces today's always-on "{title} has started." post behind the same setting. | Yes (settings + draft link) | Medium |
| F | 4 | Regulars and 4-week return rate: from instance join facts; a return = any visit to a group instance. New report page in Analytics. | No (reads facts) | Small–medium |
| G | 5 | Gallery queue. A: members submit pictures in a Discord channel, staff approve in Modbot, Modbot uploads (existing /file/image) and adds it to the chosen gallery (new AddGroupGalleryImage). B: list VRChat's own not-yet-approved submissions on the Gallery page. | Yes (submissions) | Medium |
| H | 9 | Self-assign role menus (buttons / select menu posted by Modbot). | Yes | Medium |
| I | 9 | Member tickets (button opens a private thread with staff; close + transcript to the mod log). | Yes | Medium |
| J | 9 | Birthdays (`/birthday set`, daily post + optional role for the day). | Yes | Small–medium |
| K | 9 | Polls (`/poll`, buttons, results). Overlaps batch 4 #3 (voting). | Yes | Medium |

## Shared files (rebase carefully)

| File | Steps |
|---|---|
| `src/Modbot.Core/Data/Migrations/ModbotContextModelSnapshot.cs` | C D E G H I J K: builders regenerate their migration after rebasing, never hand-merge the snapshot |
| `src/Modbot.Web/src/lib/nav.ts` | D F G H I J K |
| `src/Modbot.Discord/Gateway/DiscordNetGateway.cs` / `.Interactions.cs` | C H I J K |
| Settings page + settings contracts | C E G H I J |
| `src/Modbot.Web/src/components/calendar/CalendarEventForm.tsx` | A only |
| `src/Modbot.VRChat/Calendar/CalendarFirstJoinVRChatPost.cs`, `GroupInstanceSync.cs` | E only |
| `docs/content/docs/` | every step adds or updates its page |

## Order (three builders at a time)

1. Wave 1: A, B, F (no migrations, almost no overlap)
2. Wave 2: C, D, E
3. Wave 3: G, H, I
4. Wave 4: J

K (polls) is held for batch 4, to be designed together with batch 4 #3 (voting) (user, 2026-10-13).

Each builder: compile checks, rebase onto staging, diff review, localhost + Chrome check, push to staging.
Tests written, not run; one testing pass after wave 4 takes CI green on master.
The first builder also commits the three batch files and this plan.
