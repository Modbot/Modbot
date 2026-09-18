# Modbot — Machine Usage on Host & Database

- **Date:** 2026-09-18
- **Status:** Implemented with this document
- **Covers:** what Modbot can honestly read about the machine it runs on and what it cannot (§2);
  the sampler, its interval and its window (§3); `GET /api/health/machine` and the permission it
  takes (§4); the Machine usage card at the bottom of Settings → Host & Database (§5); what was
  deliberately left out (§6)
- **Related:** foundation §4.4 (one clock); spec 5.5 (the Data settings screen); sync health design
  (the `ViewOperationalLog` line between "Modbot's record about itself" and everything else)

---

## 1. What this adds

The Host & Database tab said what Modbot is **storing** and nothing about what it is **costing to
run**. An operator deciding whether to give the container more memory, or wondering why the app
feels slow, had to leave Modbot and open their host's dashboard — and on a home server there is no
dashboard to open.

So: a **Machine usage** card at the bottom of that tab, with a small chart per figure over the last
half hour — processor use, memory held, disk read and written — and the current value above each.

Three figures, three charts, because they are three measures at three scales and one axis can carry
only one of them.

## 2. What can be read honestly, and what cannot

Everything here is about **this process**. That is the whole of what Modbot can measure without
being told about the machine it was put on, and inside a container it is also the useful answer,
because the container is what the operator sized and pays for.

### 2.1 Processor use — derived, never read

There is no such thing as an instantaneous processor reading. What the operating system keeps is a
**running total** of processor time this process has used, and the useful figure is the difference
between two of those divided by the wall-clock time between them, divided again by how many
processors that time could have been spent on:

```
percent = (later − earlier) / (seconds × processors) × 100
```

`Environment.CpuUsage.TotalTime` is the total, and `Environment.ProcessorCount` the divisor. Both
are the runtime's own, and both are already container-aware: inside a container with a half-CPU
share, `ProcessorCount` is the share, not the host's core count. Reading `/proc/stat` instead would
be a second implementation of something the runtime already does, and it would answer about the
whole machine — a number that means something else.

The result is clamped to 0–100. A reading that straddles a step in either counter can come out
slightly outside that, and neither a negative processor nor a 140% one is a thing a reader should
be shown.

**Consequence:** the first reading after a restart is only a baseline. Nothing is drawn until the
second, ten seconds later. That is a property of the measurement, not a loading state, and the card
is simply absent until then (§5.2).

### 2.2 Memory — two different questions

- **How much is Modbot holding?** `Environment.WorkingSet`: memory resident for this process.
- **How much is it allowed?** The **control group** limit, not the machine's total memory. Inside a
  container the machine's total is whatever the physical host has, which says nothing about what
  this container may use before it is killed.

The limit is read from the control group file directly, because there is no API for it:

| Version | File | "No limit" is written as |
|---|---|---|
| cgroup v2 | `/sys/fs/cgroup/memory.max` | the word `max` |
| cgroup v1 | `/sys/fs/cgroup/memory/memory.limit_in_bytes` | a number near the largest a 64-bit count holds |

Both versions are tried, in that order, and every way the read can fail — the file absent (any
non-Linux host, and a Linux without the controller mounted), unreadable, or holding something that
is not a number — is a null rather than an exception. A limit at or above 2^62 bytes means there is
none: that is how v1 says so, and 4 EiB of memory is not a machine anybody is running Modbot on.

When no control group answers, `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` is the fallback —
the runtime's own idea of what it has to work with, which is the memory fitted on a plain machine
and is the right answer there.

### 2.3 Disk activity — the least portable, and the most easily misread

`/proc/self/io` carries, for this process:

- `read_bytes` / `write_bytes` — bytes that actually went to and from a block device.
- `rchar` / `wchar` — bytes moved by every read and write call, including the ones the page cache
  answered without touching a disk.
- `cancelled_write_bytes` — writes that were thrown away before reaching the disk.

Only the first pair is reported, as bytes a second between two readings. `rchar`/`wchar` would be a
larger, friendlier-looking number that does not mean disk activity, and this is the exact trap the
figure had to avoid. Lines are matched whole, so `cancelled_write_bytes` is never mistaken for
`write_bytes` by a suffix match.

**What this figure does not include, and why it is still worth showing.** It is Modbot's own disk
traffic. When PostgreSQL runs in another container — which it does on Railway and in most Docker
setups — the database's disk work is not in it. So the chart is labelled **Disk** on a card about
this server, alongside this server's processor and memory, and the docs say plainly whose figure it
is (`docs/content/docs/self-hosting/data-retention.mdx`). The alternative considered and rejected
was the control group's own `io.stat`: the io controller is frequently not delegated into a
container, so it is absent exactly where it would have been most useful, and where it is present it
still excludes the database container.

**Where it cannot be read at all** — every non-Linux host, and any Linux where `/proc` is not
mounted for this process — both counters are null and the chart is left out (§5.2). It is never
reported as zero.

### 2.4 The clock

Every reading is stamped from `IModbotClock` (foundation §4.4). The sampler reads no clock of its
own, which is also what makes the rate arithmetic testable: a fake clock advanced by ten seconds is
ten seconds.

## 3. The window: ten seconds, thirty minutes

`MachineUsageSampler` keeps a queue of points in memory, filled by `MachineUsageService`, a
background service.

