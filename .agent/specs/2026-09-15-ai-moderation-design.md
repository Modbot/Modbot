# Modbot — AI Moderation (Settings → AI → Moderation)

- **Date:** 2026-09-15
- **Status:** Built, first version
- **Covers:** term lists (local and from Modbot Hub), AI topics, flags, dismissals, Discord actions,
  the "Try it" box
- **Depends on:** M8 §2 and §4 (as changed 2026-09-15), M5 §5.1 (messages stored in full),
  foundation §4.2.7 (Modbot Hub term lists), AI base settings (`IAiClients`)

---

## 1. What it does

Modbot reads text a group already has — Discord messages and VRChat profiles — and checks it
against rules the group wrote or chose. A match is a **flag**: a note a moderator reads and either
keeps or dismisses. A rule the operator has set to act can also delete the Discord message and time
the author out.

Everything ships off. There is one switch for the whole feature, each rule has its own switch, and
every rule starts as flag only.

## 2. Rules

There are two kinds of rule.

- **Term list.** A named list of terms. A term is a word or phrase matched as a whole word, a
  word or phrase matched anywhere (contains), or a regular expression.
  - **Local lists** are written by the operator and edited freely.
  - **Cloud lists** come from Modbot Hub (`my.modbot.co/termlists/{id}.json`). They are read-only
    here, apart from the operator switching individual terms off. A cloud list also carries the
    Hub's combination rules (all of / any of / none of, within N words), which a local list does not
    offer.
- **AI topic.** A name and, in the operator's own words, what to catch. Each topic has a
  sensitivity: *low* (only clear cases), *medium*, *high* (anything that could reasonably be it).

Every rule has **targets** (§3) and an **action** (§6). The Hub's `ai_topics` list is not offered as a
cloud term list; its topics are prompts, not terms. Copying Hub topics into local topics is not built.

## 3. Targets

| Target | Text checked |
|---|---|
| `discordMessage` | The message text, on arrival and on each edit |
| `displayName` | VRChat display name |
| `bio` | VRChat bio |
| `status` | VRChat status text (`statusDescription`) |
| `pronouns` | VRChat pronouns |

A Hub rule's own `fields` narrow this further. A Discord message counts as `bio` for that purpose,
because both are free text a person wrote; a rule that names only `displayName` (impersonation,
for example) does not run on chat.

## 4. Matching

### 4.1 Term lists

Text is made comparable before matching: lower case, invisible characters removed, runs of spaces
collapsed, full-width and "fancy text" letters (bold, script, circled, squared) turned into plain
ones, accents removed, and common look-alikes folded (`0→o`, `1→i`, `3→e`, `4→a`, `5→s`,
`7→t`, `@→a`, `$→s`, and Cyrillic and Greek letters drawn like Latin ones). Terms go through the
same steps.

This is done with tables rather than Unicode normalisation, because Modbot builds with invariant
globalization and `string.Normalize` then returns non-ASCII text unchanged. The tables cover what
people type to get past a filter, not all of Unicode.

- **Whole word** needs a non-letter, non-digit character (or the edge of the text) on both sides.
- **Contains** matches anywhere, and so also matches inside longer words. That is the operator's
  choice to make.
- **Regular expression** is run against the text with everything but accents and look-alikes done,
  and then against the fully folded text. It is compiled with the non-backtracking engine where the pattern allows it, and
  with a **100 ms match timeout** always. A timeout counts as no match and is logged. Patterns are
  checked when saved; one that does not compile is refused.
- **Combination** (Hub only): every `allOf` word and at least one `anyOf` word present within
  `withinWords` words of each other, and no `noneOf` phrase anywhere in the text.

### 4.2 AI topics

- **Term lists run first.** AI runs only on text no term list matched for that target. A message a
  term list already flagged does not also cost an AI call.
- **One call per text.** All enabled topics for that target go in one request.
- **Structured output.** The model must answer JSON `{ "matches": [ { "topic", "why", "quote" } ] }`
  (M8 §4.1: what it flagged and why). A match whose `quote` is not actually in the text is thrown
  away, so a flag always points at words the person wrote.
- The text is sent as data inside the user message. The system message says it is untrusted and
  that instructions inside it are to be ignored.
- **Daily AI call limit.** One setting, counted per UTC day from `IModbotClock`, incremented in the
  same statement that checks it. At the limit, AI topics stop until the next day; term lists keep
  running. The "Try it" box counts towards the limit, because it costs the same.
