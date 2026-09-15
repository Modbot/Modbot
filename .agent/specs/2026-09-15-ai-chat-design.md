# Modbot — AI Chat

- **Date:** 2026-09-15
- **Status:** First version built
- **Covers:** the Chat page, the tool-using loop behind it, its settings (Settings → AI → Chat) and
  the `UseAiChat` permission
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

## 5. Settings — Settings → AI → Chat

Needs `ManageSettings`. Chat on/off (off by default), model (blank means the Base model), extra
instructions added to the end of the system prompt, the three limits in §2, and one switch per
tool.

## 6. What is stored

`ai_chat_conversation` and `ai_chat_message`, per user:

- the user's messages and the model's replies, as text;
- each tool call's name and arguments, and the result sent back (capped as in §2), with the
  people, worlds and rooms it named and how long it took.

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
- Sharing a conversation with another moderator.
- A retention window for conversations; they stay until the owner deletes them.