- **Every ten seconds.** Fine enough that a sync pass, an import or a large export shows as a bump
  rather than being averaged flat; coarse enough that the sampling itself costs nothing worth
  measuring (two small file reads and two runtime properties).
- **For thirty minutes.** As far back as the question reaches. Somebody who has just noticed the
  app is slow wants the last few minutes; anybody asking about last week is asking a question this
  was never built to answer, and answering it would mean a table.

That is **180 points**, each five numbers — a few kilobytes. The queue is trimmed to the capacity on
every append, so the oldest is dropped and a server running for a week costs exactly what one just
started costs.

**Nothing is written to the database.** No table, no migration, no retention window to configure,
nothing to prune, and nothing that survives a restart. This is a diagnostic, and the whole design is
chosen so that it cannot grow into a metrics system by accident: there is no place to put a longer
window without adding storage, which is the point at which somebody should stop and ask whether
Modbot should be doing this at all.

The first sample is taken the moment the host starts, without the settling delay other background
services take. The first reading is only a baseline, so delaying it would mean the screen is blank
for whoever opens it right after a deploy — which is exactly when somebody is watching a new build
settle. A reading that throws is said once, at debug, and never again: a host where a counter cannot
be read will fail every ten seconds forever, and an operator does not need to be told 8,640 times a
day that a diagnostic is unavailable.

## 4. The endpoint

`GET /api/health/machine`, behind **`ViewOperationalLog`** — the same permission the sync health
detail takes, and deliberately not a new one. It is the same kind of answer: Modbot's operational
record about itself, useful for deciding whether the server needs more memory and meaningless to a
moderator. A figure like this should not arrive with a permission of its own for somebody to have to
discover and grant.

It returns the window and what the window is measured against:

```jsonc
{
  "sampleSeconds": 10,
  "windowMinutes": 30,
  "processors": 4,
  "memoryLimitBytes": 2147483648,   // null when nothing sets one
  "now": "2026-09-18T14:05:00Z",    // the server's clock, not the browser's
  "points": [
    {
      "at": "2026-09-18T13:35:10Z",
      "processorPercent": 4.2,       // null where unreadable, never 0
      "memoryBytes": 402653184,
      "diskReadBytesPerSecond": 0,
      "diskWrittenBytesPerSecond": 2048
    }
  ]
}
```

**Null means unavailable; zero means idle.** Keeping them apart is the reason every figure on a
point is nullable rather than defaulted, and the reason the screen can leave a chart out instead of
drawing a flat line along the floor.

The sampler is resolved **optionally**, the way `SyncDiagnostics` is on `/api/health/sync`: a host
that maps the API without registering the background services answers with an empty window rather
than failing to resolve a service in the middle of a request. That is every test host, and the build
that writes the OpenAPI document.

The endpoint is in the published API reference (`docs/openapi/modbot.json`), unlike `/api/health/sync`
— there is nothing here that is an internal of a sync job, and an operator scripting a check of
their own deployment is a reasonable thing to do.

## 5. The screen

`MachineUsageCard`, the last card on Settings → Host & Database, full width, with one chart per
figure across three columns on a wide screen and stacked below `lg`.

Each chart carries its name, its current value, and the window as a line. Recharts through the app's
existing chart pieces (`ChartFrame`, `rechartsTooltip`, the `--series-*` tokens), so it moves with
light, dark and VR like everything else and nothing here carries a colour of its own.

- **Processor** — one line, axis in percent. The axis top is the next ten percent above the busiest
  reading, floored at ten and capped at a hundred: a floor stops an idle server's noise between one
  and two percent being stretched into a chart that looks like a machine in trouble, and growing in
  tens keeps it comparable with the last time somebody looked.
- **Memory** — one line, axis in bytes, drawn **against the limit** when there is one, so the chart
  answers "how close am I to what I was given" rather than only "how did my own use move". The value
  reads `384 MB of 2.0 GB`.
- **Disk** — two lines, read and write, with a dot and a name beside each value, because identity
  must never ride on colour alone.

No sentence anywhere explains what processor use is (CLAUDE.md, "UI text"). A label names a figure;
the value says how much.

### 5.1 Refreshing

The card refetches at the rate the server says it samples, so a screen left open follows the
machine. The rate comes from the response rather than being written twice.

### 5.2 When a figure, or all of them, cannot be read

- A figure no reading in the window carries is **left out** — no chart, no axis, no zero line.
- When nothing at all can be read, the card **is not rendered**. That covers a host with no counters
  at all, the first ten seconds after a restart (§2.1), and a signed-in account without
  `ViewOperationalLog`, whose request is refused.
- A request that fails is not given an error line of its own. A diagnostic nobody can read is not
  worth a paragraph on a screen full of settings that do work; the card simply is not there, and the
  Logs page is where a failing server is diagnosed.

## 6. Deliberately left out

- **Any figure about the whole machine.** Host processor and host memory are not readable honestly
  from inside a container without being handed something Modbot is not handed. `/proc/stat` and
  `/proc/meminfo` describe the physical host even when read from inside a container, and showing
  "8% of 64 GB" for a container limited to 2 GB would be worse than showing nothing.
- **The database's own usage.** It usually runs in another container, and Modbot has no counters
  for it. The Storage card answers the database question that can be answered honestly — its size.
- **Alerts on these figures.** Health alerts (logs and alerts design) are about things that have
  gone wrong and stay wrong. A processor that was busy for a minute is not one of those, and a
  memory alert worth having needs a longer history than thirty minutes in memory.
- **Storing any of it.** See §3.