- **Usage and spend limits (added 2026-09-15).** Every AI topic call records its input, output
  and cached token counts, the model, and OpenRouter's reported cost where there is one, in the
  shared `ai_usage` table (`Modbot.AI/Usage`), under feature `moderation` with no user. Before each
  call the engine asks `IAiUsage.LimitReachedAsync`, which checks the limit for everyone and
  moderation's own daily and monthly limits, both in money (AI chat design §10.3). At either, AI
  topics stop, the limit's sentence is the reason AI was skipped, and term lists keep running.
  Moderation first shipped with a monthly token limit because Modbot had no prices; that becomes a
  money limit once the model has a price, and until then it is kept and counted (AI chat design
  §10.5). Limits are set on Settings → AI → Limits.

## 5. Flags and dismissals

A flag records the rule, the term or topic, the target, the person, the Discord message and channel
where there is one, the matched text, and the reason (the Hub note for a term, the model's "why" for
a topic).

- **No repeats.** The same rule and term on the same message, or the same rule, term, target and
  matched text on the same profile, is flagged once.
- **Dismissal is permanent for that person** (M8 §4.4). A dismissed flag suppresses that rule and
  term (or topic) for that person from then on: no new flag, and no action.
- **Dismissal rate** is shown beside every list and topic as flags and percent dismissed, so a rule
  that is mostly noise is visible as noise.

**Seeing flags** needs *See profiles* (M8 §4.1: a flag is a note on the profile). **Dismissing** needs
*Review tickets*, the existing permission for closing a question about somebody. **Managing rules**
needs *Change settings*.

## 6. Actions

M8 §2: AI moderation rules may act on Discord chat, and never on VRChat.

- A rule's action is **flag only** (default), or for Discord messages: **delete the message**, and/or
  **time out the author** for a set number of minutes (Discord allows up to 28 days).
- Setting a rule to act records **who set it and when** on the rule. Changing the action again
  records the new person; setting it back to flag only clears it.
- When several matched rules act on one message: the message is deleted once, and the timeout is the
  longest any of them asked for.
- Actions go through the bot's live gateway (`DeleteMessageAsync`, `TimeOutAsync`). No new gateway
  intents: deleting by id and timing out through a single member lookup need none. With the bot
  offline the flag is still written and the action fact says it did not happen.
- Profile targets never act. VRChat actions are never taken on a flag.

## 7. Facts

Nothing here is changed in place; everything is a new fact.

| Type | Subject | Actor | Log |
|---|---|---|---|
| `modbot.ai-moderation.flag` | The person (Discord or VRChat) | none | Moderation |
| `modbot.ai-moderation.message-delete` | The author (Discord) | none | Moderation |
| `modbot.ai-moderation.timeout` | The author (Discord) | none | Moderation |
| `modbot.ai-moderation.flag.dismiss` | The person | The Modbot account | Moderation |
| `modbot.ai-moderation.rule.change` | The Modbot account | The Modbot account | Operational |

The two action facts carry the rules that asked for the action, the message and channel, and for each
rule the operator who set it to act. They have no actor because nobody pressed a button at that
moment; the operator is named in the data instead, which is the M8 §2 requirement. They also record
whether Discord accepted the action and, if not, why.

All are moderation retention (kept), including the rule change: "who switched this rule to delete
messages" is history.

## 8. Where the engine is called

`Modbot.Core.Moderation.IModerationChecker`, implemented in `Modbot.AI.Moderation`:

- `CheckDiscordMessageAsync(DiscordMessageToCheck, ct)` — for Discord message indexing, on each new
  and edited message with text. Not wired yet: message indexing is not on master.
- `CheckProfileAsync(ProfileToCheck, ct)` — for profile text. Wired by a background job that reads
  `vrchat.user.profile.first-seen` and `.changed` facts after a stored position and checks the stored
  profile, so the profile sync itself is not slowed by AI calls. While the feature is off the position
  does not move, so switching it on checks the profiles seen in between.

Both return what matched and what was done. Both do nothing when the feature is off. A fallback that
does nothing is registered in Core, so a process without `Modbot.AI` can still call it.

## 9. Cloud list updates

Foundation §4.2.7 says Hub updates are never applied silently, because a changed list changes what
gets flagged — and here, what gets deleted. So:

- A job checks each subscribed list every six hours; **Refresh** does the same on demand.
- A newer version is stored as *available*, with the rules added, removed and changed counted.
- **Update** applies it. Switched-off terms stay switched off by rule id.

## 10. Try it

Paste text, pick a target, optionally include AI topics. The answer lists each matching rule, the
term or topic, the matched text, and what would happen (flag, delete, timeout, or suppressed for
nobody — "Try it" has no person). Nothing is written and nothing is done, apart from the AI call
counting towards the daily limit.

## 11. Not built

- Copying Hub AI topics into local topics.
- Notifications for flags.
- Checking every stored profile on demand.
- Per-channel rules (a list for one channel only).
