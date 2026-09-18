# List paging design

**Date:** 2026-09-18
**Status:** built, except where §6 says otherwise

How a list page turns to the next page, and how a link to a page of a list keeps working.

---

## 1. The two problems

**A page number is measured from a list that is moving.** Every list on these screens is written to
while somebody is reading it: the member sweep finishes, a ban lands, the Discord bot reads the
whole member list again on every sign-in. "Skip a hundred rows and give me fifty" is measured from
wherever the list starts *now*. A row added above the boundary pushes one row down onto the next
page, where the reader sees it twice; a row removed pulls one up off the next page, where nobody
ever sees it. Neither is visible to the reader — the second page looks like a second page.

**A page could not be linked to.** Nothing about a list's position reached the address. A moderator
who found something on the third page of the ban list could not send anybody the third page of the
ban list, could not reload onto it, and could not press Back to undo a page turn — Back left the
screen entirely, because the filters replace their history entry rather than pushing one.

The audit log had already solved the first problem for itself (`AuditQuery`, spec 5.9). This
generalises what it did, and adds the second.

---

## 2. What a cursor is

A cursor names a row and a direction: *the rows after this one*, or *the rows before it*. Given a
list ordered by something, "after this row" is a comparison, and a comparison does not care how
many rows are above it or how many arrived since.

It is a piece of text the server writes and the caller hands back. It carries four things:

| Part | |
|---|---|
| Direction | `next` or `back`. |
| Sort | Which of the list's orderings it was written under. |
| Value | The ordering value of the row the page starts from, as text. |
| Id | That row's id, which is unique in the list. |

They are joined with `!`, each part percent-encoded, so no part can contain the separator. A row
with **no** value to order on — no join date, no fetched profile — writes `-` in the value's place;
a row whose value is present writes `=` and then the value. The two are kept apart because "this
person has no name yet" and "this person's name is the empty string" are different places in the
order.

```
next!joined!=2026-03-10T12%3A00%3A00.0000000%2B00%3A00!usr_abc
next!joined!-!usr_abc
back!name!=Alice%20Wonder!usr_abc
```

It is readable on purpose: a moderator, or whoever reads a script later, can see in the address bar
where a link points. Nothing promises the shape will not change, and the API documentation says so
— **send back what you were given; do not build one.**

The code is `src/Modbot.Api/Lists/ListCursor.cs`.

### 2.1 Why both halves are required

A cursor on the ordering value alone silently drops every row that shares the boundary value. These
lists tie constantly: a sweep stamps a whole batch of rows with one moment, and an imported group
has every member joining at the same instant. `AuditQuery` states the same rule for the same reason
— VRChat's audit entries share timestamps freely.

The corollary bit `/api/logs`, which ordered by `at` and paged on `id`. Those are two different
orders, so a line written late but stamped early sat above the cursor's row in the sort and below it
in the filter, and was shown on neither page. It is now ordered by the id it pages on. For a log
that is also the more honest order: the id is the order the lines were written, and the timestamp is
whatever the writer put on them.

### 2.2 Why the sort is carried

The member list can be ordered three ways, the Discord list three. A cursor holding a display name,
read back while the list is ordered by join date, would compare a name against a date and return an
arbitrary slice. Carrying the ordering's name turns that into a first page instead of a wrong
answer — which matters because it is reachable by an ordinary action: change the sort with a page
still in the address.

### 2.3 What happens to a cursor that will not read

**The first page, never an error.** A cursor that is malformed, truncated, written under another
ordering, or left over from a build that ordered the list differently is treated as no cursor at
all. A bookmark from six months ago should show the list.

This follows the audit log's existing rule for an unrecognised filter value (`ParseEnums`): a stale
link shows the timeline rather than an error page. Nothing is widened by it, because the permission
check runs regardless of what the cursor said.

---

## 3. Which way the pages go

A cursor list can offer Next and Previous and nothing else. It cannot offer "page 40", because it
does not know how many rows are above any row without counting them.

