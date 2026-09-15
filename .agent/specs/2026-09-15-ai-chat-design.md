# Modbot — AI Chat

- **Date:** 2026-09-15
- **Status:** First version built
- **Covers:** the Chat page, the tool-using loop behind it, its settings (Settings → AI → Chat),
  the `UseAiChat` and `UseAiPastLimits` permissions, and AI usage and spend limits (Settings → AI →
  Limits)
- **Depends on:** M8 §2, §4 and §6; accounts and access §3; AI Base settings (`IAiClients`)

---

## 1. What it is

A moderator asks a question in plain words — "has usr_… been banned before?", "who is in our
instances right now?", "how many people joined this week?" — and gets an answer written from
Modbot's own data. The model does not know that data; it asks for it through **tools**, each of
which is a read Modbot already serves to the web app.

It is a faster way to look things up. It is not a moderator.

## 2. The loop

One reply is a loop over the OpenAI SDK's chat completions, streamed:

1. Send the system prompt, the conversation so far and the tools on offer.
2. Stream the answer. Text goes to the browser as it arrives.
3. If the model asked for tools, run each one, send the results back, and go to 1.
4. If it did not, the reply is finished.

Limits, all from settings:

| Limit | Default | Range | What happens at the limit |
|---|---|---|---|
| Tool calls per reply | 8 | 0–50 | Further calls get "limit reached" as their result, and the next round is sent with no tools, so the model has to answer with what it has. |
| Reply length (tokens) | 2000 | 256–32000 | Sent as `max_completion_tokens` on every round. |
| Time limit (seconds) | 120 | 10–600 | The whole reply is cancelled. What was stored so far stays. |

Also fixed in code: a message is at most 4000 characters, a conversation at most 200 messages
(then "start a new one"), and a tool result sent back to the model at most 20,000 characters.

A tool the model names that was not offered is never run, even if it exists. Its result is
"no such tool".

## 3. Tools

A tool declares its **name**, a **description** and a **JSON parameter schema** for the model,
the **Modbot permission** it needs, and whether it **only reads**. The registry lives in
`Modbot.AI/Chat`; the tools themselves live in `Modbot.Api/Features/Chat/Tools`, because they
call the same query code the pages use rather than querying tables a second way.

### 3.1 Every tool runs as the person asking

The permissions are the asking user's own, read from their session on every request. Two things
follow:

- **A tool the user lacks the permission for is not offered to the model at all.** It is not in
  the request, so the model cannot ask for it and cannot learn that it exists.
- Tools that serve mixed data narrow it the same way the page does. The audit-log search passes
  the user's permissions through `AuditVisibility`; the room tool hides who was there without
  `ViewAuditLog`, exactly like the room popup.

A moderator can never see through Chat what they could not see in the app.

### 3.2 First set — all read only

| Tool | Needs | Uses |
|---|---|---|
| `find_person` | ViewProfile | stored VRChat profiles, by id or name |
| `get_person` | ViewProfile | the profile popup's read |
| `get_person_history` | ViewAuditLog | `AuditQuery`, narrowed by `AuditVisibility`, for one subject |
| `get_person_cases` | ViewProfile | `CaseFileService.ListAsync` |
| `get_person_bans` | ViewAuditLog | the group ban list, and ban/unban facts for the person |
| `search_members` | ViewMembers | the Members page's search |
| `search_audit_log` | ViewAuditLog | `AuditQuery`, narrowed by `AuditVisibility` |
| `list_live_rooms` | ViewLiveRooms | the Live page's read |
| `find_world` | ViewAnalytics | stored worlds, by id or name |
| `get_world` | ViewAnalytics | the world popup's read |
| `get_instance` | ViewAnalytics | the room popup's read |
| `group_analytics` | ViewAnalytics | the My Group page's query, for a number of days |

Each result also names the people, worlds and rooms it mentions, so the page can show them as
chips that open the usual popups.

### 3.3 Acting tools — none yet, and off by default

M8 §2 and §6: Chat takes **no moderation action on VRChat** and **does not write ban reasons or
reports** on a moderator's behalf. This version has no tool that changes anything.

The registry is built for the day one is added anyway. A tool that is not read-only is
**off unless an operator switches it on** in Settings → AI → Chat, whatever the stored switches
say otherwise; a read-only tool is on unless switched off. Anything added later still has to
pass M8 §2 — no action on VRChat on a model's say-so — and should record a fact naming the person
who asked, like any other action.

