# Modbot — AutoMod (Settings → AutoMod)

- **Date:** 2026-09-17
- **Status:** Built, first version
- **Covers:** the split of automated moderation from AI, the AutoMod tab, the actions a rule can
  take (Discord and VRChat), the AI tools a group lets AutoMod use, the AI's opinion on a flag
- **Depends on:** the AI moderation design (2026-09-15) for everything it does not change — term
  lists, matching, targets, flags, dismissals, test sets, the trial, the automatic pause, scope,
  rule versions, message context, pictures, the language on a flag, sending a flag to Reviews —
  M8 §2 and §4 (as narrowed here), M4 §4 (`GroupModeration`), Reviews (spec 5.8.5)
- **Supersedes:** the AI moderation design's §1, §6, §8 and its title; see §9 below for the list

---

## 1. Why

Everything that checks text against a term list ran inside `Modbot.AI`. It never needed an AI
client: matching a word, normalising look-alikes, writing a flag, pausing a runaway rule and
keeping rule versions are things Modbot does on its own server, and it did them on deployments
that had no AI provider at all. The settings screen said the same thing the wrong way round —
**Settings → AI → Moderation** held term lists under the AI tab, so an operator who never meant to
send anything to a model found the feature under the one heading that says "this sends things to a
model".

AutoMod is the same feature with the AI taken out of its name and out of its project. Term lists
are the feature; AI topics are what AI adds to it when AI is on.

## 2. The split

| Project | Holds |
|---|---|
| `Modbot.Moderation` (new) | `ModerationEngine`, `TermMatcher`, `NormalisedText`, `StoredTerm`, `HubTermLists` and its refresh loop, `RuleGuards`, `RunawayGuard`, `MessageContext`, `FlagReviews`, `TextLanguage`, `ProfileModerationPass` and its loop, `AutoModAiTools`, `IAiRuleChecker`, `PictureAttachments` |
| `Modbot.AI` | `TopicClassifier`, `ModerationPictures` (the fetching), `TopicRuleChecker` (the `IAiRuleChecker`), `AiCallAllowance`, `FlagReviewer` |
| `Modbot.Core` | The entities, `IModerationChecker`, `IDiscordModerationActions`, and now `IVRChatModerationActions` |
| `Modbot.VRChat` | `AutoModVRChatActions`, over `GroupModeration` |

**Its own project, not Core.** Core is the entities and the contracts every other project builds
on; the engine writes facts, opens reviews and calls out to Discord and VRChat, which is a
different weight of thing. `Modbot.Moderation` depends on Core and Analytics (for the fact writer
and `ReviewFacts`) and on nothing else. `Modbot.AI` depends on it, never the other way round.

**The engine asks the AI through one interface.** `IAiRuleChecker.CheckAsync` takes the pieces of
text no term list matched, each with the topics it is checked against, the messages before it and
the pictures that belong to it, and answers hits, the model, why it stopped, and what each call was
sent. `Modbot.AI` registers `TopicRuleChecker` over the "AI is off" fallback the moderation project
registers; a process without `Modbot.AI` runs term lists alone and says "AI is off." where a topic
would have run. The daily AI call limit, the spend limits, the batching, the prompt and the reading
of the answer are all behind that interface, because they are all about the call.

**Language detection stays out of AI.** `TextLanguage` is an offline detector built into the
binary. It is what the flag records whether the rule was a term list or a topic, so it belongs with
the engine.

**Registration.** `AddModbotModeration()` registers the engine and everything it needs with
`TryAdd`, so a host may call it before or after `AddModbotAi()`; `AddModbotModerationJobs()` adds
the two loops. The Discord bot, the VRChat gate and `Modbot.AI` each replace one fallback when
they run in the process.

### 2.1 What was renamed, and what was not

Renamed, because the old name said "AI" for something that is not:

- `settings.ai_moderation_enabled` → `automod_enabled`;
  `ai_moderation_profile_facts_read_through` → `automod_profile_facts_read_through`
- tables `ai_term_list`, `ai_flag`, `ai_test_sample`, `ai_test_run`, `ai_rule_version` →
  `automod_*`, indexes with them; `ai_topic` keeps its name, because a topic is AI
- `/api/settings/ai/moderation` → `/api/settings/automod`; the old path is rewritten to the new
  one before routing, so a client written against it keeps working and the API document lists each
  route once
- the web hash `#ai/moderation` → `#automod`, with the old one opening the new tab
- `FactType.AiModeration*` constants → `FactType.AutoMod*`

Kept:

- **The fact type strings** keep their `modbot.ai-moderation.` prefix. Every stored fact, the
  Discord event routes' moderation category and the live page's flag filter match on it, and
  renaming a stored type across the partitioned event table would buy a reader nothing.
- `ai_moderation_daily_call_limit`, `ai_moderation_calls_day`, `ai_moderation_calls_used`,
  `ai_moderation_profile_batch_size` are about AI calls and keep saying so.
