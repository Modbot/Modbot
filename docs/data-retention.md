# Data retention

Modbot records what happens in your group as an append-only log of facts, and keeps daily
aggregates derived from it. This page says what is kept, for how long, and how to delete it.

## What is kept, and for how long

| | Examples | Default retention |
|---|---|---|
| **Moderation facts** | bans, kicks, role changes, membership changes, audit-log entries, Modbot's own logins and settings changes | forever |
| **Presence facts** | instance joins and leaves, avatar changes, voice sessions, and Modbot's operational log | 90 days |
| **Rollups** | daily aggregates: member counts, join and leave rates, per-moderator action counts | forever |

Both fact retentions are configurable, including "keep forever". Rollups are not aged out, so
charts keep their full history even after the underlying events are gone — the aggregate survives,
the individual rows do not.

Presence data — who was in which instance, for how long, wearing what — is personal information,
even though it is all data your group could observe directly at the time. Modbot's position is
that it lives on your own server, its retention is yours to set, and deletion works.

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
