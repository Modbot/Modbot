# 2026-11-22: the member sweep lists too few people

## What live showed

`/api/health/sync`, 2026-11-22 20:14Z, `memberSweep.lastRun` (failed): "listed 3634 where VRChat counts
4931; the list ended at offset 4930 after about 52 pages". The last finished sweep (2026-11-22 03:30Z)
stored 3,585.

The sweep walked the whole offset range, but saw about 1,300 fewer distinct people than the group
has. So VRChat's default order shifts between page requests: some members come back on two pages,
others on none. The sweep's guard (seen less than count minus the larger of 25 and 3%) correctly
refuses to mark anyone as left, so the sweep fails every time and the count is never saved.

The ban sweep is healthy (1,226 bans), so only the members list is affected.

## Earlier result

Section 7.1 of `vrchat-audit-log-findings.md` (2026-09-13) read 4,739 of a group of 4,740 with no
sort given. The default order has changed since.

## The fix

Every member page now asks for `sort=joinedAt:asc` (`GroupSearchSort.JoinedAtasc` in the VRChat SDK,
the only other value is `joinedAt:desc`). Oldest joiner first means new joiners land at the end of the
list and cannot push anyone from one page into the next. The page overlap and every guard are
unchanged. The ban call has no sort parameter and was left alone.

## Not confirmed on live yet

The sort is a guess from what the API offers, not something measured. It is confirmed when, after the
deploy from master, `/api/health/sync` shows a member sweep that lists about the group's count and
finishes. If it still lists too few, the next step is to count how many repeats and gaps a walk has
with this sort (the `explore/OffsetProbe.cs` tool does one page at a time).
