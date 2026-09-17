# Web UX audit: audit log, member lists and the popup

*2026-09-16.* What `src/Modbot.Web` does today on the four screens the Linear work touches, what
the API already filters on, and what has to be added. Read with
`2026-09-16-linear-ui-findings.md`.

## 1. What exists

### The shell

- `src/App.tsx`: one `Shell` with a fixed sidebar (`components/Chrome.tsx`, 13.5rem) and a top
  bar (page title, density, theme, account, sign out). Pages are picked by path in `pageFor`;
  `NAV` in `lib/nav.ts` carries id, label, group and the permission each page needs.
- `lib/router.ts`: a hand-rolled path + query router. `useLocation`, `useQueryParam` (replace,
  never push), `go` (pushes and wakes every `useLocation`), `followLink`.
- `lib/preferences.ts`: density and theme in `localStorage` under `modbot.prefs`.
- **No keyboard shortcuts of any kind.** Escape closes the popup because Radix Dialog does it.
  There is no command palette, no shortcut sheet, no list selection, and no focus-the-filter key.
- No global search. The only search boxes are the per-page ones on Members, Discord members,
  Bans and the case list.

### Audit log (`pages/AuditLog.tsx`, 427 lines)

- One table: When, Source, What happened (a sentence from `components/factSentence.tsx`).
  Hovering a row shows the type in a tooltip. Nothing on the row itself is clickable except the
  names inside the sentence; the raw payload is only shown for `modbot.unrecognised`.
- Filters: a row of source chips (`AuditLog`, `SyncDiff`, `Client`, `Discord`, `Manual`,
  `Modbot`) that all read as on while the list is empty; "More filters" reveals subject id,
  actor id, from and to (date inputs), recent actors (top 8 of the 90-day list), and type chips.
- **State is component state.** Filters are not in the URL and not remembered; a reload is a
  reset. Only `?fact=<id>` (open at one entry) is read from the address.
- **The default shows every source, including Sync.** Spec 5.9.5 lists the defaults as VRChat,
  Modbot, Discord and Client; the page never applied that, and `SyncDiff` rows (a change a sweep
  noticed, with a time window and no actor) sit between the exact ones.
- Paging is keyset, "Load more" appends 50. Coverage line ("Oldest recorded entry") above.
- Two explanatory paragraphs in the filter card ("Hidden from this account: …") — UI text the
  conventions say not to add unless asked.

### Members (`pages/Members.tsx`, 439 lines)

- Search box (300ms debounce), native `<select>` for role, status (current, left, all), sort
  (joined, name, seen), Discord link (all, linked, not linked, needs ViewProfile). A `joinedFrom`
  and `joinedTo` pair is read from the URL only when an alert links to it, and shows as one
  removable button.
- Everything else is component state; nothing else is in the URL or remembered.
- Rows: person link, Discord account, roles, joined, last seen, left, and a row-actions menu.
  Rows are not focusable and there is no selection.
- `Select` and `Empty` are exported from here and imported by Discord members.

### Discord members (`pages/DiscordMembers.tsx`, 278 lines)

- Same shape: search, state (in server, left, all), role, linked. The whole row is clickable and
  opens the Discord person popup. Component state only.

### The popup (`components/subject/*`, `lib/subject.ts`)

- `SubjectPopup` reads a stack of `?subject=` values and draws the top one: `PersonPopup`,
  `WorldPopup`, `InstancePopup`, `DiscordPersonPopup`. Opening pushes history; closing is
  `history.back()` when Modbot pushed the entry, else a rewrite.
- `PopupFrame` (in `shared.tsx`) is `h-[min(50rem,calc(100dvh-2rem))] w-[calc(100vw-2rem)]
  max-w-6xl` with a 20rem left column and tabs on the right. `DialogContent` in `ui/dialog.tsx`
  has its own `max-w-[520px]`, overridden here.
- Tabs: person Logs / Cases / Metrics; world Instances / Metrics; instance People / Logs;
  Discord person Logs / Messages / Metrics. Tab choice is component state, not in the URL.
- **No JSON view anywhere.** The raw stored record (the `VRChatUserProfile`, `WorldView`,
  `InstanceView`, `DiscordMember` responses) is never shown.
- **No history tab.** The person popup's Logs tab shows the repeat-offender counts and the 50
  newest facts; a profile's earlier versions are not reconstructed anywhere.

### Facts as sentences (`components/factSentence.tsx`, 746 lines)

- One sentence per type, keyed on `type` then `typeRaw`. `changed` payloads (`{field: {old,
  new}}`) are written as "bio, from X to Y" for one field and "a, b, c changed" for several.
- Snapshot facts: `vrchat.group.members.snapshot` and `bans.snapshot` carry only a headcount;
  `discord.members.snapshot` a count; `vrchat.user.profile.first-seen` a `baseline` object;
  `vrchat.user.profile.changed` a `changed` diff; `modbot.report.snapshot.recaptured` names the
  case file. None of these open anything.

## 2. What the API filters on today

### `GET /api/audit` (`Features/Audit/AuditLogEndpoints.cs`, `AuditQuery.cs`)

