# Demo mode

- **Date:** 2026-09-16
- **Status:** Built
- **Covers:** `MODBOT_DEMO=1`, the made-up data it fills a fresh deployment with, the fact that
  everyone who opens the site is an administrator, and how the data is put back
- **Depends on:** foundation §2.6 (configuration lives in the database), §4.1 (gate), §4.4 (clock),
  §5.2 (facts are append-only), §7.1 (setup wizard); accounts and access §5 (permissions);
  analytics (daily totals computed from facts)

---

## 1. What it is

`MODBOT_DEMO=1` on a **fresh** deployment starts Modbot full of a made-up VRChat group, with no
setup wizard and no sign-in. Anybody who opens the site sees the whole app, with every permission,
and can try everything on data that describes nobody.

It exists so people can be shown Modbot and trained on it without being handed a real group, and so
the project can host one publicly.

**AI is the exception to "nothing is real".** An operator sets an AI provider and key exactly as
usual, and moderation rules, insights, Chat and the spend limits all work — against the made-up
data. That is the point: AI is the part of Modbot that is hardest to explain and easiest to show.

## 2. When the variable is honoured, and when it is ignored

One method decides, once, during start-up: `DemoMode.Decide`.

```
already set up for real = not a demo database AND (a staff account exists OR the wizard was finished)
demo mode is on         = MODBOT_DEMO is set AND not already set up for real
```

"A demo database" is `Settings.DemoData`, a column only the demo seeder ever writes. Without it a
demo would refuse to be a demo on its own second start, because seeding creates staff accounts and
finishes the wizard.

So:

| The database holds | `MODBOT_DEMO=1` does |
|---|---|
| Nothing — no accounts, no finished wizard | Fills it with made-up data and opens the app to everyone |
| A demo it filled in earlier | Carries on as that demo |
| A staff account, or a finished wizard | **Nothing.** The variable is ignored, and Modbot says so in the log on start. Nothing is seeded and nothing is removed |

The decision is made **before the container is composed**, because a demo composes a different
host: no VRChat sync, no Discord bot, no mail. It reads the database directly rather than through
Entity Framework, because the migrations have not run yet — on a genuinely fresh deployment the
tables it asks about do not exist, and that is itself the answer.

`DemoMode.IsOn` throws if anything asks before the decision is made. A caller that reads "off"
during start-up and acts on it would be a bug that is silent in the wrong direction.

### 2.1 Why an environment variable

Foundation §2.6 says configuration lives in the database, and that a fourth environment variable
needs the same justification as the first three. This has it: demo mode decides whether there is a
sign-in page at all, so it cannot be a setting inside an app that has no way to sign in to it. And
the whole value of the switch is that a real deployment cannot turn it on by accident — which a row
somebody can edit in the app would not give.

`MODBOT_DEMO_RESET_HOURS` rides along with it. Default 24, `0` means never, and anything that is
not a whole number from 0 to 8760 leaves the default in place.

## 3. The security model, which is the whole of it

**Everyone who opens a demo is an administrator.** There is no password, no account to hold, no
permission check that can fail. `DemoAuthenticationHandler` builds a principal for the seeded demo
administrator on every request, holding `ModbotPermissions.Administrator`, and every existing
`RequiresFlag` and policy passes it.

That is stated plainly rather than buried because it has one consequence that must never be got
wrong: **a demo deployment must never hold real data about real people.** Not a real group's member
list, not a real Discord server's messages, not one real ban. Everything in a demo is public to
everyone who can reach the URL.

It is tolerable only because the path is narrow, and the narrowness is enforced in one place:

- `DemoAuthentication.MayServeEveryoneAsAdministrator(demo)` is the single method that can answer
  "yes, stop checking who this is". It is `demo is { Decided: true } && demo.IsOn`.
- The scheme forwarder asks it, and the handler asks it again, so a mistake in the forwarder is a
  request with no session rather than a stranger with every permission.
- `DemoMode.Decide` is the only thing that can make `IsOn` true, and §2 is the only condition under
  which it does.

Nothing else in Modbot works out for itself whether this is a demo.

