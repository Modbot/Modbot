# One Stats page

**Date:** 2026-09-27
**Owner:** Zuelatak
**Status:** Built on 2026-09-27; not yet merged or looked at in a browser.

## What was asked

On 2026-09-27 the owner said no to a **Stats** tab at the end of the VRChat page's tab row (finding 3
of `.agent/research/2026-09-27-vrchat-page-review.md`, finding 6 of the whole-site review on branch
`claude/relaxed-solomon-c98036`). A VRChat-only tab is the wrong shape: the charts about the group,
its instances and worlds, the Discord server and the team belong on **one page of their own that
covers every platform**. The VRChat page should read like vrchat.com and the Discord page like
Discord, and neither has charts in it.

Three shapes were drawn: a tab per place, a tab per question, and a Stats heading in the sidebar.
The owner chose **a tab per question**.

## What is wrong today

Numbers from the live group ("-Thy Kingdom 18Plus"), 30 days to 2026-09-27, read with GET requests
only.

- **VRChat page, Overview:** the stats are 1,504 px of a 2,686 px page at desk width (1,694 px wide),
  and about 2,000 px of 3,461 px on a phone-width window.
- **VRChat page, Instances tab:** eight charts, a strip and four peaks under the list.
- **Discord page:** 2,710 px of a 3,319 px page is charts.
- **Team and Worlds** are sidebar pages of their own under an "Analytics" heading that also holds
  the VRChat and Discord pages.

vrchat.com's group Overview has no charts at all. Discord keeps its Server Insights apart from the
server itself.

## The decisions

| Question | Decided |
|---|---|
| Shape | One **Stats** page with three tabs: **Growth · Activity · Moderation** |
| Inside a tab | VRChat and Discord each in a block of their own, headed by the platform's name. Never one chart or one number that mixes the two |
| Top of each tab | The AI summary of the matching kind (AI insights design §1): Group on Growth, Instances on Activity, Moderation team on Moderation. Nothing when there is none |
| Range | One range picker at the top of the page (7 days, 30 days, 90 days, All), shared by the three tabs. The member count chart and the people in instances chart keep their own day and week switch |
| VRChat Overview | Loses every chart. Gains a **This week** strip, the same shape as Discord's |
| Discord page | Loses every chart. Keeps its **This week** strip as it is |
| VRChat Instances tab | Keeps Open right now and Recent instances, which is what vrchat.com shows. Its charts move |
| Sidebar | **Stats** takes Team's place at the top of the Analytics heading: Stats · VRChat · Discord. Team and Worlds leave the sidebar |

## The tabs

### Growth: is the community growing?

| Block | Panel | Today on | Range |
|---|---|---|---|
| VRChat group | Members, Joined, Left, Net change, Most members | VRChat Overview | yes (Members is the latest reading) |
| | Member count chart | VRChat Overview | its own |
| | Joins and leaves per day; Joined minus left, running | VRChat Overview | yes |
| | Invites and join requests (four numbers and a chart) | VRChat Overview | yes |
| | Roles, given and taken away | VRChat Overview | yes |
| | How long members have been members | VRChat Overview | no |
| Discord | Members, Joined, Still here after 7 days | Discord page | yes |
| | Joins and leaves per day / Member count | Discord page | yes |
| | New members still here | Discord page | yes |
| | Members now: linked to VRChat, new accounts joining, here a year or more, how long members have been here | Discord page | no |

Live figures: VRChat 4,791 members, 178 joined, 63 left, 223 invites sent (79 joined within 7 days),
127 join requests (86 approved, 34 rejected), 10 roles. Discord 797 members, 32 joined; 633 of 766
here a year or more.

The VRChat tenure panel is being moved onto the member list's join dates in another piece of work
(today it counts only joins in the fact log, and says "Over a year 0"; counted from the member list,
3,750 of 4,790 joined over a year ago). This page takes whatever that work lands on.

### Activity: when is the community busy, and where?

| Block | Panel | Today on | Range |
|---|---|---|---|
| VRChat instances | Most online at once (moved here from the VRChat numbers) | VRChat Overview | yes |
| | People in instances | Instances tab | its own |
| | When the community is active (people arriving / instances opened) | Instances tab | yes |
| | Opened and closed; most open at once; most people at once; people-hours; typical time open; most people seen in one instance, each per day | Instances tab | yes |
| | Opened, Closed, Typical time open, Most open at once; the four peaks | Instances tab | yes |
| VRChat worlds | Worlds, Instances, Time open, Time seen, Visitors, Presence reports | Worlds page | yes |
| | Worlds, by time open; Visitors per day, busiest worlds | Worlds page | yes |
| Discord | Messages, Time in voice | Discord page | yes |
| | Active members (day, 7 days, 30 days) | Discord page | yes |
| | Messages per day; Minutes in voice per day | Discord page | yes |
| | Busiest channels; Busiest hours; Went quiet; Top contributors | Discord page | yes |