| Parameter | Type | Notes |
|---|---|---|
| `type` | repeated string | Fact type names, intersected with what the caller may see |
| `source` | repeated enum | `AuditLog`, `SyncDiff`, `Client`, `Discord`, `Manual`, `Modbot` |
| `subject` | string | Exact match on `subject_id` |
| `subjectPlatform` | enum | `VRChat`, `Discord`, `Modbot` |
| `actor` | string | Exact match on `actor_id` |
| `actorPlatform` | enum | |
| `from`, `to` | datetime | On `occurred_at`; `to` exclusive |
| `beforeOccurredAt` + `beforeId` | cursor | Keyset paging |
| `limit` | int | 1–200, default 50 |

`GET /api/audit/filters` returns the visible type list with labels and category, the source
names, and the top 50 actors of the last 90 days with action counts. `GET /api/audit/entries/{id}`
returns one entry.

Columns on `modbot_event` that are **not** filterable yet: `world_id`, `instance_id`, category
(derived from type), precision (exact vs window), whether there is an actor, and the payload.

### `GET /api/members` (`Features/Members/MemberEndpoints.cs`)

`search` (ILIKE on display name and id), `role` (one role id, JSON contains), `status`
(current, left, all), `sort` (joined, name, seen), `linked` (needs ViewProfile), `joinedFrom`,
`joinedTo`, `page`, `pageSize`. The response carries the group's roles (no counts) and coverage.

Not filterable: several roles at once, "has no role", 18+ flag, representing, last seen range,
profile fetched or not, membership status word, visibility.

### `GET /api/discord/members` (`Features/DiscordMembers/DiscordMemberEndpoints.cs`)

`search`, `state` (in-server, left, all), `role` (one), `linked`, `page`, `pageSize`. Roles come
back highest first (no counts). No sort.

Not filterable: several roles, bots, pending, timed out, boosting, joined range, sort.

### Search

There is no search endpoint. `GET /api/discord/routes/people?search=` finds stored VRChat
profiles by name or id but needs ManageSettings. Members and Discord members each search their
own list. Worlds are only listed by `GET /api/analytics/worlds` (ViewAnalytics).

### Snapshots and history

There is no versions table. A person's profile at a moment in time exists only as facts:

- `vrchat.user.profile.first-seen` → `data.baseline` (display name, pronouns, date joined, age
  status, age verified, tags).
- `vrchat.user.profile.changed` → `data.changed` as `{field: {old, new}}` under VRChat's own
  field names (`displayName`, `bio`, `statusDescription`, `pronouns`, `currentAvatarImageUrl`,
  `currentAvatarThumbnailImageUrl`, `profilePicOverride`, `ageVerificationStatus`,
  `ageVerified`, `dateJoined`, `tags`), with a time window since the previous refresh.
- `vrchat_user` holds the current row plus `raw_profile` and `raw_public_profile` (last read
  only, not history).
- A case file stores `profile_at_ban`, `membership_at_ban` and `ban_list_entry_at_ban` as JSON,
  taken when it was written, with one permitted recapture.
- Worlds: `vrchat_world` is the last read; `WorldSync` writes no change facts.
- Instances: `vrchat.group.instance.create/close/update` carry VRChat's whole audit entry under
  `data.auditData`; `vrchat_instance` rows are the room as Modbot last knew it.
- Group: `vrchat.group.update` carries a `changed` diff; `settings.group_info_snapshot` is the
  current read.
- Discord: `discord.member.nickname`, role changes and the rest carry `changed` or the ids; the
  `discord_member` row is current only.

So "the user as they were at that time" can be rebuilt by replaying `changed` diffs backwards
from the current row (old values) or forwards from the baseline (new values); nothing else
needs a new table. A world or an instance has no versions to show, only the current record and
whatever the fact's payload held.

## 3. What needs adding

**API**

- `GET /api/audit`: `world` (world id), `instance` (VRChat instance number), `category`
  (Moderation, Operational), `precision` (Exact, Window), `hasActor` (true/false), `q` (a word
  in the description or payload, ILIKE on `data::text`). Counts per source and per type for the
  current window are cheap enough on a partitioned table for one page; per-value counts on the
  filter list come from `/api/audit/filters` (types already carry none; add a 30-day count).
- `GET /api/members`: `role` repeated (any of), `noRole`, `eighteenPlus`, `representing`,
  `seenFrom`/`seenTo`, `profile` (fetched, not-fetched); role counts on the roles list.
- `GET /api/discord/members`: `role` repeated, `bot`, `pending`, `timedOut`, `boosting`,
  `joinedFrom`/`joinedTo`, `sort` (joined, name, seen); role counts.
- `GET /api/search?q=`: people (VRChat, by name or id, ViewMembers), Discord people
  (ViewMembers), worlds (ViewAnalytics), for the command palette. One round trip, permission
  narrowed, small.
- `GET /api/vrchat-users/history?id=`: the profile versions replayed from facts, newest first,
  each with the fact id, the window, which fields changed, and the fields as they stood after
  that change. ViewProfile.

**Web**

- A shortcut registry and a `useShortcuts` hook that ignores keys typed into fields; a command
  palette; a `?` sheet; `g` chords; list selection with `j`/`k`/`Enter`.
- One `FilterBar` component (property → operator → values, chips, clear, URL, localStorage)
  used by the audit log, Members and Discord members, with the audit log's default sources set
  to VRChat, Discord and Client (Sync off).
- A wider popup, tabs per kind with Overview, History and JSON added.
- Audit rows that expand on click into the full detail and a JSON view.
- A profile-version view from the history endpoint, opened from a profile fact's expanded row
  and from the person popup's History tab.