## 4. Permission

`UseAiChat` (`1L << 21`), "Use AI chat". Chat also needs AI on (Base) and Chat on (this tab).

Not added to the built-in Moderator or Viewer roles. Using Chat sends group data to whatever
provider the operator chose, so it is granted on purpose, like `ViewLiveRooms`. Administrator
holds it.

`UseAiPastLimits` (`1L << 23`), "Use AI in excess of usage limits", is §10's way past a spend limit.
Not in the built-in Moderator or Viewer roles either.

## 5. Settings — Settings → AI → Chat

Needs `ManageSettings`. Chat on/off (off by default), model (blank means the Base model), extra
instructions added to the end of the system prompt, the three limits in §2, and one switch per
tool.

## 6. What is stored

`ai_chat_conversation` and `ai_chat_message`, per user:

- the user's messages and the model's replies, as text;
- each tool call's name and arguments, and the result sent back (capped as in §2), with the
  people, worlds and rooms it named and how long it took.

Every provider call is also a row in `ai_usage` (§10).

Only the owner can list, open, continue or delete a conversation; anybody else gets a 404, the
same answer as for one that does not exist. Deleting the Modbot account deletes its
conversations. Conversations are not facts: they are a person's own scratch pad, not moderation
history.

## 7. What is sent to the provider

To the endpoint on Base, with its key: the system prompt (Modbot's rules, the current time and the
extra instructions), the conversation so far, the definitions of the tools on offer and their
results. Those results are Modbot data — names, bios, ban history, who is in a room — so a
deployment that does not want that leaving the building uses a local model (M8 §4.2).

The user's Modbot id is not sent.

## 8. Server log

Every reply writes one line with the user id, conversation id, number of tool calls, outcome and
duration. Every tool call writes one line with the user id, tool name, whether it worked and
duration. Message text and tool arguments are never logged.

## 9. Not in this version

- Tools that act (§3.3).
- Stopping a reply part-way when it crosses a spend limit (§10.3).
- Sharing a conversation with another moderator.
- A retention window for conversations; they stay until the owner deletes them.

## 10. Usage and spend limits

Added 2026-09-15 at the maintainer's request: spend limits and cost estimates for every AI feature,
broken down by feature, with Chat's limits customisable by user, role or for the whole deployment,
and a permission to go past them. Everything below lives in `Modbot.AI/Usage` and is shared by every
feature that calls the AI: `moderation`, `insights`, `chat`, and `test` (the Test button on Settings →
AI → Base, which is a real call and is counted and limited like the others).

### 10.1 What is recorded

Every call to the provider writes one `ai_usage` row through `IAiUsage.RecordAsync`: when, the
feature, the account (null for a call nobody asked for, such as a scheduled insight or an AI topic
check), the model id that was asked for, the provider, and the input, cached input and output tokens
the provider reported. Chat asks for usage on every streamed request, so a round with tool calls is
recorded like a final answer. A call the provider never counted records nothing.

For OpenRouter only, each request also asks for usage accounting (`"usage": {"include": true}`) and
the cost OpenRouter reports at `usage.cost`, in US dollars, is stored in `ai_usage.reported_cost`. It
is read through the OpenAI SDK's store of fields it has no property for (`ChatTokenUsage.Patch`). The
field is not sent to other providers, because OpenAI refuses a request with a field it does not
know, and another server's `cost` is not believed.

### 10.2 Prices

A row with a reported cost costs exactly that. Every other row is priced **when it is read**, from
the price of its model:

1. **An entered price** (`ai_model_price`): typed in by the operator on the price list, per million
   input, cached input and output tokens. Always wins.
2. **A fetched price** (`ai_fetched_price`): from OpenRouter's public model list,
   `GET https://openrouter.ai/api/v1/models`, which needs no key and gives `pricing.prompt`,
   `pricing.completion` and usually `pricing.input_cache_read` in US dollars per token, as strings.
   Stored per million with when it was fetched. A price of `-1` (a router whose price depends on the
   model it picks) is left out, and so are the time-of-day `overrides` a few models have; their
   reported cost covers them. A model OpenRouter stops listing keeps its last fetched price.
3. **No price.** Its spend is **unknown, never zero**: every figure carries the unpriced tokens
   beside the money, the page writes "$1.20 + unknown" or "Unknown", and an estimate built on them
   says the same. Unknown spend cannot reach a money limit.

Providers count cached tokens inside the input count, so those are charged at the cached price, or
the input price when there is none. Because pricing happens on read, a price entered today also
prices what was used earlier in the month.