Previous is a real cursor, not the browser's Back button: it reverses the ordering, reads a page,
and turns the rows back round. That is what makes a pasted link to page three usable — the reader
arriving on it has no history to go back through.

`ListPaging.ReadAsync` does the part that is the same everywhere: read one row more than was asked
for, so "is there another page" costs nothing extra; drop the extra; turn the rows round when
reading backwards; and decide which of the two cursors to hand back.

The other half — the ordering, and the comparison for "after this row" — stays in each list. A
shared expression builder covering nullable values, mixed directions and three different tie-break
types would be more code than the comparisons it replaced, and none of it readable. Each list's
comparison is four to eight lines of ordinary LINQ beside the ordering it mirrors.

---

## 4. The address

The position travels in one query parameter, `cursor`, beside the filter chips that produce it
(`lib/filters.ts` writes those as repeated `f`). One parameter is enough because the direction is
inside the cursor: the browser never reads one, builds one, or decides which way it points. It
carries the server's text to the address and back.

```
/members?f=status:is:current&f=role:is:grol_mod&cursor=next!joined!%3D2026-03-10T12%3A00%3A00Z!usr_abc
```

**Turning a page pushes a history entry. Changing a filter replaces one.** So Back walks back
through the pages that were turned, and a filter change does not bury the screen under history.
Changing a filter, the search box, or the sort also drops the cursor: the list being paged is a
different list now, and page three of it means nothing.

One piece of client code does all of this — `src/Modbot.Web/src/lib/listPosition.ts`, whose pure
half is tested with Node alone, as `filters.ts` is. Every list page uses `useListPosition()` rather
than a `page` of its own, so turning a page, sharing the link and pressing Back mean the same thing
on every list.

### 4.1 What the controls say now

The three list footers each said `Page N of M` between a Previous and a Next button, each
recomputing `M` from a total. There is no `M` any more, so they are one shared `Pager` with two
buttons and no label. The count that people actually read — *"4,812 people"* — was never in the
footer; it is beside the filters, and it stays (§5).

---

## 5. What happens to the totals

A cursor page does not need a count and the audit log has always refused to compute one: counting a
filtered slice of the partitioned fact log on every page turn is a scan of every partition.

The member list, the group ban list, the Discord member list and the repeat-offender list are a
different size — thousands of rows behind an index — and each shows its total on screen. **Those
totals stay.** `total` is still counted on every read and is still the whole filtered list, not the
page. Dropping it would take away the only figure on the screen that says how big the answer is, to
save a count that was already being paid.

`page` and `pageSize` are still in each answer. `pageSize` means what it always did. `page` is the
page number a caller asked for with `page`, and is `1` for anybody paging by cursor, because there
is no page number to report. The API reference says so on the field.

---

## 6. Which lists moved

### Moved to cursor paging

