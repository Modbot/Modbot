# Modbot — What makes a person flagged

- **Date:** 2026-09-26
- **Status:** Rules 1–4 approved by the owner on 2026-09-26 and built with this document. The avatar
  rule (§5) is still open.
- **Covers:** the rules that make a person "Flagged" (the companion's red "Flagged user joined" card,
  its alert sound and voice line, the red roster row, the Live page), the website settings for them,
  the reason wording, and the avatar rule's lookup problem
- **Related:** client protocol design (2026-09-12), live updates design (2026-09-16), notification
  filters design (2026-09-19), audit log avatar and kick timing design (2026-09-19),
  `.agent/research/vrchat-log-events.md`, foundation §4.3.4

---

## 1. Today

One rule: a person with at least one kick or ban (`MemberKicked`, `MemberBanned`) is Flagged. It is
counted by `ContextHandler.CountPriorActionsAsync` and read in five places that must agree:

| Where | File |
|---|---|
| Companion roster, `/companion/context` | `ContextHandler.ContextAsync` |
| Companion person card, `/companion/user/{id}` | `ContextHandler.UserAsync` |
| Live stream (`person_joined` → `flagged_join`) | `LiveReader.BuildAsync` |
| Live page on the website | `LiveEndpoints` |
| Old long-poll alert (older companions) | `EventsHandler.RaiseAlertsAsync` |

One Modbot runs one group (`Settings.ManagedGroupId`), so "per group" means this server's settings.

## 2. The rules

A person is Flagged when **any** switched-on rule matches. All rules are read from Modbot's own
records; none of them calls VRChat when a person joins.

| Rule | Matches when | Setting | Default | Reason text |
|---|---|---|---|---|
| Kicks and bans | at least 1 `MemberKicked` or `MemberBanned` about them | on/off | on | "1 kick or ban" / "3 kicks or bans" |
| Warns | at least N `GroupInstanceWarn` about them | on/off, N (1–99) | on, N = 5 | "5 warns" (the real count) |
| Nuisance | their stored trust rank is `Nuisance` | on/off | on | "Nuisance" |
| AutoMod | they, or a Discord account linked to them, have an AutoMod flag that is open or confirmed, not dismissed, not from a trial rule, from a rule that counts | on/off; which rules count: every rule, or the rules picked | on, every rule | "AutoMod: {rule name}" (several: "AutoMod: A, B") |
| Avatar | see §5 | – | – | "Avatar: {what matched}" |

Notes on each:

- **Warns.** `GroupInstanceWarn` is the only warning Modbot records (from the audit log sync and
  imports). Notes are not warns and do not count. There is no Modbot-own or Discord warn.
- **Nuisance.** The rank lives on the stored profile, which profile sync reads *after* a person is
  first seen. A person already read is flagged at the join. A newcomer's rank may land a little
  later: their roster row turns red when it does, but no join card is sent late. (Sending one later
  would need a new kind of live event; left out unless the owner wants it.)
- **AutoMod.** Uses the `automod_flag` rows AutoMod already writes, which name the rule, list, term
  and person. Term-list severities exist in the Hub lists but nothing uses them, so the setting is
  by rule, not severity. A dismissed flag never counts. Most of what AutoMod reads is Discord
  messages, so a flag on a Discord account with an active link to the person counts too. The rule
  is named as it was when it flagged (`automod_flag.rule_name`). A new index on
  `(subject_platform, subject_id)` serves the lookup.
- **Kicks and bans** keep today's meaning. The reason text changes from "1 prior moderation action"
  to "1 kick or ban" so it reads alongside the others; `priorActions` on the wire keeps its meaning.

When several rules match, the reason line lists them in the table's order, joined with " · ",
e.g. "2 kicks or bans · Nuisance". The roster row carries one chip per matched rule, same words.

Flagged still beats Staff and Member in roster order (`Standing`).

## 3. Website settings

**Settings → Moderation**, a new card **"Flagged"** under the repeat-offender card. No explanatory
text on the card (CLAUDE.md).

```
┌ Flagged ─────────────────────────────────────────┐
│ Kicks and bans                          [on ]    │
│ Warns                                   [on ]    │
│   At least            [ 5 ]                      │
│ Nuisance rank                           [on ]    │
│ AutoMod                                 [on ]    │
│   [x] Every rule                                 │
│       (unticked: one box per term list and topic)│
│                                        [Save]    │
└──────────────────────────────────────────────────┘
```