- The Cloud report's `aiModerationEnabled` wire field. Cloud is deployed separately from the
  servers that report to it, and a server on either side of this change has to keep reporting; it
  now carries `automod_enabled`.

## 3. The tab

**Settings → AutoMod**, right after Moderation. It holds:

1. **The AutoMod card**: the one switch.
2. **Try it**, as before. "Include AI topics" appears only while AI is on.
3. **Term lists**, local and from Modbot Hub, as before.
4. **The AI section**, shown only while AI is switched on under Settings → AI → Base (the same
   `settings.ai_enabled` that card saves, carried on the response as `aiEnabled`):
   - **AI topics**, moved from the old screen.
   - **AI tools** (§6), with the daily AI call limit and today's count beside them.

With AI off the section is not disabled or greyed: it is not there. A hidden card cannot be
misread as a broken one, and there is nothing on it a person could use.

Settings → AI loses its Moderation sub-tab. The AI tab is now what its name says: where AI is set
up and what AI features cost.

## 4. The engine with AI off

`ModerationEngine` reads three switches once per check: `automod_enabled`, `ai_enabled` and the
AI tool switches (§6). Term lists run first as before. Where a topic would run:

- AI off → `IAiRuleChecker` is not called; the outcome says "AI is off."
- AI on, "check text against AI topics" off → not called; the outcome says the tool is off.
- AI on, tool on → called. Pictures are handed over only when the picture tool is on as well.

The fake checker in the tests counts its calls, which is how "never called" is proved rather than
asserted.

## 5. Actions

A rule's action is any of:

| Action | Applies to | Through |
|---|---|---|
| Flag | every target | always |
| Send to Reviews | every target | `open_review_for_each_flag`, as in §19 of the AI moderation design |
| Delete the Discord message | Discord messages | `IDiscordModerationActions` |
| Time out the author | Discord messages | `IDiscordModerationActions` |
| **Ban from the VRChat group** (new) | display name, bio, status, pronouns | `IVRChatModerationActions.BanFromGroupAsync` |
| **Remove from the VRChat group** (new) | display name, bio, status, pronouns | `IVRChatModerationActions.RemoveFromGroupAsync` |

Every rule still starts as flag only. Setting any action is gated on a passing test run, starts the
trial, is limited by scope, and the rule pauses itself if it runs away — the VRChat actions earn
nothing the Discord ones did not have to.

**A rule never acts across platforms.** Discord actions are taken when the match is a Discord
message; VRChat actions when it is a VRChat profile. A rule that ticks a group ban with only
Discord messages as its target is refused on save, as a delete with only profile targets already
was. A bio is not a message to delete, and a Discord author is not a group member to ban — linking
the two accounts is a different feature, and a rule that reached across it would act on somebody
the match never named.

**The VRChat implementation.** `AutoModVRChatActions` in `Modbot.VRChat` goes through
`GroupModeration`, the wrapper the Kick and Ban buttons use, so the endpoint class, the interactive
priority and the `...WithHttpInfoAsync` rule are decided in one place; nothing is recorded as done
unless VRChat accepted, and a 429 is never retried. It refuses to act on the account Modbot signs in
as, for the reason the button does. It does not share the button's confirmation key or its case
file: a rule has no dialog to press twice, and its flag and action fact are its record. Like the
Discord actions it does not update the stored member and ban rows; the sweeps confirm what
happened on their next pass, as they confirm everything else.

**Ban and remove together.** A ban takes the person out of the group as well, so when a rule asks
for both and the ban worked, the removal is not sent: it would be a second request for something
VRChat has already done. If the ban failed, the removal is still tried.

**Facts.** Two new types, `modbot.ai-moderation.group-ban` and `.group-remove`, shaped like the
two Discord action facts: no actor, the rules that asked with the operator who set each to act, the
flag ids, and whether VRChat accepted. Moderation retention. The flag row records `group_banned` /
`group_removed`, and during a trial `would_group_ban` / `would_group_remove`; the runaway check
counts all four kinds of action.

### 5.1 Not built

- **Kick from an instance.** There is no service for it: M4 left instance kick open, and whether
  it needs presence in the instance is still unanswered. Nothing here calls the VRChat SDK
  directly, so the action waits for the service.