Fetched prices match a model id exactly, whichever provider the usage went through. The fetch runs
once a day while AI is on and set to OpenRouter (an hourly check; a deployment on a local model server
never calls openrouter.ai on its own), and whenever the operator presses Fetch prices. It has its own
`HttpClient`, never the VRChat one or its proxy, and is not retried: a failure waits for the next
hourly check, and a 429 waits a day.

### 10.3 Limits

A limit is a daily and/or monthly amount of money (UTC days and months, from `IModbotClock`), set for:

| Applies to | Compared with | Stops |
|---|---|---|
| Everyone | every feature's spend together | every feature |
| A feature | that feature's spend | that feature |
| A role | the Chat spend of each person holding the role, separately — not a pot the role shares | Chat |
| A user | that person's Chat spend | Chat |

**A call is stopped by the tightest limit that applies to it.** Person and role limits are Chat's
only: Chat is the one feature a person drives call after call, and the others that name a person do
so for one button press. The check runs before a call, never during one; a call that starts just
under a limit can finish a little over it, and the next is refused. The provider is not called.

At a limit:

- **Chat** refuses new turns with HTTP 429 and a sentence naming the limit — "Your daily AI spend
  limit is reached.", "The Moderator role's monthly AI spend limit is reached.", "The monthly AI spend
  limit for Chat is reached.", "This Modbot's daily AI spend limit is reached."
- **Moderation** stops its AI topic checks and reports the sentence as the reason AI was skipped;
  term lists keep running (AI moderation design §4).
- **Insights** skips scheduled runs, and Generate now answers 409 with the sentence (AI insights
  design §6).
- **The Test button** answers "did not work" with the sentence.

### 10.4 `UseAiPastLimits`

"Use AI in excess of usage limits" takes away the limits on the person and on their roles.
**The limit for everyone and the feature limits still apply**, Administrator included. The limit for
everyone is the operator's ceiling on what the deployment's AI key may cost, and a feature limit is
the operator's share of that ceiling for one feature; a permission that could spend past either would
turn them into suggestions.

### 10.5 Token limits from before prices

AI moderation and AI insights first shipped with a monthly token limit per feature
(`ai_feature_limit`), because Modbot had no prices yet. No screen ever set one. Limits are money now,
and each token limit is **changed into a monthly money limit for its feature once the feature's model
has a price**:

- The model is the one the feature is set to use now: Chat's or Insights' own model when set,
  otherwise the Base model.
- A token limit counts input and output together, and they have different prices, so it is priced at
  the mix of input, cached input and output tokens the feature has actually used. With no usage, it is
  priced as uncached input, which costs less than output on all but one model OpenRouter listed in
  September 2026, so the money limit almost always stops the feature no later than the token limit
  would have.
- When the feature already has a monthly money limit, the lower of the two is kept.
- A token limit whose model has no price is **kept as a token limit**: still counted, still stopping
  the feature ("The monthly AI token limit for Moderation is reached."), shown on the limits page as a
  token limit that can be removed, and changed on a later pass once a price arrives.

The change runs on every hourly price pass, after a fetch, and after the operator saves prices. It is
logged, not recorded as a fact.

### 10.6 Cost by feature and the estimate

Settings → AI → Limits shows, per feature and in total, spend today, this week (from Monday), this
month and last month, each with its token counts; a daily spend chart for the last 30 days, stacked by
feature; and the top ten accounts by Chat spend this month.

**The month-end estimate** is deliberately plain, so anybody can check it by hand:

> spend so far this month + (spend over the seven whole days before today ÷ 7) × days left after today

The seven days may reach back into last month, which is what makes it usable on the first. It is
shown per feature, in total, and next to each limit for everyone or a feature with how far through
the monthly limit it is.

### 10.7 Warnings and the fact

**Health** shows an "AI spend" card, only when there is something to show, listing each limit for
everyone or a feature where spend is at 80% or more of the limit, the month-end estimate is over a
monthly limit, or the limit is reached. Amber while close, red once reached. Person and role limits
are not listed: they are about one account, not the bill. The warnings come from
`/api/health/sync` (`aiSpend`), which needs `ViewOperationalLog`.

**One operational fact, `modbot.ai.limit.reached`,** is recorded the first time a limit stops a call
in its UTC day or month, and not again for that limit in that period. `ai_limit_reached` holds the
claim — the limit, whom it was for, and the period start — so two calls stopped at the same moment
write one fact. Its payload names the limit, the feature, the period, the amount, what had been spent
and the sentence shown. It is in the presence retention class, like other operational noise.