Live figures: 513 most online at once, 53 most people in one instance (Just B Club 4.0), 37
instances opened, 68 minutes typical, 9 worlds, 1,912 presence reports; Discord 138 messages, 340
hours in voice.

### Moderation: who is doing the work, and when is nobody covering?

| Block | Panel | Today on | Range |
|---|---|---|---|
| Team | Moderators active, Actions, Coverage gaps, Instances nobody watched | Team page | yes |
| | Coverage gaps; Actions per moderator; Actions per day; What kind of actions | Team page | yes |
| Discord | Moderation actions per day | Discord page | yes |

Live figures: 18 moderators, 845 actions, 1 coverage gap.

**Reworked 2026-10-01** (task-discovery TASK-032; analytics design review F7-F9) so the tab answers
"is moderation keeping up?" and "is the team OK?" without becoming a leaderboard:

| Decided | Why |
|---|---|
| **Actions on people** and **Door and admin**, never summed | The reviews' own list (accountability signals 3.4). Unbans are door work: relief, no strike. The insight figures split the same way |
| The reader's own row first, with their usual (the reviews' baseline) and the **team middle** (a median) | A moderator's question is "am I doing what I usually do", not "where am I in the table" |
| Each moderator's row, and who left an instance last, only with **See the audit log** | The audit log already says who did what; everybody else sees the team and themselves. This answers the task's "own row only, or everyone named?" without a new setting |
| No team middle below four active moderators | With three or fewer, the middle is one person's own figure or gives it away beside your own |
| A week grid of busy hours with nobody on, inside **Left without a moderator** | Busy by head counts, nobody on by the gaps, **not seen** striped apart so it never reads as blame. The gap logic is unchanged |
| The people buttons are saved (`ReviewThresholds.CoverPeople`, default 3), by **Change settings** only | A threshold chosen fresh on every visit is no goal. Kept in the review thresholds document so it needed no migration |
| **How long things wait**: middle waits of join requests, flags and reviews, with a daily line | Middles, not averages: one weekend request must not outweigh a hundred |
| **Acted on again** (30 days, the repeat-offender kinds) and **Bans lifted** (within 30 days, with the lift reasons) | Team numbers only. Reasons are counted by label, not sorted into "overturned", because each group writes its own list |

## The This week strips

Both strips are the last seven days, today included, against the seven before, the way
`ServerWeek` already counts Discord's (`Features/Analytics/Server/ServerAnalyticsContracts.cs`).
The strip's heading opens Stats.

| VRChat | Live (last week) | Discord | Live (last week) |
|---|---|---|---|
| New members | 39 (40) | New members | 6 (3) |
| Left | 9 (16) | Talked | 26 (22) |
| Join requests | 31 (33) | Messages | 35 (47) |
| Most online at once | 513 (none) | Time in voice | 186 h (154 h) |

Members is not in the VRChat strip: the header already shows it. Most online at once shows no
"last week" figure, because a peak is one moment and has no fair figure to set beside it.

The VRChat figures can come from the group stats request as it is, asked for 7 and 14 days. A
`week` part on that request, built like `ServerWeek`, would make it one request; that is a choice
for whoever builds it, not a requirement.

## Addresses

- Stats opens at `/stats`, and each tab has its own address: `/stats/growth`, `/stats/activity`,
  `/stats/moderation`.
- `/analytics/team` opens the Moderation tab and `/analytics/worlds` the Activity tab, so links and
  bookmarks still work.
- `/analytics/group` and `/analytics/instances` stay the VRChat page and its Instances tab.

## Foundation spec 10.1

§10.1, "Analytics is five sections, not one dashboard", rules out one long scroll that mixes
questions asked by different people. One page with a tab per question keeps that rule. Its table
was rewritten with this change (and the section renamed "split by question"):

- My Group becomes **Growth**, and takes Discord's growth panels.
- Instances and Worlds become **Activity**, with Discord's messages and voice.
- My Team becomes **Moderation**, with Discord's moderation actions.
- Tracked Groups stays a later feature.

No tab of the Stats page holds every number at once; a summary tab like that would be the dashboard
§10.1 warns against.

## Cost

- **Web:** a new Stats page with three tabs; the charts taken out of `MyGroup.tsx`,
  `Instances.tsx` and `MyServer.tsx`; `MyTeam.tsx` and `Worlds.tsx` turned into parts of tabs;
  a This week strip on the VRChat Overview; `lib/nav.ts`, the routes and search words. About 15 files.
- **Server:** none required. The five `/api/analytics/*` requests already take the range.
- **VRChat requests:** none. Every figure is read from Modbot's own records.
- **Docs:** foundation §10.1's table and the docs site's analytics pages.

## Still open

- The unusual activity card (`AlertsCard`) sits at the top of the VRChat Overview. It is not a
  chart: keep it there, or move it to Now?
- What the "Analytics" heading is called once VRChat and Discord have no charts under it. This
  belongs with the whole-site review's sidebar proposal.
- The VRChat strip's New members counts every join. Discord's counts only people who joined in the
  week and are still there. Counting VRChat's the same way (from the member list's join dates)
  would make the two read alike.
