# Thy Kingdom ideas, batch 2 (2026-10-13): research only

Sent by the Thy Kingdom Driver session on 2026-10-13. The user picked these. For each idea: check
what Modbot already has, research it, and build nothing until the user OKs a plan.

Context: the VRChat group KING.9295 has about 4,822 members and the Discord about 754, so VRChat is
the hub. Keep each feature generic (usable by any community), with Thy Kingdom as the first user.

1. **Event templates that also set VRChat category, tags and language, plus a fixed name per
   series.** An audit found tags and languages empty on nearly every event, categories
   inconsistent, and the Saturday hangout renamed often. Modbot templates steps 1 to 4 are live
   (22b3f071). Check whether they cover VRChat category, tags and language, and how a fixed series
   name would fit.
2. **Duplicate-event guard:** warn when the same title and time is posted twice. On 2026-10-07 two
   movies were each posted twice.
3. **"We're live" group post drafts:** when a group instance opens, draft the VRChat group post for
   a Host to approve and send. Staff have written about 100 of these by hand since April.
4. **Regulars and 4-week return-rate report** from event and instance attendance. This is the peer
   review's main metric. A returning-members count was set aside earlier ("backburnered").
5. **Gallery queue:** Guild Photographers submit pictures in Discord, staff approve, and Modbot adds
   them to the VRChat group gallery. Today the gallery has only "Banner and Icon". Check whether the
   API allows gallery uploads, and whether VRChat+ is required.
6. **Join-request helper:** show age-verified status, past bans and staff notes next to each VRChat
   group join request. Group joins are by request.
7. **Role sync and mismatch check from a glossary:** one mapping of rank to VRChat role to Discord
   role, with Modbot flagging mismatches. The source of truth is
   `G:\Thy Kingdom\companions\J-glossary.md`.
   - Discord names come from a 2026-10-07 snapshot.
   - VRChat names are as they will be after the user's planned renames.
   - Regent is a new rank that acts for the Owner. It has no role yet.

   | Rank | VRChat role | Discord role |
   |---|---|---|
   | Owner | Group Owner - Duke | Your Grace - Duke (Owner) |
   | Lady/Lord (admins, by Council vote) | Admin - Lady/Lord | My Lady/Lord (Admin) |
   | Moderator | Moderator - Knight | Dame/Sir - Knight (Mod) |
   | Trial | Trial Moderator - Squire | Squire (Trial Mod) |
   | Member | Member - Villager | Villager (Everyone) |
   | Guild | Contributor - Guild Member | Guild Member (to be created) |
8. **Permission watch:** alert staff when any role or bot gains Administrator or another dangerous
   permission. A test bot, ZuelTestBot, had Administrator on the live server.
9. **Replace overlapping bots:** check which of Birthday Bot, GiveawayBot, Ticket Tool and similar
   bots Modbot could take over. The server has 31 bots, and about 8 overlap on moderation.

Rules that apply: no new VRChat endpoint without asking about its rate limit (foundation spec
§4.3.4; explore the codebase first). Never retry a 429.

## Findings (2026-10-13, checked against origin/master b703abf0)

| # | Already in Modbot | Missing | New VRChat endpoint? | Size |
|---|---|---|---|---|
| 1 | Templates store VRChat category, languages, tags (CalendarTemplateContracts.cs; CalendarVRChatRequests.cs sends them). Category defaults to hangout | Nothing pushes people to fill tags/languages; no fixed series name (title copied, then editable) | No | Small |
| 2 | Duplicate finder for Discord events only (Core/Calendar/DiscordEventDuplicates.cs: same plain title, starts within 15 min; GET /api/calendar/discord-duplicates) | No check when saving a Modbot event; unknown whether the 2026-10-07 copies were made in Modbot or by hand in VRChat | No | Small (Modbot events) / Medium (also VRChat-side) |
| 3 | GroupInstanceSync polls group instances every 10 s; CalendarFirstJoinVRChatPost auto-posts "{title} has started." + join link for calendar events (no draft, no approval, no picture). Marketing drafts + ManagePosts exist | No draft for a Host to approve; nothing for non-calendar instances | No (instances, instance info, group posts all already used) | Small–medium |
| 4 | Instance join/leave presence facts kept forever by default (FactRetention.cs) | No "regulars" or return-rate definition or report; calendar attendance not checked | No | Small–medium |
| 5 | Gallery list/remove (GroupGalleries.cs); picture upload POST /file/image (VRChatPictureUploads.cs, 1/60 s) | Adding an image to a gallery: AddGroupGalleryImage is in the SDK but unused; approval flow unclear; VRChat+ need unknown; Discord submission intake | YES: AddGroupGalleryImage (rate limit must be asked) | Medium |
| 6 | Requests page reads join requests live (GetGroupRequests); bans, case files, notes stored; age-verification status stored on VRChat users (AgeVerificationStatusLastSeen, Is18PlusVerified) | Requests rows don't show bans, notes or age status | No (if using stored data) | Small |
| 7 | Role pairs VRChat role ↔ Discord role with who-decides, preview, disagreement facts (Discord/Sync/RoleSync.cs) | No rank label, no grouped mismatch view, Regent has no role | No | Small (label) / Medium (Ranks page) |
| 8 | Gateway gets RoleUpdated/GuildMemberUpdated; mod log shows "Role changed"; staff-role mapping refuses Administrator | No server-wide watch or alert for dangerous permissions | No (Discord only) | Small |
| 9 | Has: giveaways (posts + reaction entry), automod, mod log, smart welcome, verification/join gate, /remindme | Lacks: birthdays, member tickets, self-assign role menus, polls, leveling, starboard | — | Varies |

## User's answers
- 1a: empty VRChat tags or language on save → warn, let them save (A).
- 1b: series name → editable, with a warning when it differs from the template (B).
- 2a: duplicate on save → warn, let them save (A).
- 2b: duplicate check also covers events already on VRChat's group calendar (B).
- 3a: which instances get a draft → a setting: calendar-opened / any group instance / opened from Modbot.
- 3b/3c: Host gets a Discord ping with a link to the draft (A), unless a setting makes it post automatically (then no draft, no ping).
- 4a: a return = any visit to one of the group's instances (A).
- 5: both A (staff approve in Modbot/Discord, Modbot adds the picture: needs new AddGroupGalleryImage) and B (members submit in VRChat, Modbot lists the queue). Rate limit for AddGroupGalleryImage: asked (proposal: share groups.gallery.write, 0.1 req/s, unmeasured).
- 5 rate limit: AddGroupGalleryImage shares groups.gallery.write (0.1 req/s with removing) (A).
- 6: bans, notes, 18+ status shown on each row of the Requests list (A), from stored data (no new reads).
- 7: rank label on existing role pairs + one out-of-step view (A).
- 8: permission watch alerts staff (A), with an optional setting to also remove the permission automatically (B), off by default.
- 9: take over all four bot jobs: self-assign role menus, member tickets, birthdays, polls (polls overlap batch 4 #3 voting).
