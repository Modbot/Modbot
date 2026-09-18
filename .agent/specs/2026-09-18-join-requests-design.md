# Modbot — Join Requests

- **Date:** 2026-09-18
- **Status:** Implemented with this document
- **Covers:** what VRChat actually offers for a group's join queue (§2); how the Requests screen
  builds its list and what that list cannot know (§3); the two new rate-limit classes and why they
  are two (§4); what happens to a request that is already gone (§5); the two permissions (§6); the
  facts each answer writes (§7); what was deliberately left out (§8)
- **Implements:** the `Requests` page, `GET /api/requests`, `POST /api/requests/approve`,
  `POST /api/requests/reject`, the `ViewJoinRequests` and `AnswerJoinRequests` permissions, and the
  `groups.requests` and `groups.requests.answer` endpoint classes
- **Related:** foundation §4.1 (the gate), §4.2 (pacing), §4.3.1 (cold stop), §4.3.4 (the standing
  instruction on unmeasured endpoints), §4.3.5 (the `interactive` backstop), §5.9.1 (who decided);
  M4 §4 (kick, ban and unban, and the single-use key); VRChat proxy and moderator buckets design

---

## 1. What this adds

Modbot already recorded the *history* of the join queue. The audit-log sync maps
`vrchat.group.request.create`, `.reject` and `.block`, they are counted in daily totals and they
show up in insights, so a moderator could see what had happened to the queue and could do nothing
about it. Working the queue meant leaving Modbot for VRChat — which is exactly where Modbot's
record of who this person is, and whether the group has thrown them out before, is not.

This adds a **Requests** screen: the people waiting to be let in, each shown the way the rest of the
app shows a person, with Approve and Reject beside them.

## 2. What VRChat actually offers

Both halves exist, and they were checked against the SDK (`VRChat.API` 2.21.0,
`libs/vrchatapi-csharp-latest/src/VRChat.API/Api/GroupsApi.cs`) rather than assumed:

| What | VRChat's endpoint | Answers with |
|---|---|---|
| The outstanding requests | `GET /groups/{groupId}/requests` (`GetGroupRequests`) | `List<GroupMember>` |
| Answering one | `PUT /groups/{groupId}/requests/{userId}` (`RespondGroupJoinRequest`) | nothing (empty body) |

Three things about them shaped the design.

**There is a real list.** This is the difference that decides everything else. Had there been no
list endpoint, the screen would have had to be reconstructed from the `vrchat.group.request.create`
facts the audit-log sync writes, minus the `.reject` and `.block` ones — and that list could only
ever hold the requests Modbot happened to be running for. A group that has had Modbot for a week
and a queue three months deep would have been shown a week of it, with nothing on the screen saying
so. Because the endpoint exists, the screen does not have to make that caveat: **what it shows is
the queue.**

**The list pages by offset and carries no total.** `n` and `offset`, and a plain JSON array back —
no `totalCount`, no `hasNext`, unlike the audit log's `PaginatedGroupAuditLogEntryList`. So the API
answers with `hasMore`, which is simply "the page came back full", and the screen has Previous and
Next rather than a page count. A total would have to be invented, and an invented total on a
moderation screen is worse than none.

**The answer body carries a `block` flag.** `RespondGroupJoinRequest` takes `action`
(`accept`/`reject`) and `block`, where blocking stops the person asking again. **Modbot always
sends `block: false`.** Blocking somebody is a heavier decision than declining one request, it is
not undoable from this screen, and a moderator who wants that outcome has Ban — which writes a case
file and shows up on the Bans page, where a decision like that belongs. The `blocked` query
parameter on the list endpoint is left at its default for the same reason: the blocked requests are
a different list answering a different question, and Modbot has no screen that asks it.

## 3. How the list is built, and what it cannot know

`GET /api/requests` reads VRChat **when the moderator asks**, and stores nothing.

