# List paging design

**Date:** 2026-09-18
**Status:** built

How a list page turns to another page, and how a link to a page of a list keeps working.

---

## 1. The decision

**Numbered pages, with the page number in the address.** Every list in the app that pages does it
the same way: the server takes `page` and `pageSize`, answers with `total`, `page` and `pageSize`
beside the rows, and the browser works out how many pages there are and draws the numbers.

This was decided twice in one day, and the second answer is the one that stands. §7 says what the
first one was and why it was taken out; it is written down so that nobody reads the arguments for
keyset paging and reintroduces it.

---

## 2. The problem it solves

**A page could not be linked to.** Nothing about a list's position reached the address. A moderator
who found something on the third page of the ban list could not send anybody the third page of the
ban list, could not reload onto it, and could not press Back to undo a page turn — Back left the
screen entirely, because the filters replace their history entry rather than pushing one.

**There was no way to reach a page except through the pages before it.** The footers said
*"Page 3 of 97"* between a Previous and a Next button. The one figure on the screen a reader might
want to act on — 97 — was the one thing they could not act on.

---

## 3. What the page number rests on

A page number is "skip this many rows, then give me this many". That is a sound answer only while
the ordering is **total**: every row has exactly one place, and two reads of the same list put the
rows in the same places. An ordering with ties leaves the tied rows in whatever order the database
happened to return them, and then two rows can swap between one page and the next — so one is shown
twice and the other not at all, on a list nobody has touched.

Every ordering these lists offer ends in an id that is unique within the list:

| List | Ordered by | Tie-break | Sorts |
|---|---|---|---|
| `GET /api/members` | join date, newest first | user id | `joined`, `name`, `seen` |
| `GET /api/bans` (the group's ban list) | ban date, newest first | user id | one |
| `GET /api/discord/members` | join date, newest first | user id | `joined`, `oldest`, `name` |
| `GET /api/people` | last seen, newest first | user id | `seen`, `name`, `known` |
| `GET /api/repeat-offenders` | last action, newest first | subject id | one |

Rows with nothing to order on — no join date, no fetched profile — sort to the end of the list,
which is also where they page.

`tests/Modbot.Api.Tests/Features/Members/MemberPagingTests.cs` and its Discord counterpart walk each
list twice, once whole and once two rows at a time, and assert the two reads are the same sequence.
They seed ties on purpose, because a sweep stamps a batch of rows with one moment and the Discord
bot re-reads the whole server on every sign-in.

### 3.1 What a page number still gets wrong, and why that is accepted

A list that is written to while somebody reads it can still repeat a row or hide one: a member joins
above the boundary between reading page one and page two, everything shifts down, and the last row
of page one is the first row of page two.

That is real, and it is a trade rather than a fault to be fixed at any price. Against it:

- a numbered page can be linked to, refreshed onto and jumped into;
- these lists already count their `total` on every read, so the count a page number needs is
  already paid for;
- a moderator who wants the oldest member is far better served by pressing the last page than by
  pressing Next ninety-four times.

The shift is also one row, briefly, on a list whose rows a moderator is scanning rather than
processing exactly once. The place where "exactly once" matters is the fact log, and the audit log
pages by its own cursor for exactly that reason (§6).

---

## 4. The address

The page travels in one query parameter, `page`, beside the filter chips (`lib/filters.ts` writes
those as repeated `f`).

```
/members?f=status:is:current&f=role:is:grol_mod&page=3
```

Page one is written as **nothing at all**, so a plain list has a plain address and there is one
spelling for the first page rather than two.

Only plain digits are read as a page number. `Number` would read `1e3` as a thousand and `" 4 "` as
four, and this never writes either, so a hand-edited address means what it looks like or means page
one. Anything else — `page=0`, `page=-3`, `page=three`, a link from an older build — is page one
rather than an error, following the audit log's existing rule for an unrecognised filter value
(`ParseEnums`): a saved link should show the list.

**Turning a page pushes a history entry. Changing a filter replaces one.** So Back walks back
through the pages that were turned, and a filter change does not bury the screen under history.
Changing a filter, the search box or the sort also goes back to page one: the list being paged is a
different list now, and page three of it means nothing.

One piece of client code does all of this — `src/Modbot.Web/src/lib/listPage.ts`, whose pure half is
tested with Node alone, as `filters.ts` is. Every list page uses `useListPage()` rather than a
`page` of its own, so turning a page, sharing the link and pressing Back mean the same thing on
every list.

The search box needs one piece of care: its debounced effect runs once on mount, and going back to
page one there would throw away the page a pasted link asked for. It resets the page only when the
typed words actually differ from the words being searched.

---

## 5. The control

One `Pager` (`src/Modbot.Web/src/components/Pager.tsx`), under every list that pages. Previous, the
numbers, Next. It draws nothing at all when there is only one page.

**A long list shows both ends and where the reader is**: the first page, the last page, the page
being read and two neighbours either side, with a gap standing for the numbers left out.

```
1 … 7 8 9 10 11 … 200
```

A hundred numbers in a row is a wall, not a control. The first and last are always there because
"start again" and "the far end" are the two jumps people actually make; the neighbours are there
because the next page is the next thing anybody wants. A gap that would hide a **single** number is
replaced by that number — `1 2 3 4 5 6 7 … 200` rather than `1 … 3 4 5 6 7 … 200` — because the gap
is no narrower than the number it hides and one press further away.

That is at most nine things wide, which fits a phone without the row wrapping more than once.

**A page past the end of the list.** The server answers a page past the end with no rows, the whole
list's `total`, and the page number that was asked for; it does not clamp, because then the address
and the answer would disagree. The pager does the clamping instead: when the page it is on is past
the last page — a filter narrowed the list under somebody on page nine — it goes back to page one
rather than leaving them looking at nothing with no number left to press.

### 5.1 What the footer does not say

The count people actually read — *"4,812 people"* — is beside the filters at the top of the list,
not in the footer, and it stays there. The footer is a control; it carries no sentence.

---

## 6. Which lists page which way

### Numbered pages

`GET /api/members`, `GET /api/bans`, `GET /api/discord/members` and `GET /api/people` take `page`
and `pageSize` and answer with `total`, `page` and `pageSize`. All four are drawn by the shared
pager.

`GET /api/repeat-offenders` takes `offset` and `limit` and answers with `total` and `offset`. It is
the same shape of answer by another spelling, and it is left alone because nothing pages it: the
screen asks for two hundred rows and draws them. It gets numbered pages when something needs them,
not before.

### Paged by cursor, on purpose

- **`GET /api/audit`** — the fact log. Its cursor is two named parameters (`beforeOccurredAt` and
  `beforeId`). It refuses to count a filtered slice, because that is a scan of every partition, so
  there is no `total` to turn into a number of pages even if somebody wanted one. It is also the
  list where reading a row exactly once genuinely matters.
- **`GET /api/logs`** — Modbot's own log, paged by row id (`before`). The table is written to
  constantly and nothing links to a page of it.
- **`GET /api/events/poll`** — a long poll, not a list.

### Offset and limit, unmoved

- **`GET /api/audit/bans`** — the fact-derived ban list. It is not a query over rows: it reads
  every ban and every unban fact, groups them per subject in memory, works out each subject's
  current state, sorts the result and skips into it. Worth knowing: that in-memory sort has **no
  tie-break at all**, so two subjects sharing a timestamp are in an arbitrary order and its paging
  can already repeat or skip one of them. That is a real fault, and the fix is the current-state
  tables spec 5.2 describes, not a change to how it pages.
- **`GET /api/cases`** — orders by `createdAt` with the case file's `uuid` as the tie-break, which
  is total, so its offset paging is sound. No screen pages it; the pane asks for twenty and stops.
- **`GET /api/insights`** — takes `before` as a `createdAt` alone while ordering by `createdAt` and
  then `id`, so it can drop an insight that shares a timestamp with the boundary row. It is a
  published parameter feeding a panel that asks for ten rows. Recorded here rather than fixed
  quietly.
- **`GET /api/discord/members/{id}/messages`** — pages by number on purpose: its `at` parameter
  takes a message id and answers with *the page containing it*, which is a page number by
  definition.
- **`GET /api/giveaways/{id}/draws/{drawId}/entrants`** — a draw is frozen when it is made and its
  entrants are ordered by a rank unique within it, so an offset cannot skip or repeat anything. Its
  Previous and Next live inside a dialog that several draws can open at once, so they stay local to
  that panel rather than moving into the address.

### Not paged at all

Flags, Reviews, Giveaways, Calendar, Live, Users, Roles and the analytics tables each read one
capped batch and draw it. Several of them have no tie-break in their ordering, which does not bite
while nothing pages them and will the moment something does.

---

## 7. Cursors were tried, and taken out

Earlier the same day, "pagination with cursors and pagination in query string" was read as a request
for keyset paging, and the member list, the group's ban list, the Discord member list and the
repeat-offender list were converted to it: a `ListCursor` type, a shared `ListPaging.ReadAsync`, a
`cursor` parameter, `next` and `previous` beside the rows, and a Pager with two buttons and no
numbers.

It was taken out the same day, before it had ever been in a release. The reasons, so that the next
reader has them:

1. **It was not what was asked for.** The maintainer's words: *"I think we should just have pages
   for everything and not cursors."*
2. **It removed the thing people wanted.** A cursor list cannot offer page 40, because it does not
   know how many rows are above any row without counting them. Next and Previous were all that was
   left, which is fewer ways to move than the lists had before.
3. **Two ways to page is worse than one.** `page` had been kept working alongside `cursor` for
   compatibility, so every one of these endpoints had two positions, a rule for which won, and a
   `page` field that answered `1` and meant nothing. That is machinery to keep working and explain
   forever, for a contract no caller had yet.
4. **What it bought was small here.** Keyset paging earns its keep where a row must be read exactly
   once, or where counting is too expensive to do. These lists are thousands of rows behind an
   index, they count their total on every read anyway, and a moderator is scanning them rather than
   processing each row once.

The correct observation inside the conversion is kept, because it was never about cursors:
**`/api/logs` was ordered by `at` while paging on `id`**, so a line written late but stamped early
sat above the boundary row in the sort and below it in the filter, and appeared on neither page. It
is now ordered by the id it pages on, which for a log is also the more honest order — the id is the
order the lines were written, and the timestamp is whatever the writer put on them. That reasoning
lives in `LogEndpoints`' own comment.

---

## 8. Tests

- `tests/Modbot.Api.Tests/Features/Members/MemberPagingTests.cs` — the member list walked whole and
  in twos in each of its three orderings, and the two reads compared; everybody joining at the same
  instant still paging through without a repeat; people with no join date paging at the end; a page
  past the end answering empty with the whole list's total; `page=0` and a negative answering the
  first page; a page size beyond the cap cut down to it. The group's ban list gets the same walk
  with ties on both boundaries.
- `tests/Modbot.Api.Tests/Features/DiscordMembers/DiscordMemberPagingTests.cs` — the same walk in
  all three orderings, newest-first and oldest-first being each other reversed, everybody joining
  at once, and a page past the end.
- `tests/Modbot.Api.Tests/Features/Members/MembersTests.cs` — already held the first of these:
  pages counted from one, with the total being the whole filtered list.
- `src/Modbot.Web/tests/listPage.test.ts` — the address: a page number round-trips, page one is
  written as nothing, anything that is not plain digits reads as page one, turning a page keeps the
  filters and the rest of the address. And the numbers themselves: a short list showing all of
  them, a long list showing both ends and the reader's neighbours, a gap of one being that number,
  and a page past the end drawing the end of the list.