### 3.1 What a visitor can do

Everything an administrator can. That includes changing settings, writing case files, closing
reviews and deleting things — which is fine, because §6 puts it all back.

### 3.2 What is refused

Anything that would reach outside Modbot or let somebody into it:

| Refused | How |
|---|---|
| VRChat | `DemoVRChatGate` replaces `IVRChatGate`. Every call returns "VRChat is off in the demo." with `NotConfigured`, which every caller in Modbot already handles — an unconfigured deployment is the ordinary first state of a real one |
| Discord | The bot is simply never registered: it connects on its own schedule once a token is stored, so the only sure way to keep a demo off Discord is not to have one. `DemoDiscordMessenger` replaces the direct-message sender |
| Email | `DemoMailRelay` replaces `IMailRelay` |
| Signing in, invites, reset links, the test email, pairing a companion | A short list of paths, refused before authentication with `403` and `{"error": "Off in the demo."}` |

The first three are replaced at the door rather than checked for at each call site. A demo that
dotted "unless this is a demo" through the codebase would depend on every future caller remembering;
replacing the gate, the relay and the messenger means no future caller can find a way round.

The rest of the companion surface — reporting presence, reading alerts — is left alone, so a
client somebody points at a demo still works. Only pairing is refused.

## 4. The data

A made-up group called **The Long Porch**, worked out in memory by `DemoPlan` before a single row is
written, from a fixed seed so the same demo rebuilds the same way. Every instant is measured back
from **now**, from `IModbotClock`, so the charts always run up to today and the Live page always has
rooms open, whenever the demo is started.

| | Roughly |
|---|---|
| VRChat profiles | 420 — bios, pronouns, status, account age, trust tags, 18+ flags |
| The mix | 8 on the team, ~250 regulars, ~70 newcomers, 22 repeat offenders, ~68 who left |
| Group membership | Joins, leaves and role changes across a year |
| Worlds | 12 |
| Rooms | ~600 over the year, each with who was in it and a head-count reading every ten minutes; a few open right now |
| Moderation | ~450 warnings, kicks and bans, some lifted; a case file with a written report for every ban |
| Reviews | Opened by the real review job from the real facts; most closed with an outcome, three left open |
| Discord | One server, 14 channels, 8 roles, ~300 members, several thousand messages across the year, ~1,200 voice sessions, and a handful of timeouts and kicks |
| Account links | About a third of the people have their Discord and VRChat accounts linked |
| Calendar | 6 events — four behind, two ahead |
| Keys | One API key, one webhook |
| Evidence | A file or two on the most recent case files |
| Facts | ~18,000 |

**Everything is internally consistent, and the analytics are not typed in.** A ban points at a
person who exists, in a room that existed, by a moderator who was on the team that week. The daily
totals are then produced by `DailyTotalsJob.RebuildAsync` from those facts — the same job that
computes them on a real deployment — and the repeat-offender counts and moderator baselines by the
same review job. There is no second story about the data; the numbers are the data.

Facts are written through `IFactWriter`, like every other producer, so they are append-only here
too (§5.2). The roles each person held are worked out by the plan and put in the payload, which
means the writer skips the three lookups it would otherwise make per fact — over eighteen thousand
facts that is the difference between a minute of seeding and most of an hour.

### 4.1 Pictures

Profile pictures, avatars and world images are two-colour gradients written straight into the `src`
as `data:image/svg+xml` URLs. Nothing is fetched from anywhere: a demo that pulled four hundred
thumbnails off an image host would break when that host rate-limited it, would not work at all on a
machine with no internet, and would put a third party in the middle of a page that is meant to have
no outside dependencies. They also look, at a glance, obviously not like photographs of anybody,
which is the right look for made-up people.

### 4.2 Retention

The demo sets every retention window to "keep it", so a year of charts does not empty out after
ninety days and make the demo look broken.

### 4.3 Live

The Live page shows rooms whose `GroupId` matches the managed group, that were seen in the group's
own list, and that have not closed. Who is in one is read from presence facts reported by a **paired
companion** whose owner has a linked VRChat account, so the demo seeds a client device for each
of the eight staff accounts and names one in every presence report.