- **Warn, note, watch** (M4's Modbot-side actions) — not built as actions anywhere yet.
- **Acting on a linked account** across platforms (see above).

## 6. The AI tools

Four switches, stored as JSON in `settings.automod_ai_tools` the way Chat's tool switches are:
only switches somebody moved are stored, so a tool added later starts where its kind should.

| Tool | Key | Starts | Checked where |
|---|---|---|---|
| Check text against AI topics | `classify_topics` | on | the engine, before building the request (§4) |
| Check pictures | `check_pictures` | on | the engine, when gathering a text's pictures |
| Give an opinion on a flag | `review_flag` | on | the flag endpoint, before the reviewer is asked |
| Propose an action for a flag | `propose_action` | off | the flag endpoint, which asks for an action only when on |

The rule is the one Chat has: **a switched-off tool is never called.** The switch is checked where
the tool would run, not only on the settings page, and the tests prove it with a fake that counts.

The first two default on because they are what AI topics were before the switches existed: a
deployment that had topics running keeps them running. The opinion defaults on because it does
nothing until a moderator presses a button. The proposal defaults off because it shapes what a
moderator does next, and that is a thing a group should choose.

### 6.1 The opinion

`FlagReviewer` (`Modbot.AI`) sends the model the rule and its text at the version that flagged,
the words that matched, the kind of text, and the member's text — the stored message with the
messages that went with it, or the profile field as stored now — between two lines of a marker that
is different every request, exactly as a topic check sends it (AI moderation design §15). The
model answers `{ "verdict": "keep" | "dismiss", "why": "..." }` as strict structured output, and,
when the proposal tool is on, `"action"`: one of `none`, `delete_message`, `timeout`, `group_ban`,
`group_remove`. An answer of any other shape is thrown away whole.

The opinion is stored on the flag (`ai_opinion`, `ai_opinion_reason`, `ai_proposed_action`,
`ai_opinion_at`, `ai_opinion_call_id`) and, when the flag has a review, added to the review's
evidence so the person closing it reads it beside the words. The call keeps its prompt and answer,
because somebody pressed a button.

**It is advice.** M8 §4.1 stands: the opinion changes nothing by itself, the proposal is never
carried out by Modbot, and the buttons a moderator presses are the ones that were there before. The
opinion needs *Review tickets*, the permission for deciding a flag, and one AI call under the same
limits as everything else.

### 6.2 What "with a moderator approving it in Reviews" means here

The proposal shows on the flag's review; closing the review as "the rule was right" or "wrong"
decides the flag as it always did. Carrying the proposed action out is the moderator's own press of
the existing Kick, Ban or Discord buttons. An "approve and do it" button would be Modbot acting on a
model's say-so with one click between, and M8 §2's line — a human in the group decides — is a
line, not a checkbox.

## 7. Facts, for reference

| Type | Change |
|---|---|
| `modbot.ai-moderation.flag` | now also carries `wouldGroupBan`, `wouldGroupRemove` |
| `modbot.ai-moderation.group-ban` | new |
| `modbot.ai-moderation.group-remove` | new |
| `modbot.ai-moderation.rule.change` | `settings` changes now carry `aiTools`; rule changes carry `groupBan`, `groupRemove` |

## 8. API

- `GET/PUT /api/settings/automod` — the old `/api/settings/ai/moderation` is rewritten to it. The
  response carries `aiEnabled` and `aiTools`; the update takes `enabled`, an optional
  `dailyAiCallLimit` and optional `aiTools` switches.
- Term list and topic inputs and views carry `groupBan`, `groupRemove`; the trial carries
  `wouldGroupBan`, `wouldGroupRemove`; Try it answers `wouldGroupBan`, `wouldGroupRemove`.
- `POST /api/moderation-flags/{id}/ai-opinion` — *Review tickets*; 409 when AI is off or the tool
  is. The flag list carries `aiOpinionAvailable` so the page knows whether to show the button.

## 9. What this narrows or replaces in the AI moderation design (2026-09-15)

| There | Now |
|---|---|
| Title and §1: "AI Moderation (Settings → AI → Moderation)" | AutoMod, on its own tab (§3). The AI moderation design remains the reference for everything in its §2 to §5 and §9 to §19. |
| §6 Actions: "M8 §2: AI moderation rules may act on Discord chat, and never on VRChat"; "Profile targets never act. VRChat actions are never taken on a flag." | Replaced by §5: a rule may ban or remove from the VRChat group on a profile match, under the same gate, trial, scope and pause. Discord actions still act on Discord messages only. |
| §8: "`Modbot.Core.Moderation.IModerationChecker`, implemented in `Modbot.AI.Moderation`" | Implemented in `Modbot.Moderation`; the AI half is `IAiRuleChecker` (§2). |
| §4.2: AI topics run whenever AI is on | Only while the topic tool is on (§6); pictures only while the picture tool is. |
| M8 §2: "Actions on VRChat (kicks, bans, group removal) are still never taken on an AI signal." | Narrowed: a group's own AutoMod rule, term list or AI topic, may ban or remove from the group once the operator has set it to act. Federation and group flags are unchanged: never. Instance kicks remain unbuilt. |
| M8 §4.1: AI review is advisory and never acts | Unchanged, and the flag opinion (§6.1) is built to it. |

## 10. Not built

- Kick from an instance (§5.1).
- Warn, note, watch as rule actions.
- Acting on a linked account across platforms.
- An "approve" button that carries a proposal out (§6.2, on purpose).
- Renaming the fact type strings and the Cloud wire field (§2.1, on purpose).
