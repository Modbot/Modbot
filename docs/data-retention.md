# Data retention

Modbot records what happens in your group as an append-only log of facts, and keeps daily
aggregates derived from it. This page says what is kept, for how long, and how to delete it.

## Modbot does not delete anything by default

There is no default retention period. A deployment nobody has configured keeps every fact forever,
and that is the intended way to run it.

This is not an oversight to be tidied up later. Group history cannot be backfilled: whatever
Modbot did not record while it was happening is gone, and no amount of API access brings it back.
A default that quietly destroyed presence history after ninety days would have been deleting the
answer to "is this person a regular?" for everyone who joined more than three months ago — which
is most of what Modbot exists to be able to tell you.

| | Examples | Default retention |
|---|---|---|
| **Moderation facts** | bans, kicks, role changes, membership changes, audit-log entries, Modbot's own logins and settings changes | forever |
| **Presence facts** | instance joins and leaves, avatar changes, voice sessions, and Modbot's operational log | forever |
| **Rollups** | daily aggregates: member counts, join and leave rates, per-moderator action counts | forever |

Both fact retentions are configurable if you want a window. Rollups are never aged out, so charts
keep their full history even where the underlying events have been removed — the aggregate
survives, the individual rows do not.

## What keeping everything costs

Less than people expect. One fact occupies about **326 bytes**, indexes included — measured, not
estimated.

| Your group | Facts per day | Per year | At $0.25/GB/month |
|---|---|---|---|
| 100–10,000 members, 1–2 instances | ~2,000 | **240 MB** | about 6¢/month |
| 16,000 members, 3–5 instances | ~20,000 | **2.4 GB** | about 60¢/month |
| 150,000 members, 30 instances | ~432,000 | **51 GB** | about $13/month |

Most groups are in the first row, where a retention policy would save pennies a year.

### The settings page shows your numbers, not these ones

Under **Settings → Data**, Modbot reports what your own deployment is doing:

- **Current usage** — real bytes on disk, asked of the database directly, including indexes.
- **Growth rate** — facts per day, measured from your own fact log.
- **Projected size** at 6, 12 and 24 months if that rate continues.

Enter either a **per-GB monthly price** (if you pay for hosting) or your **disk size** (if you host
at home), and the projection is restated as a monthly cost or as the date you would run out of
room.

Two things are worth knowing about those projections. They are a straight line, which real growth
is not — a group that opens more instances generates more facts per member — so treat them as an
order of magnitude rather than a forecast. And Modbot will not extrapolate at all from less than a
day of history; a brand-new deployment shows its size and its rate, and no projection, until it has
watched for long enough to have something to say.

## Privacy

Presence data — who was in which instance, for how long, wearing what — is personal information,
even though it is all data your group could observe directly at the time. Modbot's position is
unchanged by keeping it longer: it lives on your own server, its retention is yours to set, and
**deletion works regardless of your retention settings**. Somebody asking to be erased is not
asking about your disk space.

Modbot never stores Discord message content. Message volume is counted per person per day and no
record of an individual message is written.

## How deletion works

Expired facts are removed by destroying the monthly table they are stored in, not by deleting rows
one at a time. A month is only destroyed once everything in it is past its retention; where a month
still holds moderation facts, it is rebuilt containing only those.

**Purge user** erases every fact about one person across all of history, together with any
per-person counts held only as aggregates. Two things deliberately survive it:

- Records of moderation actions that person *performed* on someone else. Those are records about
  the people they were applied to, and erasing them would delete someone else's moderation history.
- The fact that a purge happened, and how much was removed. It does not record who was purged.

Daily aggregates are recomputed as part of the purge, so nothing that was deleted is still being
counted.
