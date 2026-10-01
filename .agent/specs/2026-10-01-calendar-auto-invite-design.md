# Modbot Calendar: inviting people, and saying when the first person is in

- **Date:** 2026-10-01
- **Status:** Built with this document
- **Covers:** who an event invites when Modbot opens its instance (§2), how each person is reached
  (§3), pacing and the new VRChat endpoint (§4), what survives a restart (§5), what stops sending
  (§6), the first-person post (§7), facts, purge and permissions (§8–§9), the screen (§10), what is
  pending and what was left out (§11–§12)
- **Depends on:** calendar design §4 (opening the instance, `calendar_opening`); lists design (saved
  lists, `GiveawayRuleChecker.PeopleAsync`, §6 permissions); foundation §4.1, §4.1.1, §4.3.1, §4.3.4;
  people already in an instance (`InstancePeopleReader`)
- **Narrows:** lists design §1 and §8 ("a list never messages anybody"), and giveaways design §9 (no
  direct messages). The user decided both on 2026-10-01 for this one case: an organiser picks a list
  for an event, and the people on it are invited to that event.

---

## 1. What this adds

An event that opens its instance automatically can also invite people to it: **the host first, then
the event's staff, then the people on one saved list**. Not every group member.

And an event can post once, in its Discord channel and in the VRChat group's posts, when **the first
person is in** the instance.

Both are settings on the event, shown only when **Open the instance automatically** is ticked.

## 2. Who is invited

| Field | What | Stored |
|---|---|---|
| **Host** | One Modbot staff account | `calendar_event.invite_host_user_id` |
| **Staff** | Several staff accounts | `calendar_event.invite_staff_user_ids` (jsonb) |
| **Invite list** | One saved list, optional | `calendar_event.invite_list_id` |

A staff account is reached through its linked VRChat account, and its Discord account when that is
proven (`StaffDiscord`) or when the VRChat account is linked to one in the server. A list's people are
who the list holds **at the moment the instance opened**, worked out by the same checker as the Lists
page, with the VRChat and Discord ids it found.

The queue is written once per occurrence, in order: host, staff, list. One row per person per
occurrence (`calendar_invite`, unique on event, occurrence and person), so a host who is also staff and
on the list is invited once, as the host.

**Left out when the queue is written, and again just before each send:**

- banned from the VRChat group (`group_ban`, not lifted), or from the Discord server;
- already in the instance, when a moderator's client is there to say so (`InstancePeopleReader`).

They keep a row marked **skipped**, so the event can say why, and are not counted.

## 3. How each person is reached

```
has a VRChat id ──► VRChat invite ──► accepted ............................ VRChat invite
                         │
                         └─ refused (403: not friends, or anything else)
                                 │
has a Discord id ◄───────────────┘  (or no VRChat id at all)
        │
        ├──► direct message ──► sent ...................................... Discord message
        │                  └──► refused (DMs closed, unknown user) ....... couldn't reach
        └─ none ........................................................... no way to reach
```

**VRChat only lets an account invite its friends.** `POST /invite/{userId}` answers 403 "You need to
be friends with that user first" otherwise (VRChat's API description, checked 2026-10-01).

### 3.1 Who is a friend: remembered, not read

VRChat does not hand over the full friends list cheaply, and reading it would be a new endpoint. So
Modbot keeps its own memory, `vrchat_friend` (person, friend yes or no, when, how it learned), decided
by the user 2026-10-01:

| Learned from | Says |
|---|---|
| An invite VRChat accepted | friend |
| An invite VRChat refused with 403, not a Cloudflare block | not a friend |
| The `friends` ids VRChat sends with the account when Modbot signs in (no extra call) | friend. Only adds: that list may be incomplete, so nobody missing from it is marked not a friend |

How it is used, for each person with a VRChat id:

| Modbot remembers | Does |
|---|---|
| a friend | VRChat invite |
| nothing yet | one VRChat invite; the answer teaches it |
| not a friend, learned in the last **7 days** (`CalendarInviter.AskAgainAfter`) | straight to the Discord message, no VRChat turn spent |
| not a friend, older than that | one VRChat invite again, in case they added the account since |

The sign-in's ids are held in memory (`SignInFriends`) until the invite loop writes them down; the
gate has no database. A purge deletes the person's row.

**The direct message** carries the event's title and a **Join** button with the instance's link,
nothing else. Discord.Net keeps to Discord's own limits; Modbot also waits two seconds between direct
messages and sends at most five a pass.

## 4. Pacing, and the new endpoint

`invites.send` — `POST /invite/{userId}` (`InvitesApi.InviteUserWithHttpInfoAsync`):

