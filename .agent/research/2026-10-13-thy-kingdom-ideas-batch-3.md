# Thy Kingdom ideas, batch 3 (2026-10-13): research only

Sent by the Thy Kingdom Driver session on 2026-10-13. The user picked these. The same rules as
batch 2 apply: check what Modbot already has, research each idea, and build nothing until the user
OKs a plan. Keep each feature generic, with Thy Kingdom as the first user.

1. **One-step 18+ verification.** One action does all of this:
   - gives the Discord role "Verified 18+" and removes "❓ Not Age Verified";
   - gives the VRChat group role "18 Plus Verified";
   - logs who did the verifying;
   - deletes any ID image afterwards, so no ID is stored.

   Context: the handbook's age rules (`G:\Thy Kingdom\handbook-draft.md` §9, around line 398) and
   the ID cleanup checklist (`G:\Thy Kingdom\companions\G-id-cleanup-checklist.md`).
2. **"Open a second instance" alert:** ping staff when a group instance is nearly full. Busy
   hangouts reach 50 or more people.
3. **Event recap:** after each event, post the turnout, a photo thread and the next date. Pairs with
   batch 2 #5 (gallery queue).
4. **Member milestones:** mark a member's 1st and 5th event (5th makes them a "Regular"), and send a
   welcome-back to anyone returning after 60 or more days away. Pairs with batch 2 #4
   (regulars and return rate).
5. **Partner tracker:** partner groups, their contacts, the last collab, and the assigned Envoy.
   Envoy is a new Guild job: a member who represents Thy Kingdom to another group.
6. **/term and /rule lookup:** answer from the glossary tab (draft:
   `G:\Thy Kingdom\companions\J-glossary.md`) and the member rules
   (`G:\Thy Kingdom\companions\D-member-rules.md`), for example `/term Regent`. The source docs
   will live in Google Docs, so check how Modbot could read them (an export or a synced copy).
7. **Yearly moderation numbers:** build the yearly report of warnings, kicks, bans and appeals
   automatically. The handbook promises yearly numbers, following the Santa Clara Principles.

## Findings (2026-10-13, read from origin/master b703abf0; not re-verified line by line)

| # | Already in Modbot | Missing | New endpoint? | Size |
|---|---|---|---|---|
| 1 | Sticky 18+ flag with who/when (VRChatUser.Is18PlusVerified; PUT .../age-verified, EditAgeVerification, fact names the moderator); LinkedRoles gives the Discord 18+ role from it; AddGroupMemberRole already used | Removing "Not Age Verified"; giving the VRChat 18+ group role; Modbot stores no image bytes, so "delete ID" means deleting the Discord message (new Discord call) or nothing | No VRChat; maybe a Discord message delete | Small–medium |
| 2 | Alert watcher "instance-filling" (statistical, AlertChecker.cs); head counts every 30 s (InstanceHeadCountSync) | A fixed "nearly full" threshold setting; capacity not stored (never hardcode it) | No | Small |
| 3 | Event end time; instance peak head count; group post sender reusable | Per-person attendance; Discord thread creation | Discord thread (new) | Medium |
| 4 | Presence facts; SmartWelcome DM; list roles could give "Regular" | Milestone/Regular/welcome-back logic; attendance linked to calendar events | No | Medium |
| 5 | Nothing similar (OtherGroupName only names) | Partner entity, contacts, last collab, Envoy | No | Medium |
| 6 | 19 slash commands; Google service-account key (Calendar scopes only); AI chat tools | /term, /rule; Docs reading needs a Drive/Docs scope + sharing, or an admin-edited copy in Modbot | Google Docs API (new) if reading Docs | Small–medium |
| 7 | Facts + daily totals for bans, unbans, removals, instance kicks, warns (TeamAnalyticsQuery) | Appeals (only an "Appeal upheld" unban reason); yearly report; public page | No | Small (staff count) / Medium (public page or real appeals) |