- One VRChat request per page opened, plus one per press of Refresh. Nothing polls it.
- Each row is then filled in from Modbot's own tables — three reads for the whole page, never one
  per row: the stored profile (name, picture, trust rank, the sticky 18+ mark), the group's ban list
  and the group's member list.
- Answering a row takes it out of the list in the browser rather than re-reading the page, so
  clearing a backlog costs one VRChat request per answer instead of two.

**Why not sync it.** A stored copy of this list would be wrong in precisely the way that matters:
its rows would be buttons that do nothing, because somebody answered the request in VRChat while
the page was open. Every other list in Modbot is a record of what happened, and staleness there
costs accuracy; this one is a work queue, and staleness costs a moderator pressing a button on
somebody who is already in.

**What it cannot know.** Three things, and none of them is hidden behind a friendly sentence:

1. **Whether VRChat answered at all.** A refusal — a cold stop, a 429, no session — returns
   429 or 502 and the screen says it could not read the list. It never returns an empty list on a
   failure: an empty queue and a queue Modbot could not read look identical on a screen, and only
   one of them means there is nothing to do.
2. **Who the person is, when Modbot has never seen them.** The overwhelmingly common case for a
   join request is somebody with no record at all. The row carries `known: false`, and the name
   and picture are then whatever VRChat sent with the request. Modbot does **not** fetch the
   profile: that would be one `users.*` request per row of a list read on every page open, for a
   person who may be rejected a second later.
3. **How many are waiting.** VRChat does not say, so neither does Modbot.

## 4. Rate limits — two new classes, both guesses

Foundation §4.3.4 forbids inferring a limit from a neighbour and requires the question to be asked.
**Neither of these endpoints has been measured, and both numbers below need confirming by the
maintainer.** They are deliberate underestimates, chosen so that being wrong costs stale data
rather than an opaque multi-minute block.

| Class | Lane | Rate | Backstop | Scoped | Why this number |
|---|---|---|---|---|---|
| `groups.requests` | `groups.requests` | **0.2 req/s** (one per 5 s) | `interactive` | group | Group-shaped data with no measurement, so `groups.read`'s rate — the same reading §4.3.4.1 applied to `users.groups` |
| `groups.requests.answer` | `groups.requests.answer` | **0.5 req/s** (one per 2 s) | `interactive` | group | A group write with no measurement; the same deliberately low starting point the maintainer set for `groups.moderate`, not a number read off it |

Four decisions inside that table are worth stating.

**They are two classes, not one.** Reading the list and answering it share nothing but a URL
prefix. On one lane, the answer a moderator just pressed would queue behind a refresh of the list
they pressed it on — which at 0.2 req/s is a five-second stall for nothing.

**Answering is not on `groups.moderate`.** It is tempting: they are all group writes a moderator
presses. But working a join queue is *many small writes in a row*, and a ban is one. Sharing a
bucket would mean a moderator clearing a backlog of forty requests could cold stop the ban button
— and Ban is the one action that must still work when everything else has stopped. The isolation
principle §4.3.4.1 set out for `users.groups` applies with more force here, because the blast
radius would include the most consequential control in the product.

**Both pass through `interactive`, not `global`.** Nothing polls either endpoint; a person opened a
page. That is what §4.2's reserved room is for (§4.3.5), and drawing from `global` would put both
behind the bucket the sweeps keep empty.

**Neither is in `Scheduled`.** The settings screen sums the scheduled classes against the 2 req/s
ceiling so an operator can see the room left. These are what the room left is *for*, not part of
what consumes it.

A 429 on either is a cold stop of that class alone and is **never retried** (§4.3.1). On the list,
the screen says it could not read the queue. On an answer, the answer did not happen and the
moderator is told so, in the same words a rate-limited ban uses.

## 5. A request that is already gone

The row a moderator presses may be out of date by the time the press arrives: somebody accepted it
in VRChat, another moderator answered it in Modbot, or the person withdrew it. VRChat answers
**404**.