| | |
|---|---|
| Rate | **1 per 30 s.** Set by the user 2026-10-01, not measured (foundation §4.3.4). Its own class, not `groups.invites`: that is the group invite, a different endpoint. |
| Lane | `invites.send`, its own |
| Backstop | `global` — timer-driven, nobody is waiting on it |
| Priority | Background |
| 429 | Cold-stops this class (§4.3.1). That person's invite is not sent again; they fall to Discord. The next people wait for the class to open. |
| 5xx, 408, no answer | The invite may have arrived. Not sent again, and **no direct message on top**: the person counts as couldn't reach. Only a refusal VRChat certainly acted on (a 4xx other than 408) hands them to Discord. |
| Cloudflare block | Arrives as a 403 with Cloudflare's page; checked before the 403 rule. VRChat never saw it: nothing is learned about the person, no direct message, and the row waits for a later turn. |

The pace is kept twice, as group invites do: by the bucket, and by the last send time stored on the
rows (`tried_at`), so a restart cannot hand back a turn.

A request the gate never sent (the class cold-stopped, VRChat not signed in) leaves the row waiting
for the next pass. That is not a retry: nothing reached VRChat.

## 5. Restarts

Everything is in the database. The queue is written once (`calendar_opening.invites_queued_at`).
A row is marked **sending** before the VRChat call and **messaging** before the direct message;
a crash between the mark and the answer leaves it there, counted as sent, and never sent again.
Being invited twice is the failure to avoid.

## 6. When sending stops

Waiting rows are marked **stopped** when the instance closes, the occurrence ends, or the event is
cancelled or deleted. A list too long to finish in time simply stops; the event says "Invited N of M".

## 7. "The first person is in"

Two tick boxes, each once per occurrence, when the group instance poll first counts somebody in the
event's opened instance (`vrchat_instance.last_user_count` or `peak_user_count` above zero):

| Tick box | Posts | Needs |
|---|---|---|
| **Announce in Discord when the first person joins** | In the channel post's channel: the title and a **Join** button | The channel post and its channel; otherwise it cannot be ticked |
| **Announce in VRChat when the first person joins** | A group post: the title as its title, "… has started." and the join link as its text; visible to the group; **VRChat's member notification off** | The managed group |

Each is marked on `calendar_opening` (`first_join_discord_posted_at`, `first_join_vrchat_posted_at`)
before it is sent, so a restart never posts twice; a refusal is kept on the row and shown on the event.

**A group post, not a group announcement** (user, 2026-10-01). VRChat's own description of
`POST /groups/{groupId}/announcement` says creating one removes the group's other announcements, and
posts are the newer way. The post call (`GroupsApi.AddGroupPost`) is one Modbot already makes from the
VRChat page, on `groups.posts.write` (1 per 10 s, foundation §4.3.4), so nothing new is asked of
VRChat. Sent at Background priority: a timer sends it.

## 8. Facts

One fact per person reached or not, subject **the person** (VRChat id, else Discord id), no actor:
Modbot did it. Moderation log, like group auto-invites.

| Fact | When | Data |
|---|---|---|
| `modbot.calendar.invite.send` | A VRChat invite or direct message went out | event, title, `via`, `role` |
| `modbot.calendar.invite.fail` | Neither got through | event, title, `via` tried, `role`, VRChat's or Discord's words |

No notification is raised. A purge deletes the person's `calendar_invite` rows and their
`vrchat_friend` row with their facts.

## 9. Permissions

- Setting host, staff, list and the first-person post: **Manage calendar**, like the rest of the event.
- **Picking a list** (or changing it) also needs **See members and See profiles**, the Lists rule
  (lists design §6). Keeping the list already on an event while editing something else does not.
- A list an event still being run names cannot be deleted, as for giveaways and auto-invites.

## 10. The screen

Event form, under **Open the instance automatically**: **Host**, **Invite list**, **Staff**,
**Announce in Discord when the first person joins**, **Announce in VRChat when the first person
joins**. Event details: **Invited N of M**, then VRChat invite, Discord message, couldn't reach and no
way to reach as counts, and a refused first-person post's error. No explanatory text.

A list already on an event is shown by name to somebody who may not pick lists, and cannot be changed
by them. The Lists page shows an event that invites a list beside its giveaways.

## 11. Pending: VRChat's Terms of Service

The user asked for a check of whether a bot that opens group instances on a timer, sends invites and
direct messages for them, and posts in the group when the first person is in, is within VRChat's
Terms of Service. Not done yet. Calendar design §4 carries the same note.

## 12. Not built

- Reading the whole friends list (a new endpoint; ask first). §3.1 remembers instead.
- Sending to people who join the list after the instance opened.
