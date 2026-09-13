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
| **Case files** | the written rationale for a ban, the evidence attached to it, and the snapshot of the subject's profile at the time | forever — **and see the exception below** |
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

### Evidence files are the exception to every number above

Those figures are about **facts**, and a fact is small. A video attached to a ban report is not.

| | Size |
|---|---|
| One fact | 326 bytes |
| One screenshot | 1–5 MB |
| One short clip | 20–100 MB |

A single 100 MB video is about **320,000 facts** — roughly five months of everything the typical
group in the first row records. The moment you start attaching evidence, it is the evidence that
decides your storage bill, not the history.

They also grow differently, which matters more than the ratio. Facts arrive at a steady rate, so
projecting them forward is meaningful. Evidence arrives per ban: nothing for a month, then four
videos in a bad weekend. Modbot therefore measures and projects the two **separately** and shows them
as separate lines, rather than fitting one trend through both and being wrong about each.

Where those files live is your choice — an S3-compatible bucket (recommended), a mounted disk, or
inside the database. Only the last one lands in the numbers above, and it is the option Modbot
recommends against, because it also lands in every `pg_dump` you take.

### The settings page shows your numbers, not these ones

Under **Settings → Data**, Modbot reports what your own deployment is doing:

- **Current usage** — real bytes on disk, asked of the database directly, including indexes.
- **Growth rate** — facts per day, measured from your own fact log.
- **Projected size** at 6, 12 and 24 months if that rate continues.
- **Evidence**, counted and sized on its own line, wherever you have chosen to keep it.

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
**deletion works regardless of your retention settings** — with one documented exception, stated
immediately below rather than buried, because it is a real limit on a promise this page makes twice.

Somebody asking to be erased is not asking about your disk space.

### The exception: a case file survives being purged

**Purging a user does not delete the case files your moderators wrote about them.** The written
rationale for a ban, the evidence attached to it, and the snapshot of that person's profile taken at
the time are all retained.

This is not an oversight and it is not a technical limitation. **A case file is your group's record
of its own decision** — the justification for something your moderators did, and may have to defend
to the person it was done to, to the rest of your staff, or to another group.

If a purge deleted it, then anyone your group banned could erase the evidence of why, on request, at
any time. That would make "purge user" a tool for laundering a moderation history, and it would
quietly undermine the accountability features that exist to catch a moderator acting out of a grudge
— those work by examining the record, and they are only as trustworthy as the record.

It is the same reasoning that already keeps records of actions the purged person *performed* on
somebody else: those are records about other people, and they are not the purged person's to delete.

Everything else still goes. Every instance they joined, every session, every avatar they wore, every
count they contributed to — all of it, across all of history.

**And the receipt says so.** A purge tells you exactly what it kept and why, by name and count:

> Purged 41,208 facts.
> **Retained: 2 case files** (bans on 14 January and 2 April), including their written rationale,
> 3 evidence files, and the profile snapshots taken at the time. Case files are your group's record
> of its own moderation decisions.

If you decide a particular piece of evidence should go, an administrator can delete it — deliberately,
one file at a time, with the deletion itself recorded. That remains possible. It is simply not
something a purge does on your behalf without telling you.

Modbot never stores Discord message content. Message volume is counted per person per day and no
record of an individual message is written.

## How deletion works

Expired facts are removed by destroying the monthly table they are stored in, not by deleting rows
one at a time. A month is only destroyed once everything in it is past its retention; where a month
still holds moderation facts, it is rebuilt containing only those.

**Purge user** erases every fact about one person across all of history, together with any
per-person counts held only as aggregates. Three things deliberately survive it:

- Records of moderation actions that person *performed* on someone else. Those are records about
  the people they were applied to, and erasing them would delete someone else's moderation history.
- **Case files written about them** — the rationale, the evidence, and the profile snapshot. See
  *The exception* above, which explains why at length, because it is the one place this page's
  promise about deletion does not hold.
- The fact that a purge happened, and how much was removed. It does not record who was purged.

Daily aggregates are recomputed as part of the purge, so nothing that was deleted is still being
counted.