**Email alerts** come later with Modbot Cloud. They plug in at `IAiSpendAlerts`, which is told of the
same event straight after the fact is written; the one registered today does nothing.

### 10.8 Settings → AI → Limits

Needs `ManageSettings`, including the top Chat users. Cards: spend by feature with the estimate, daily
spend, spend limits (with what each is compared with today and this month, the estimate and how far
through each limit), top Chat users this month, and the price list. The price list shows every model
in use, set in settings or priced, with the entered price in the boxes, the fetched price as the
boxes' placeholder, and where the price in use comes from ("Entered", "OpenRouter · 3h ago" or "No
price"). Limits and entered prices are each saved as a whole list; clearing a model's boxes removes
its entered price.

### 10.9 The model picker

Added 2026-09-15 at the maintainer's request: **OpenRouter is the recommended provider**, and the
model boxes on the AI pages offer OpenRouter's whole list with prices, so a moderator picks a model
knowing what it will cost and what it can do rather than typing an id from memory.

OpenRouter is first on the provider list, carries a **Recommended** badge, and is what a new
deployment starts on (`AiProviders.Default`); a deployment that already saved another provider keeps
it.

**What is stored.** The daily price fetch (§10.2) now also stores the list itself, in
`ai_catalog_model`: the id, OpenRouter's name for it, the maker (the part of the id before the `/`,
with OpenRouter's leading `~` for an alias taken off), the context length, the most tokens one answer
may be, what it reads and writes, the request fields it takes (`tools`, `structured_outputs`, …),
when OpenRouter added it, the three prices per million, and every price field OpenRouter listed, per
unit as listed, as JSON. The table is replaced as a whole on each fetch, so it is what OpenRouter
lists today; `ai_fetched_price` is untouched by that and keeps a dropped model's last price, so past
spend stays priced. Unlike `ai_fetched_price` it also holds routers priced `-1` and models with no
price at all, marked as such, because the picker has to show them for what they are.

**`GET /api/settings/ai/catalog?feature=`** (`ManageSettings`) serves it, so opening the picker calls
nobody: the models in the order below, what the feature needs, when the list was fetched, this
deployment's average tokens per call, and the prices the operator entered. `feature` is `base`,
`moderation`, `insights` or `chat`. An entered price still wins over a fetched one, and a model with
an entered price is never "price varies". Refresh on the picker is the existing
`POST /api/settings/ai/prices/fetch`, which is never retried after a 429.

**Recommended for a feature.** No favourite model is ever named. A model is recommended when all
three hold:

1. it does what the feature needs — `tools` for Chat, `structured_outputs` for Moderation and
   Insights, and both for Base, because every feature without a model of its own runs on the Base
   model and Moderation always does;
2. it has a real price — not a router priced `-1`, and not a model OpenRouter prices not at all,
   since neither can be compared on cost;
3. OpenRouter added it less than eighteen months ago.

Recommended models come first, cheapest first inside each group, then the rest on the same footing,
with unpriced models last. "Cheapest" is what a thousand calls would cost this deployment where
there is usage to work that out from, and otherwise the input and output prices added together. The
rule is one function, `AiModelCatalog.Order`.

**What this deployment would pay.** When `ai_usage` has rows from the last thirty days, the picker
adds a column: the cost of a thousand calls of the feature the picker was opened from, at that
feature's own average input, cached input and output tokens, priced by the same `AiPrices.CostOf`
spend is priced with. Base averages every feature together apart from the Test button, which is one
short message and no work. With no usage the column is not shown.

**The picker.** A dialog with a search box and a dense table: the model's name with its id beneath
it, the maker, input, output and cached input per million tokens, the context length written as
"200K", and the cost per thousand calls. Any column can be sorted; the list starts in the
recommended-first order above. It filters by maker, Tools, Structured output, Free, and a highest
price per million. What a model can or cannot do is a chip, never a sentence: "Tools", "Structured
output", "Images in", "Free", and, where it matters for the feature the picker was opened from, "No
tools", "No structured output" and "Price varies". Picking a row fills the model box; the model
already saved is marked, and a model id typed by hand that OpenRouter does not list is kept and
shown as "Not in list". Every other provider keeps a plain list of the ids its own endpoint returns,
with the operator's entered price beside each, or "No price".