### 4.4 Evidence

Evidence is stored in the database backend, so a demo needs no bucket and no disk. The files are
small labelled SVG panels, for the same reason the profile pictures are gradients.

## 5. Seeding, and how long it takes

Two halves.

**The quick half** — the settings, the team, the 420 people, the group membership, the 12 worlds,
the rooms and their head counts, the ban reasons and case files, the Discord server and its members,
the calendar, the API key and the webhook — runs **synchronously during start-up**, after the
migrations and before the first request. It is a few seconds. Nobody ever sees an empty demo.

**The heavy half** — ~18,000 facts, several thousand Discord messages, the daily totals rebuilt
from them, and the review job — runs afterwards in `DemoDataService`, and takes about a minute. The
app is fully usable throughout; the charts fill in behind it. `GET /api/demo` reports the step and
how far it has got, which the Sync health page shows as a line and the top bar shows beside the
demo label.

The plan built by the first half is handed to the second, so both halves describe the same group
down to the minute. If a process is killed between them, the quick half is simply done again —
which costs a couple of seconds and cannot leave the two disagreeing.

## 6. Putting it back

Seeded data drifts as people try things, so there are two ways to reset it:

- **A "Reset demo" control** in the app's top bar, beside the demo label. Everyone is an
  administrator here, so everyone can press it.
- **`MODBOT_DEMO_RESET_HOURS`**, default 24, `0` for never.

A reset wipes every table the demo owns and fills them in again, with the progress line saying so
the whole way through. It is deliberately not clever: a demo is made-up data, so there is nothing to
preserve and nothing to migrate, and the simplest thing that cannot leave half a group behind is to
empty the tables and write them again.

Two things survive a reset on purpose: the built-in roles, which belong to the migrations rather than
to the demo, and **the settings row, rewritten rather than deleted** — so an AI provider and key the
operator set, which is the whole point of demo mode, is not lost every time the data turns over.

The reset is asked for through `DemoState` and carried out by the background service within a few
seconds, rather than inside the request. A request that spent a minute wiping and re-seeding would
time out somewhere in the middle and leave the person who pressed the button with no idea whether it
had worked.

## 7. What is not in demo mode

- **No link to a live group.** The maintainer asked for this explicitly: dummy data only. There is
  no setting that points a demo at a real group, and the gate cannot reach VRChat to do it.
- **No accounts, no invites, no sign-in page.** Not hidden — absent.
- **No second demo in one database.** Run one Modbot per database, the same as always.
- **No way to turn a real deployment into a demo.** `POST /api/demo/reset` answers `404` anywhere
  but a demo, and nothing anywhere sets `Settings.DemoData` except the seeder.

## 8. Where it lives

| Path | What |
|---|---|
| `src/Modbot.Core/Configuration/DemoMode.cs` | The decision, and the only thing that can make it |
| `src/Modbot.Core/Configuration/DemoState.cs` | The progress line and the reset request |
| `src/Modbot.Demo/DemoPlan.cs` | The made-up group, worked out before anything is written |
| `src/Modbot.Demo/DemoWords.cs` | Every made-up name, bio, world and message |
| `src/Modbot.Demo/DemoSeeder.cs` | The quick half, and the wipe |
| `src/Modbot.Demo/DemoHistory.cs` | The facts, the messages and the totals computed from them |
| `src/Modbot.Demo/DemoOutside.cs` | The stand-ins for VRChat, email and Discord |
| `src/Modbot.Demo/DemoStartup.cs` | Deciding, and seeding, during start-up |
| `src/Modbot.Demo/DemoDataService.cs` | The background fill-in and the reset schedule |
| `src/Modbot.Api/Auth/DemoAuthentication.cs` | The sign-in scheme, and the one method that gates it |
| `src/Modbot.Api/Features/Demo/DemoEndpoints.cs` | `GET /api/demo`, `POST /api/demo/reset`, the refusals |
| `src/Modbot.Web/src/components/DemoMarker.tsx` | The label and the reset control |
| `docs/content/docs/self-hosting/demo-mode.mdx` | How to run one |
