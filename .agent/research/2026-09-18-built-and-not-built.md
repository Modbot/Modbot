# What is built, and what the specs describe that is not

- **Date:** 2026-09-18
- **Method:** a read-only pass over `.agent/specs/` (44 documents), `.agent/plans/`, `src/`,
  `docs/content/docs/` and `docs/openapi/modbot.json`, on `staging`.
- **Rules applied:** a spec's **Status** line was treated as a claim to check, not as truth — three
  overstate what shipped and two understate it. Tests were not counted as evidence of shipping;
  several suites were written the same day and had never been run.

---

## 1. What is built

| Area | What works |
|---|---|
| **Moderation** | Kick, ban and unban reaching VRChat, each with a reason from the group's own list, a one-press-one-action guard, and a fact written only after VRChat accepts. Ban case files with profile snapshots, withdrawal and evidence. The audit log with VRChat's entries beside Modbot's. Repeat offenders. Moderator pattern reviews. Person, world and instance popups, and one merged view per person. |
| **Sync** | Members, bans, group info, group audit log, worlds, group instances, instance head counts and the weekly profile refresh — all through one gate with per-endpoint lanes, priority, cold stop on 429, and a Sync health page. |
| **Discord** | The bot. Four slash commands. Account linking by Discord sign-in plus a VRChat bio code, and the two linked roles. Event routes to chosen channels. Instance announcements on a live-edited card. Message and voice history with its own retention. The My Server analytics page. |
| **AutoMod and AI** | Term lists and AI topics checked against display name, bio, status, pronouns and Discord messages. Actions up to banning or removing from the VRChat group. Test sets, trials, a runaway pause, rule versions, scope and exemptions. Per-flag AI opinion. The Flags page. AI Chat with read-only tools. Scheduled Insights. Unusual-activity Alerts. Spend limits in four scopes, and the call log. An MCP server. |
| **Analytics** | My Group, My Server, My Team, Worlds and Instances. Daily totals. The fact log with linked facts. Tiered retention that prunes by dropping partitions. |
| **Accounts and access** | Roles as named permissions. Staff accounts, invite links, reset links, required VRChat linking. Sessions with keep me signed in, and sign out everywhere. An email queue under a daily limit. |
| **Companion and overlay** | Log tailing, instance sessions, presence reporting with offline buffering and clock sync. Pairing by link. Multi-server routing with no cross-group leakage. The sent journal. Spoken announcements. A real SteamVR and OpenXR overlay with grab-and-place, curve, opacity and size. |
| **Cloud and the central site** | my.modbot.co (register, go, known servers). Modbot Cloud (accounts, server registry, claiming, event backup, server logs and alerts, showcase, subscribers, update feed, Hub term lists). The landing page. Update checking on both server and companion. |
| **Giveaways** | Rules and the builder, a preview that is honest about precision, weighting, Discord reaction entry, frozen entrant snapshots, seed promise-and-reveal, and re-draws. |
| **The API** | 195 published endpoints. API keys. Outgoing webhooks with a delivery log. Live events over WebSocket and long polling. Cursor paging. The VRChat proxy. Demo mode. |

---

## 2. Specified but not built

Ranked within each group by what a person using Modbot would actually feel.

### Never started

1. **Warning somebody, noting somebody, watching somebody** — M4 §2, §6, §8.3. The `Warn`
   permission is bit 11, is described in the catalogue, and is granted to the built-in Moderator
   role. Nothing reads it. There is no endpoint, no fact Modbot writes, and no button; the web
   app's action type is kick, ban or unban only. Notes are the same: the fact type exists and the
   importer is its only writer, so there is no way to write a note about a person from inside
   Modbot. Watch has no representation at all.

2. **Acting on many people at once** — M4 §5, M7 §3. The `BulkAction` permission is bit 12,
   catalogued, and enforced by nothing. There is no multi-select in the web app; the list
   selection helper is a single-row keyboard cursor. Nothing hands a set of people to an action.

3. **Saved segments** — M7 §2.2. No segment entity, table, migration, endpoint or page. What was
   built from M7 is §4 giveaways, plus the parts of §2, §5 and §6 a giveaway needed. The rule
   engine is generic in shape but giveaway-only in reach: every type is named for giveaways, its
   three callers are all giveaway code, a rule tree can only be stored inside a giveaway row, and
   the only way to evaluate one is the giveaway preview endpoint. Four predicate families from
   §2.1 are also missing: derived series, first seen, absolute join dates, and per-kind moderation
   counts. Export and announce-to-this-list do not exist. Building segments is a table, some
   endpoints, the missing predicates and a rename — not a second engine.

4. **Sharing flags and warnings between groups** — M8 §5, entire. No peering table, no signed pull
   transport, no per-peer trust, no in and out switches. **M8 §3, group flagging** is equally
   absent. Modbot Cloud is adjacent but unrelated: it carries accounts, a registry and log backup,
   and no moderation signals.