That is the ordinary ending for a queue two people are working at once, and it is handled as one
rather than as a fault:

- `GroupJoinRequests` returns the failure like any other — it does not throw, and nothing retries.
- `ModerationActionService` turns a 404 on `approve` or `reject` into a plain sentence — *"That
  request is no longer waiting. Somebody may have answered it in VRChat."* — because "Not Found"
  in front of a moderator reads as a bug in Modbot.
- The answer carries `gone: true`. The endpoint still answers **200**: the request was
  well-formed, Modbot did what was asked, and the world had moved.
- The row leaves the list, because the person is no longer waiting — the same outcome as a
  successful answer, reached differently.
- **Nothing is recorded as done.** The fact written is `modbot.action.failed`, exactly as for any
  other refusal (M4 §4.1). No query for "who was let in" can ever count one of these.

`gone` is derived from the stored status code rather than a column of its own, so a second press of
the same key is told the same thing the first one was.

## 6. Permissions

Two flags, **bits 32 and 33**. (Bit 31 was spoken for by work in flight when these were added, and
a bit claimed twice is the one mistake the bitfield cannot recover from.)

- **`ViewJoinRequests`** — see the queue. Separate from `ViewMembers`: the member list is who is
  already in, and reading it costs nothing; this is a queue of strangers, read live, and every read
  spends VRChat request budget.
- **`AnswerJoinRequests`** — approve or reject. Separate from `ViewJoinRequests` the way
  `ManageCalendar` is from `ViewCalendar`, and deliberately not folded into `Kick` or `Ban`:
  deciding who gets in is a different job from removing somebody who is already in, and plenty of
  groups hand the first out more freely than the second.

Neither is in the built-in Moderator or Viewer roles. Administrator holds both, as it holds
everything.

## 7. What each answer records

Answering goes through `ModerationActionService`, the same path a kick or a ban takes, so it
inherits the guarantees M4 §4 established rather than reimplementing them: the key claimed on a
unique index before anything is sent, so one confirmation acts exactly once; nothing recorded as
done unless VRChat accepted; a refusal recorded as a refusal; Modbot's own account off limits.

| Outcome | Fact |
|---|---|
| Approved, VRChat accepted | `modbot.action.request.approve` — *"Let into the group from Modbot"* |
| Rejected, VRChat accepted | `modbot.action.request.reject` — *"Join request turned down from Modbot"* |
| Anything else | `modbot.action.failed` |

The subject is the person, on the VRChat platform; the actor is the Modbot account of the moderator
who pressed the button. These are deliberately separate from the `vrchat.group.request.*` facts the
audit-log sync writes about the same event, for the reason §5.9.1 gives: VRChat attributes
everything Modbot does to Modbot's own account, so its log can say the request was answered and
cannot say who decided to.

A reject may carry reasons from the group's list and a note, and needs one only where the group has
switched on requiring a classification. A reject is not a ban — most of them have nothing to say,
and demanding a reason for each would make a queue of thirty unworkable and the reasons meaningless.

**Nothing is written to `group_member` or `group_ban`.** Neither table is what this screen reads,
so there is no stale page to patch up, and the member sweep lists an approved person on its next
pass exactly as it lists anybody else who joined.

## 8. Left out on purpose

- **A background poll, and a notification when somebody asks.** The queue is read when a moderator
  looks at it. Polling it would spend budget on the assumption that somebody is watching, and the
  notification pipeline is somebody else's design.
- **Approving or rejecting several at once.** `BulkAction` exists as a flag and this screen does
  not use it. At one answer per two seconds a bulk control would mostly be a progress bar, and the
  first thing anybody would want from it — "reject everything below Known trust" — is a rule, not
  a button.
- **Blocking.** See §2.
- **Fetching the profile of everybody in the queue.** See §3.
- **A count beside the sidebar label.** VRChat sends no total, and a number Modbot made up beside
  a page label is indistinguishable from one it read.