- Stored as one sparse jsonb document, `settings.flag_rules`, the way `ReviewThresholds` is (missing
  key = default). One migration adds the column.
- `GET` / `PUT /api/settings/flag-rules`, needs `ManageSettings`. A save writes a `SettingsChanged`
  fact with before and after.
- The AutoMod picker lists term lists and topics by name; a deleted rule drops out on read.

## 4. How it is built

- One reader, `FlagRules.ReadAsync(db, subjectIds, ct)`, reads the settings once and, in one query
  per rule, returns for each person the rules that matched and the reason words. It replaces
  `CountPriorActionsAsync` at all five places above (and the chat tool that reads the same thing).
- `Describe`, `Standing` and `Flags` take that result instead of a bare count.
- **Wire shapes do not change.** `flagged`, `reason`, `standing`, `flags` and `priorActions` keep
  their names and types; only the words and when `flagged` is true change. The old long-poll alert
  (`FlaggedJoinAlertDto`) already carried a `reason`. `priorActions` still counts kicks and bans
  even with that rule off, because it has always been on the wire.
- **One companion display change.** The alert card printed "N prior actions" under the reason, and
  the person card printed it after the standing. With the reason now naming every rule, both
  would say it twice, so the companion shows the reason and the flags line only
  (`Overlay/Views/OverlayView.cs`). An installed older companion still shows the extra line.
- Docs: the "Who is flagged" section on `moderation/live.mdx`, with the overlay, Flags and live
  updates pages pointing to it.

## 5. Avatars — blocked, owner to choose

VRChat's security analysis (`GET /analysis/{fileId}/{versionId}/security`) needs the **file id and
version of the avatar's own package**. The only way to them is `GET /avatars/{avtr_…}` →
`unityPackages[].assetUrl`, which needs the avatar's `avtr_` id. Modbot has neither:

- The log names another person's avatar only by its display name: `Switching {person} to avatar
  {avatar}`, and `[AssetBundleDownloadManager] [n] Unpacking Avatar ({avatar} by {author})`.
  Checked on the owner's own logs from 2026-09-26: no line ties a `file_` or `avtr_` id to another
  person. (`file_` ids that do appear are `/1/file` pictures on `[SDFCreator]` lines, not packages.)
- VRChat stopped sending `currentAvatarImageUrl` / tags for other people in API v1.21.0.
- The instance's user list, which would carry it, is only filled in for the world's owner.
- The analysis endpoint may also only answer for the file's own uploader. Not checked.

The owner answered (2026-09-26) that an avatar switch in the Live data carries more information.
Checked and not found yet: the local deployment's `vrchat.avatar.change` facts hold only
`avatarName`, `displayName` and `deviceId`; the local Live page had no open instance. Waiting on
where the owner sees it.

Choices, once that is known:

- **A. Avatar name rule now.** A list of words set on the website, matched against the avatar
  name and author from the log lines above (what the companion already sends as `avatarName`, plus
  the author if the companion is taught the Unpacking line). Reason "Avatar: {word}". No VRChat call.
- **B. Test VRChat's verbose log first.** The owner starts VRChat with `--enable-verbose-logging`,
  joins an instance, and I check whether download lines then carry the package's file URL. If they
  do, the companion sends it, and the server calls the analysis endpoint — after the owner answers
  the rate-limit question for `/avatars` and `/analysis` (foundation §4.3.4).
- **C. Leave avatars out** of this change and build rules 1–4.

## 6. Tests to write (not run)

- `FlagRules` against real PostgreSQL: each rule alone, thresholds at N−1 and N, dismissed and trial
  AutoMod flags ignored, rule picked vs not, Nuisance vs no rank, several rules at once and the
  reason order, settings missing = defaults.
- `LiveReader`: a join becomes `flagged_join` with the new reason; a join matching nothing stays
  `person_joined`.
- `ContextHandler`: Flagged beats Staff and Member; one chip per rule.
- Settings endpoint: round trip, bad N refused, `SettingsChanged` written.
- Web: the card's load and save.