5. **Role sync and ban sync with Discord** — M5 §3, §4. No mapping entity, no setting, no sync job.
   More decisively, the Discord gateway has no ban and no kick method at all, so a VRChat ban
   cannot become a Discord ban today. The reverse is not wired either, and with no origin field
   §4.2's loop prevention has nothing to record. Not to be mistaken for the two fixed linked roles,
   which come from link state and read no VRChat group role.

6. **Auto-inviting linked members to the group** — M5 §2.3. The rate-limit lane is declared and
   used by nothing; the endpoint constant is declared and called by nothing. Modbot records
   invites other people made.

7. **The notification pipeline** — foundation §4.5, named as a dependency by M3, M4, M5, M6 and the
   evidence design. The notifier interface does not exist anywhere in the code. No severity
   routing, no per-person per-channel preference, no digest, no deduplication, no web push, and no
   "a critical alert nobody can receive is surfaced on next login". Four unconnected things
   shipped instead: health alerts by email, Discord event routes, in-app AI alerts, and the
   companion's alert long-poll. Every spec sentence that says "through the notifier" describes
   something absent.

8. **Launching an instance** — M6 §2. No launch endpoint, no launch screen, no saved presets, no
   staff auto-invite. The instances route is read-only. The single call site for VRChat's create
   instance is the calendar opener, which is what M6 §6 lists as a non-goal; the calendar design
   supersedes that in practice and M6 was never updated, so the two specs contradict each other
   with no note.

9. **Approving or rejecting a request to join.** Modbot ingests request created, rejected and
   blocked from the audit log and counts them, but there is no endpoint to act on one. A moderator
   sees the history of the join queue and must still go to VRChat to work it. This is on no
   deferred list and not on the not-built page.

10. **Avatar id resolution, and acting on an avatar** — M3 §7.2–§7.3, M4 §3.2. Avatar-changed facts
    carry a display name only. No file-to-avatar mapping, no provider adapter, no cache, no
    setting, so "ban everyone wearing this avatar" has no data underneath it.

11. **Verbose-logging flag onboarding in the companion** — M3 §2.3.0–§2.3.3. The launch-flag string
    appears nowhere in the code or the docs. No store-marker check, no copy button, no first-run
    blocker. The consequence is the one §2.3.3 asked to prevent: the client cannot tell missing
    flags from a changed log format, and guesses between them in one sentence.

12. **Tracked Groups** — foundation §10.3. Recorded as deferred, and the navigation carries a
    comment saying it has no entry until it exists.

13. **Evidence record-keeping** — evidence storage §6, §7.1, §14.1. The attached, accessed and
    destroyed fact types exist, are labelled, are categorised for the audit page and are routed to
    Discord, and **nothing ever writes them**. §14.1's "every view and every download writes an
    accessed fact" is not true of any view or download. The columns §7.1 names — reference count,
    state, last verified — do not exist, so the integrity sweep and the sampled existence check
    cannot exist either. Storage is solid; the chain of custody around it is not recorded at all.

### Partly built

14. **Destroying evidence.** The endpoint exists, is gated and works. The web app has no client for
    it at all. The harder half: the destroyer refuses while any report references the bytes, and
    **there is no detach** — no endpoint, no method, no fact type. So anything attached to a case
    file cannot be destroyed by any route, the API included. Only an orphan blob is destroyable.

15. **Removing everything stored about one person.** The purger is complete and careful: facts,
    counted-only daily totals, Discord messages with their earlier versions, standing giveaway
    entries, blanked frozen entrants, days recomputed. Its only reference in the solution is its
    registration. No endpoint, no button, no caller.

16. **A list of paired devices** — M3 §4. The list and revoke endpoints exist and return everything
    the spec asks for. The web app has no screen, no client and no settings card.

17. **Accountability at the moment of action** — M4 §8.1. The confirmation offers reason buttons and
    a note, but does not show "fourth kick in 30 days by three moderators" and does not nudge with
    "only you have ever actioned this person". The data exists; the prompt at the point of decision
    does not. Two adjacent halves are also missing: reviews open automatically but never
    auto-resolve (§8.2), and reversal classification — mistake, appeal upheld, sentence served
    (§9) — has no field.

18. **Modbot Hub term list updates** — foundation §4.2.7. Lists, schema, index, import, version
    comparison and explicit apply are all built. Missing: the scheduled check, so an operator must
    press refresh per list and the notification §4.2.7 describes has nothing to raise it; and the
    onboarding step that selects default lists.

19. **Instance monitoring writes no facts of its own** — M6 §3. The instance sync writes rows and
    takes no fact writer, so the created and closed facts come only from VRChat's audit log.
    Instances no moderator stood in are in the tables but absent from the fact stream, which is the
    case the poll exists to cover.