| List | Ordered by | Tie-break | Sorts |
|---|---|---|---|
| `GET /api/members` | join date, newest first | user id | `joined`, `name`, `seen` |
| `GET /api/bans` (the group's ban list) | ban date, newest first | user id | one |
| `GET /api/discord/members` | join date, newest first | user id | `joined`, `oldest`, `name` |
| `GET /api/repeat-offenders` | last action, newest first | subject id | one |

In every one of them, rows with nothing to order on — no join date, no fetched profile — sort to
the end of the list, as they did before, and the cursor's missing-value mark is what lets a page
boundary land among them.

### Already cursor-paged, left alone

- `GET /api/audit` — the pattern everything else follows. Its cursor is two named parameters
  (`beforeOccurredAt` and `beforeId`) rather than one piece of text. That is a published contract
  that works, and rewriting it would break every caller to gain nothing but a spelling.

### Already cursor-paged, fixed

- `GET /api/logs` — ordered by `at` while paging on `id`, so lines fell between the pages (§2.1).
  Now ordered by the id it pages on. No change to any parameter or answer.

### Not moved, and why

- **`GET /api/audit/bans`** — the fact-derived ban list. It is not a query over rows: it reads
  every ban and every unban fact, groups them per subject in memory, works out each subject's
  current state, sorts the result and skips into it. There is no row in a table to name, so there
  is nothing for a cursor to point at. Keyset-paging it would mean first building the current-state
  tables spec 5.2 describes, which is a different piece of work. It keeps `offset` and `limit`.
  Worth knowing: its in-memory sort has no tie-break at all, so two subjects sharing a timestamp
  are in an arbitrary order and its offset paging can already repeat or skip one of them. That is a
  real fault, and the fix for it is the same fix — real rows.

- **`GET /api/cases`** — orders by `createdAt` with the case file's `uuid` as the tie-break. This
  codebase has no keyset comparison on a `uuid` and no precedent for translating one; `string.Compare`
  on a text column is what every comparison here uses. Case files are written one at a time by a
  moderator rather than swept in batches, and no screen pages the list (the pane asks for twenty
  and stops), so an offset does not drift under anybody today. It is the next one to move, and
  moving it means deciding what it orders on.

- **`GET /api/insights`** — takes `before` as a `createdAt` alone while ordering by `createdAt` and
  then `id`, which is the same half-cursor fault as the log had. Fixing it properly means changing
  the shape of `before`, and it is a published parameter feeding a panel that asks for ten rows. It
  is recorded here rather than fixed quietly.

- **`GET /api/discord/members/{id}/messages`** — pages by number on purpose: its `at` parameter
  takes a message id and answers with *the page containing it*, which is a page number by
  definition. Message history is also append-only at one end and read from an anchor, not scrolled
  from the top.

- **`GET /api/giveaways/{id}/draws/{drawId}/entrants`** — a draw is frozen when it is made and its
  entrants are ordered by a rank that is unique within it. An offset into a list that cannot change
  cannot skip or repeat anything.

- **Lists with no paging at all** — Flags, Reviews, Giveaways, Calendar, Live, Users, Roles and the
  analytics tables each read one capped batch and draw it. There was no paging to move. Several of
  them also have no tie-break in their ordering, which does not bite while nothing pages them and
  will the moment something does.

---

## 7. The old parameters

`page`, `pageSize`, `offset` and `limit` still work everywhere they worked before, and every one of
these endpoints is in `docs/openapi/modbot.json`, which is a published contract: API keys and
scripts already call them.

A caller that sends `page` still skips rows and still gets `total`, `page` and `pageSize` back — and
now also gets a `next` to move to. A caller that sends `cursor` wins: the cursor is used, `page`
comes back as `1`, and the skip is not applied. Sending both is not an error; it just means the
page number is ignored.

They stay for at least one release. The API documentation says plainly what a page number can do
wrong on a list that is being written to, so a script author has a reason to move rather than an
instruction.

---

## 8. Tests

- `tests/Modbot.Api.Tests/Lists/ListCursorTests.cs` — the text: a cursor reads back as itself, an
  id with anything in it survives, a missing value is not an empty one, nonsense and a cursor from
  another ordering both read as nothing.
- `tests/Modbot.Api.Tests/Features/Members/MemberPagingTests.cs` — the pages join up; a member
  joining between two reads is neither repeated nor skipped; a member leaving between two reads
  does not pull a row out of sight; everybody joining at the same instant still pages through;
  people with no join date page at the end; an unreadable cursor shows the list; `page` still works
  and hands back a cursor; a cursor beats a page number. The group ban list gets the insertion case
  and the unreadable case of its own.
- `tests/Modbot.Api.Tests/Features/DiscordMembers/DiscordMemberPagingTests.cs` — all three
  orderings forwards and back, the tie when everybody joined at once, and a cursor read under the
  wrong sort.
- `src/Modbot.Web/tests/listPosition.test.ts` — the address: a cursor round-trips, an empty one
  reads as the first page, clearing it leaves the filters alone, turning a page keeps the rest of
  the address.
