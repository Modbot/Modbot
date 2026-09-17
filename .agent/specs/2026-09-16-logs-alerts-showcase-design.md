# Logs in the app, logs in Cloud, alerts by email, and the showcase

- **Date:** 2026-09-16
- **Status:** Built
- **Covers:** Modbot's log store and Logs page; sending the log to Modbot Cloud; Cloud's log storage
  and viewer; health alerts by email from both sides; the contributors, sponsors and early adopters
  Modbot shows on its Credits page
- **Depends on:** foundation §4.4.1 (the log streams), §5.5 (retention), §5.9.4 (the operational
  log's permission); central services §1.1 (what a server uses Cloud for); cloud event backup §0
  (this is the "server log feed" that spec set aside), §4.1 (the two databases), §8 (the limits)
- **Narrows:** cloud event backup §10.10 ("Modbot deployments sending their own logs to Cloud is a
  separate feature, not yet built, and will need its own statement"). §6 below is that statement.

---

## 1. What the maintainer asked for

> "Another feature of modbot cloud and modbot should be able to see and filter structured logs
> **inside the app** if you don't have Seq or filesystem access, with a 180 day default retention
> that can be changed. Logs can be sent to Modbot Cloud by default (can be turned off, for remote
> debugging and support services) and view them in Modbot Cloud for admins and instance owners with
> healthchecks that notify people by email when something is wrong (configurable in Modbot or Modbot
> Cloud). … Modbot Cloud should have an api endpoint that pulls Contributors from Github and a
> database table with API endpoints for Sponsors and Early Adopters so we can showcase them in
> Modbot from cloud, A group image link, group banner image link, and group ID to make a clickable
> link to their VRChat group."

And, asked which of three shapes the Cloud copy should take: **all logs, unchanged** — not
warnings-only, not anonymised.

---

## 2. The log store in Modbot

### 2.1 Why a fourth destination

Foundation §4.4.1 gives Modbot a console, three file streams and an optional Seq. Each assumes
something a real deployment may not have:

| Destination | Assumes |
|---|---|
| Console | somebody is watching it, or the platform keeps more than the last few hundred lines |
| Files | a disk that survives a redeploy |
| Seq | a Seq server, and somebody who set it up |

The one durable thing every Modbot has is its database. `modbot_log` is the fourth destination, and
the **Logs** page is how it is read.

### 2.2 What is stored

The same events as the main file stream: **Information and above**, with **outbound API traffic left
out**. Columns: `at`, `level`, `message`, `template`, `source` (Serilog's `SourceContext`), `area`
(`LogArea`), `exception`, `properties` (`jsonb`).

API traffic is excluded for size, not for tidiness. A busy sync writes tens of thousands of those
lines a day; six months of them would be the largest thing in the database by a wide margin, for a
stream that is already in `modbot_log_http_*.jsonl` and in Seq.

`MODBOT_DEBUG_LOGGING` and `LOG_LEVEL=Debug` do not change what goes in the table.

### 2.3 The sink

`DatabaseLogSink` is a Serilog sink with four rules:

1. **Never blocks the thread that logged.** `Emit` is one bounded-queue write. Rendering, redaction
   and PostgreSQL all happen on its own task.
2. **Drops rather than waits.** A full queue (10,000) means the database is slow or gone; the answer
   is to lose log lines, not to slow Modbot down. Drops are counted and the count is on the Health
   page, so "the log has gaps" is a fact somebody can see.
3. **Never logs its own failures through Serilog.** That is a loop. Failures go to Serilog's
   `SelfLog` and into the status the Health page reads.
4. **Batches.** Up to 500 rows, or two seconds, whichever comes first, written with `COPY`.

**It is built before the database is known to be reachable and connected after the migrations run.**
Logging must exist before the container does — the first thing an operator needs to read is why the
database could not be reached. So the sink queues from the first line and `Start(connectionString)`
is called after `DatabaseMigrator`; the startup lines are written then, rather than lost.

### 2.4 Secrets

The table is read in a browser by anyone holding `ViewOperationalLog` **and is sent to Modbot
Cloud** — both wider audiences than a shell on the host. So `LogSecrets`:

- replaces the value of any property whose **name** contains `password`, `passwd`, `secret`,
  `token`, `apikey`, `api_key`, `authorization`, `credential`, `cookie`, `privatekey`, `accesskey`,
  `sessionid`, `bearer`, `signature` or `passphrase`;
- removes a **bearer or basic credential** and a **connection-string password** from the rendered
  message and from the exception, whatever they were called.

**This is a net, not a promise.** The rule Modbot relies on is that nothing logs a secret. This
catches the mistake anyway, because the cost of the mistake here is a password on a moderator's
screen.

### 2.5 Retention

`Settings.LogRetentionDays`, **180 by default**, `0` keeps forever, edited under **Settings**,
**Host & Database**, **Logs**. A daily job deletes past the window, 10,000 rows at a time.

**Deliberately not foundation §5.5's "keep everything by default".** That rule is for a group's own
history, which cannot be filled in later. A log line is Modbot talking about itself, and six months
is longer than any question anybody asks of it.

**A delete, not a dropped partition**, which is how the fact log and the stored messages are pruned.
Those run to hundreds of millions of rows; this one is thousands a day with API traffic left out, so
a sliced delete is the simpler thing that works. If it ever grows past a few tens of millions,
partition it by `at`.

### 2.6 The page

**Logs**, in the sidebar under **Setup**, behind `ViewOperationalLog` — the same line §5.9.4 draws
for the operational half of the audit log and for the Health page.

Filters: search (message and exception), lowest level, source, area, and a day range. Paged by row
id rather than page number, because the table is written to constantly and a page number would show
the same line twice.

---

## 3. Sending the log to Cloud

### 3.1 On by default

Turned off under **Settings**, **Host & Database**, **Logs**, and off entirely when
`MODBOT_CLOUD_DISABLED` is set — that variable wins over the setting, as central services §1.1
requires of every Cloud feature.

Why it exists: a deployment the project cannot reach is a deployment nobody can help with. Cloud
holds a copy so the answer survives a redeploy, and so the operator can read it from somewhere their
own Modbot is not.

### 3.2 What is sent

The rows as stored, unchanged. The maintainer chose that over warnings-only and over anonymised.
§6 is the privacy statement it owes.

### 3.3 The place-marker, and what is dropped

**There is no second queue.** The shipper reads `modbot_log` from the row id it last sent, kept in
`Settings.CloudLogSentThroughId`. That survives restarts for free and cannot drift from what the
store kept.

| Case | What happens |
|---|---|
| Cloud unreachable | nothing is lost; the marker does not move; backoff 30 s → 10 min, honouring `Retry-After` |
| More than **50,000** rows behind | the marker jumps forward; the skipped **oldest** rows are counted into `Settings.CloudLogDropped` |
| `400` or `413` | that batch is skipped and counted — re-sending would never work and would block every line behind it |
| `401` | the install is forgotten and registered again next pass |

The oldest are what is dropped, because the newest are what somebody is asking about. A deployment
that comes back after a fortnight should not spend hours sending a fortnight of log lines nobody
will read.

A first-ever run starts 1,000 rows from the top, so turning this on does not send six months.

### 3.4 Identity

**The credential the server already has**: its id and secret in Cloud's server registry, which
`ServerReporter` establishes and keeps in the same settings row.

Not a registration of its own. A deployment registering with Cloud twice would give Cloud two ids
for one thing and no way to join them — and that join is exactly what lets the account which claimed
the server read its own logs (§4.3). An earlier revision of this feature did register separately,
because the registry had not landed when it was written; it was reversed the same day, before
anything shipped.

So: a server that has not registered yet sends nothing and says so on the Health page, and a `401`
is left alone — `ServerReporter` owns that credential and registers again on its own next pass, and
two things replacing one secret would fight.

---

## 4. Logs in Cloud

### 4.1 Where

The **engine** database (`DATABASE_ENGINE_URL`), beside the client events, as cloud event backup
§4.1 said it would be.

### 4.2 Partitioned by month, unlike the events beside them

| | `client_event` | `instance_log` |
|---|---|---|
| Volume | a few dozen an hour per client | hundreds of times that |
| Key | `(install_id, client_event_id)` — the client's own id, for exact retries | `(id, received_at)` — a sequence |
| Pruned by | a sliced delete | dropping whole months |

The events cannot be partitioned: a partitioned table cannot hold a unique key that leaves out its
partition column, and the event id is the only thing that makes a retry exactly safe (spec §4.3).
Log lines have no such id and are far more numerous, so they get the partitions.

**The partition key is `received_at`, Cloud's own clock, not `at`.** A deployment whose clock is
years out would otherwise write into a partition that does not exist and fail the whole batch. Both
times are stored and both are shown.

There is **no de-duplication**. Giving a log line an id would mean a unique index over the whole
table, which a partitioned table cannot hold without its partition key and which would cost more
than the problem. The sender advances its marker only after Cloud answers, so a retry re-sends only
when the answer was lost — a handful of repeated lines after a dropped connection is the right price
for not indexing hundreds of millions of rows.

### 4.3 Who may read them

**A Cloud administrator, or the account that claimed the server.** A deployment's log is its
operator's, not the project's: Cloud holds it so the project can help with a problem on a deployment
it cannot reach, and so the operator can read it from somewhere their own Modbot is not.

One method decides, `AdminLogEndpoints.MayReadAsync`. An administrator may leave the server out and
read across every deployment, which is what makes "is anyone else seeing this?" answerable; an
account must name a server it claimed, because there is no "everybody's logs" for an account.

### 4.4 Retention

`CloudSettings.LogKeepDays`, **180 by default** (the events' window is 365), `0` keeps forever.
Whole months are dropped, and only once a month's **upper** bound is past the cutoff, so nothing is
ever removed early.

### 4.5 Limits

Mirroring cloud event backup §8: 1,000 lines a batch, 1 MB compressed, **8 MB** decompressed (text
compresses far better than the event documents, so the same megabyte on the wire carries more), 30
batches a minute per install, 200,000 lines an hour per install.

A line is tidied rather than refused when it is odd — an unknown level becomes `Information`, a
message longer than 8 KB is cut, a property document past 16 KB is replaced with a note. A whole
batch thrown away because one line was long would lose the ninety-nine around it, which are the ones
somebody needs.

---

## 5. Alerts by email

### 5.1 Two halves, because they see different things

| | Watches | Sends through |
|---|---|---|
| **Modbot** | VRChat, the Discord bot, the audit log sync, the database size, AI spend, the email queue, whether its logs are reaching Cloud | its own SMTP, through the email queue and the daily limit |
| **Cloud** | whether that Modbot has gone quiet, and errors in the lines it did send | Resend |

**A Modbot cannot email anybody about being down.** The process that would send the mail is the
process that is not running, and the database holding the address is the one that cannot be reached.
That is the whole reason the Cloud half exists, and it is why the two are complements rather than
copies: Cloud cannot see whether VRChat is reachable or how big the database is, and Modbot can,
because it is up.

### 5.2 The state is a row

Both halves:

- going wrong sends **one** email;
- staying wrong sends nothing until the **quiet time** (6 hours by default) is up, and then says it
  again, because a problem nobody fixed is still a problem;
- coming back sends **one** email saying it is over.

In the database rather than in memory, so a restart during a problem does not start the emails over
— which is the failure mode that makes people turn alerting off.

The quiet time works like the unusual-activity alerts' (AI insights §8) without the "much worse"
exception: a sync that is broken is not twice as broken an hour later.

Turning a check off clears its state, so turning it back on later does not open with a recovery
email about a problem nobody was told about.

### 5.3 Who is told

- **In Modbot:** the staff accounts somebody ticked. Not every administrator — the person who keeps
  the server running is often not the person who moderates, and mail nobody wanted is mail everybody
  filters. An account with no address cannot be ticked; a disabled one is skipped.
- **In Cloud:** the address on the account that claimed the server, unless somebody typed another
  one. An owner setting this up for themselves never types their own address, and an address that
  follows the account cannot go stale when they change it. Turning it on for a server nobody has
  claimed needs an address, because an alert nobody receives is worse than no alert: it looks set
  up.

### 5.4 Two smaller decisions

**The storage check has its own size**, not the disk size on the Data settings screen. That one is a
what-if input the browser remembers and Modbot deliberately never stores; this is a line somebody
wants to hear about, which is a different thing and belongs to the alert that reads it.

**An email Cloud could not send does not start the quiet time.** Otherwise an outage at the mail
provider would silently swallow the one message that mattered.

### 5.5 Why Cloud uses Resend rather than the deployment's SMTP

Cloud is a service the project runs, not an appliance somebody self-hosts, so its mail is a key in
the environment rather than a form. And it could not borrow the deployment's SMTP even if it wanted
to: the whole point of that half is sending mail when the deployment is unreachable. With no
`RESEND_API_KEY` the checks still run and record what they found, visible in admin rather than
silent.

---

## 6. Privacy facts for the privacy policy

This is the statement cloud event backup §10.10 said this feature would owe.

1. **Modbot sends its own log to Modbot Cloud by default.** It is the one thing a self-hosted Modbot
   sends anywhere the project operates. The facts about members — bans, joins, messages, case files,
   evidence — never leave the operator's own database.
2. **What is sent is the log, unchanged**: Information and above, with the message, the time, the
   part of Modbot that wrote it, the exception, and the values the line carried. Not warnings only,
   and not anonymised.
3. **A log line can name a member.** It is Modbot talking about its own work, and that work is about
   people.
4. **Requests Modbot sends to VRChat and Discord are not included.**
5. **Credentials are removed before a line is stored at all**, so they are never sent (§2.4).
6. **It can be turned off**, in Settings or with `MODBOT_CLOUD_DISABLED`.
7. **A deployment is identified by a random id** made on first send, not by its group, domain or
   name.
8. **Cloud keeps them 180 days** by default, and only a Cloud administrator can read them — and,
   once accounts exist, the account the deployment is linked to.

---

## 7. The showcase

### 7.1 Contributors come from GitHub

Not from a table: GitHub already knows, and a second list kept by hand only goes out of date. Cached
six hours, so however many Modbots ask, GitHub is asked a few times a day — nowhere near its 60
unauthenticated requests an hour. A refusal is remembered for half an hour rather than retried on
every read, which is the shape that turns a rate limit into a ban. Bots are left out.

`GITHUB_TOKEN` is optional. The repository is private today, so without one the read is a 404 and
the answer is an **empty list** — the right answer, not an error.

### 7.2 Sponsors and early adopters are one table

The maintainer wrote "a database table … for Sponsors and Early Adopters", and they are the same row
with a different word on it: a name, a link, a picture, and for a VRChat group its id, its icon and
its banner. Two tables would be the same columns twice, two sets of endpoints and two admin screens.

A group id becomes a link to `vrchat.com/home/group/<id>`. It is never checked for shape (foundation
§3.1.1). A link that is not `http` or `https` is refused on the way in, because these rows are drawn
on every Modbot in the world.

### 7.3 Public to read, admin to write

The three reads need no credential: every Modbot shows them, and asking each to hold a key for a
list of names the project publishes anyway would be a credential for nothing. Writing needs the
Cloud admin sign-in — there is no undoing a name that should not have been there.

### 7.4 Modbot reads them through its own server

Not from the browser, so that a deployment with `MODBOT_CLOUD_DISABLED` set makes no request at all,
and so one deployment asks Cloud a few times a day rather than once per moderator per visit. Cached
six hours; a failure is remembered for ten minutes. Cloud being slow or down costs a tab on the
Credits page and nothing else.

---

## 8. Environment variables added

All optional; all on Modbot Cloud, none on a self-hosted Modbot.

| Variable | Default | What it does |
|---|---|---|
| `RESEND_API_KEY` | none | Sends Cloud's alert email. Unset means the checks run and nothing is sent. |
| `ALERT_FROM_EMAIL` | none | The address that email comes from. Unset means none goes. |
| `GITHUB_TOKEN` | none | Reads the repository's contributors. Unset means an empty list. |
| `GITHUB_REPOSITORY` | `binn/Modbot` | Which repository, as `owner/name`. |

A self-hosted Modbot gains no variable. `MODBOT_CLOUD_DISABLED` gains a second meaning: it stops the
log going to Cloud, and stops the Credits page asking for the showcase.

---

## 9. What is not built

- **Trends over the stored logs.** Nothing reads `instance_log` but the viewer and retention.
- **Anything on the companion.** Its log stays on the moderator's PC (M3 §10); this feature is
  about servers.