20. **Evidence dedup across case files** — §5.3, §6. One row per hash carries a single report id,
    filled only when null, so the same bytes attached to a second case file stay attached to the
    first and never appear on the second. The references query can therefore return at most one
    report, which breaks §6's promise to name every report a destroy would affect. Also missing:
    EXIF disclosure, the sweep of final objects nothing references, and the optional evidence
    hostname.

### Deliberately deferred, with the reason recorded

- **Incoming webhooks** — an API key already does the job, and an incoming URL is a credential
  logged by every proxy in between.
- **Email when AI spend nears a limit** — reaching a limit *does* email ticked staff accounts
  through the health alert check. Only the nearing warning is absent. The not-built page is
  accurate in wording and misleading in effect.
- **Instance kick** — no service exists, and whether it requires presence in the instance is
  unanswered.
- **Discord sign-in, and requiring it** — the Discord id on an account is typed in, not proven.
- **Invite links by email**, and **forcing a password change after a temporary one**.
- **Publisher pinning, the installer, and the clean-machine release gate** — waiting on a signing
  identity; the release workflow says so when the secret is absent.
- **Choosing a release by API compatibility** — dropped by the update-checking design §9.
- **A per-person presence daily total** — named in the giveaways spec as the single largest
  improvement available, because it would lift the retention refusal for the rule people most want.
- **Stopping an AI reply part-way when it crosses a spend limit** — the check is pre-call only.
- **Copying Hub AI topics into local topics**, **notifications for flags**, **checking every stored
  profile on demand**.
- **Trends over the stored server logs.**

---

## 3. Where the documentation is wrong

### Pages that tell a moderator the opposite of the truth

1. **Members and bans says Modbot cannot moderate at all.** The callout says there are no kick,
   warn, ban or unban buttons and that those permissions do nothing yet. Kick, ban and unban
   shipped, and the same page documents them further down. This is the page somebody opens to do
   the job.
2. **Security still carries the session claim** corrected elsewhere: fourteen days, extended with
   use. Without the box a session dies on browser close; with it there is a hard thirty days that
   does not slide.
3. **Flags says a rule can never act in VRChat.** A rule can ban and remove from the group, the
   Flags screen renders badges for it, and another page documents it.

### Wrong numbers and wrong places

4. **The AutoMod runaway threshold** is documented as four times a rule's hourly average; the code
   uses sixteen.
5. **An environment variable named in the docs does not exist**, and the one Cloud requires is
   undocumented. Cloud refuses to start without it.
6. **Erasing a person is promised in four pages** and cannot be done; the not-built page correctly
   says so, so the site both promises and denies it.
7. **Two pages send a reader to the old AutoMod tab** under AI.
8. **Four places name a Settings tab by its id** rather than its label.
9. **The health page describes eight cards; there are fourteen.**
10. **The companion install page says five screens; there are six.**
11. **The popup page describes the JSON tab as six panels**; it is one document.
12. **Three pages say Settings needs change settings**; it now opens for manage users or manage
    roles too.
13. **The alerts page links a daily email limit to a page that never mentions one.** The limit is
    real and documented nowhere.
14. **Three permission names in the docs do not match the labels in the app.**
15. **A debug environment variable is missing** from the page that promises every variable.
16. **The chat page describes deleting an account**, which cannot happen; accounts are disabled.

### Built, documented nowhere

17. **The proxy VRChat images switch**, which changes how every picture loads.
18. **Theme and density, including a VR density** that changes hit targets, type size, border
    weight and contrast for reading through a headset. The whole docs site returns nothing for
    theme, density or dark mode.

---

## 4. The shape of it

M0 through M3 are substantially real. M4 shipped three verbs of roughly eight. M5 shipped its
analytics and linking and none of its sync. M6 shipped watching and announcing and none of
launching. M7 shipped giveaways and none of segments. M8 shipped AutoMod and none of federation or
group flagging. The foundation's notification pipeline, named as a dependency by five later specs,
was never built, and each feature grew its own delivery instead.

The pattern most worth acting on is not the milestone count. It is the three places where a
capability is fully written and cannot be reached: the purger with no caller, evidence destroy with
no detach and no button, and the evidence facts that are declared, labelled, routed and never
written. Those are the cheapest gaps to close, and the ones most likely to be believed already done
— by a reader of the code as much as by a reader of the docs.

The documentation problem is not neglect either. It is lag at the exact points where something
shipped: six of those pages were correct when written and were overtaken. The cheap habit that
would catch most of them is to grep the docs for the old claim when a feature lands, rather than
only adding the new page.

## 5. Two process notes

- `.agent/plans/` holds one file, the M0 plan. Everything from M1 onward was built without one, so
  CLAUDE.md's description of that folder is aspirational rather than descriptive.
- Two shipped features have no spec at all: the **MCP server**, whose own code cites a design
  document that is not in `.agent/specs/`, and the companion's **spoken announcements**, documented
  on the site but never designed on paper.
