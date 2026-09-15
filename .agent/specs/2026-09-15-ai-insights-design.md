# Modbot — AI Insights

- **Date:** 2026-09-15
- **Status:** Built (first version)
- **Covers:** Settings → AI → Insights, the insights stored from it, where they are read, and the
  unusual-activity alerts of §8
- **Depends on:** AI Base settings (`IAiClients`), daily totals (foundation §5.4), analytics pages (§10.1),
  M8 §2 and §6

---

## 1. What an insight is

The maintainer asked for "AI Insights for configuring AI insights and etc", with no more detail. This is
the first version chosen for it.

An **insight** is a short piece of writing by the AI model about Modbot's **own figures** for a stretch of
days: what went up, what went down, and what stands out against the stretch before. It is stored with
the exact figures the model was given, so anyone reading it can check every sentence against the
numbers.

There are three kinds, one per question a moderator already asks the analytics pages:

| Kind | Question | Figures |
|---|---|---|
| `group` — Group | Is the group growing, and how active is it? | Members VRChat reported at the end of each stretch; joins, leaves, invites, join requests, requests approved and rejected, bans, rooms opened, Discord messages; busiest worlds by visitors. |
| `team` — Moderation team | How much moderation happened, and of what kind? | Each kind of moderator action (kicks, warns, bans, unbans, removals, invites, approvals, rejections, role changes); how many moderators took any action; reviews opened; case files written. |
| `rooms` — Rooms | What ran, and how busy was it? | Rooms opened; typical minutes open; the most people in one room; worlds by rooms opened; the busiest rooms (world, day, most people). |

Each insight covers whole UTC days, like every other day in Modbot (foundation §5.4): **a day** is
yesterday, **a week** is the seven days ending yesterday. It is always compared with the stretch of the
same length just before.

### 1.1 Why figures and not records

The model is given counts, not facts about people. No VRChat or Discord id, no display name and no
message text is ever put in front of it — the only names it sees are world names, which are public.
That keeps two things true at once:

- An insight cannot become a report about a person (M8 §6), because the model knows nothing about
  any person.
- A deployment on a hosted provider sends it nothing about its members (M8 §4.2).

The moderation team kind is therefore about the team as a whole. It says "4 moderators took an
action", never who took the most.

### 1.2 What an insight never does (M8 §2, §6)

- It never takes an action, opens a review, writes a ban reason, or suggests acting against anyone.
- It never gives a score, rating or single number about a person or about the group's "health".
- The instructions tell the model to use only the figures given, to say plainly when a figure is
  missing or zero, and not to state a guess about why something changed as though it were known.

The last point is an instruction, not a guarantee; that is why the figures are stored beside the text.

## 2. Settings

Settings → AI → Insights (`#ai/insights`), behind **Change settings** like the rest of the tab.

- **Time zone** — an IANA name such as `Europe/London`, picked from the browser's list. Only decides
  *when* a scheduled insight is written. Modbot had no time zone anywhere before this; the figures stay in
  UTC days.
- **Model** — optional. Empty means the Base model.
- For each kind:
  - **On** — off by default.
  - **Every** — day or week, with a weekday for a week.
  - **Time** — the hour of the day, in the time zone above.
  - **Discord channel** — optional. When set, each scheduled insight of that kind is also posted there.
  - **Generate now** — writes one straight away for the stretch ending yesterday and shows it with its
    figures. It is stored like any other, but is not posted to Discord: somebody pressing the button is
    checking what it looks like.

Nothing runs while AI is switched off on Base.

## 3. Scheduling

`InsightScheduleService` checks once a minute. For each kind that is on, it works out the most recent
moment the schedule named. If that moment is later than the one last handled, it claims it with a
conditional update (so two runs cannot both write it) and writes the insight.

- Saving a schedule marks the latest past moment as handled, so turning a kind on at 3 pm with a 9 am
  time does not write one immediately.
- A server that was down for three days writes one insight when it comes back, not three.
- A failed call is stored with its error and not tried again until the next scheduled moment. The error
  shows on the settings card.
- The clock is `IModbotClock` (foundation §4.4).

## 4. Delivery

- **Web app:** the My Group analytics page shows the latest insight of each kind, with earlier ones and
  the figures a click away. Reading needs **View analytics**, the same as the pages the figures come
  from. Failed attempts are not shown there.
- **Discord:** `InsightPostService` in Modbot.Discord posts insights that name a channel and have not been
  posted, while the bot is ready. A channel that is gone or not allowed is recorded as a permanent
  failure and not tried again; any other failure is tried again on the next pass for up to a day, after
  which the insight is too old to be worth posting.

Modbot.AI does not reference Modbot.Discord: the insight row carries the channel, and the Discord side
picks it up, the same way the moderation log reads the fact log.

## 5. Storage

- `modbot_insight_settings` — one row: time zone, model.
- `modbot_insight_schedule` — one row per kind: on, every, hour, weekday, channel, last moment handled.
- `modbot_insight` — one row per insight: kind, first and last day, how it started (schedule or
  button, and who pressed it), model and provider, the figures as `jsonb`, the text or the error, and
  the Discord outcome.

Insights hold no personal data (§1.1), so they are not covered by retention or purge-user.

## 6. Usage and spend limits

Added 2026-09-15 at the maintainer's request that every AI feature has spend limits and cost estimates
broken down by feature.

- Every insight call records one row in the shared AI usage table (`src/Modbot.AI/Usage`): the input,
  output and cached tokens the provider reported, OpenRouter's reported cost where there is one, the
  model asked for, feature `insights`, and the person only when somebody pressed Generate now. A
  scheduled insight has no person.
- A failed call that the provider never counted records nothing.
- Before a call, the limits are read through the same shared place: the limit for everyone and
  insights' own daily and monthly limits, both in money (AI chat design §10.3). Person and role limits
  are Chat's and do not apply, even to Generate now. When a limit is reached, a scheduled insight is
  skipped (its moment still counts as handled, so it is not written late once the limit resets) and
  Generate now answers 409 with the limit's sentence, such as "The monthly AI spend limit for Insights
  is reached."
- Insights first shipped with a monthly token limit because Modbot had no prices; that becomes a money
  limit once the model has a price, and until then it is kept and counted (AI chat design §10.5).
- Limits, spend by feature and the month-end estimate are on Settings → AI → Limits.

## 7. Not in this version

- ~~An insight straight after a big event (a room far busier than usual). It needs a rule for "big"
  that is not a guess, and the Rooms kind covers the same ground a day later.~~ **Built as §8.** The
  rule for "big" is §8.2, and it is not a guess: it is that deployment's own recent history.
- Discord server figures beyond the message count. The Discord analytics being built now can add
  figures to the Group kind as they land.
- Asking follow-up questions about an insight. That is AI Chat.

---

## 8. Unusual-activity alerts

Added 2026-09-15 at the maintainer's request: "a spike of new accounts joining, a burst of flags, or
a sudden drop in active members. A short Discord post when one happens, not only the scheduled
summaries."

An **alert** is a note that one figure Modbot watches ran far outside what that deployment's own
recent history says is normal. It is not an insight: it is not scheduled, it is not written by the
model, and it carries figures rather than prose. The model's only part is one optional sentence
about those figures, and the alert goes out whether or not it gets one.

Everything §1.1 and §1.2 say still holds. An alert names counts, a stretch of time, and at most a
world or room. It never names a person, never scores anybody, and never says what to do.

### 8.1 What is watched

Ten watchers, each switched on and off on its own, each with its own sensitivity.

| Watcher | Figure | Window | Least figure |
|---|---|---|---|
| `vrchat-joins` — People joining the group | `vrchat.group.member.join` facts | an hour | 5 |
| `discord-joins` — People joining Discord | `discord.member.join` facts | an hour | 5 |
| `new-accounts` — New accounts joining | of those joiners, the ones whose VRChat account was made less than 30 days before they joined | an hour | 3 |
| `flags` — AI flags | `modbot.ai-moderation.flag` facts | an hour | 3 |
| `actions` — Moderation actions | bans, removals, room kicks, warnings, rejections, blocks, and Discord bans, kicks and timeouts, added together | an hour | 3 |
| `leaves` — People leaving | `vrchat.group.member.leave` and `discord.member.leave` | an hour | 5 |
| `rooms-opened` — Rooms opening | `vrchat.group.instance.create` facts | an hour | 3 |
| `room-filling` — A room filling up | the head count of the busiest open group room, against the peaks of rooms opened in the last 14 days | now | 8 |
| `room-unwatched` — Nobody watching a busy room | the busiest open group room with no moderator's client in it | now | 8 |
| `active-drop` — Fewer active members | people who sent a message or were in voice, in the week ending yesterday against the four weeks before | a week | 20 |

VRChat joins and Discord joins are watched separately on purpose: they are different doors, they
move for different reasons, and adding them would hide a spike on one behind a quiet day on the
other.

"Active member" means what the My Server page already means by it — somebody with a per-person row
in the message or voice daily totals — so the alert and the chart cannot disagree. A deployment with
no Discord server has no active figure, and the least figure keeps that watcher silent rather than
alerting on nothing.

### 8.2 How "unusual" is decided

One rule, in one testable place: `UnusualRule` in `src/Modbot.AI/Alerts`. Pure — no clock, no
database.

It is given the figure for the window just ended and the same figure for each **matching earlier
window**: the same hour of the day on each of the last 14 days, or the four weeks before this one.
Matching, because a Friday at nine in the evening has nothing to do with a Tuesday at four in the
morning, and comparing them would alert every Friday evening forever.

- **Normal** is the *middle* of the earlier windows, not their average.
- **Spread** is the middle of how far each earlier window sat from normal, never treated as less
  than 1.

The middle rather than the average because one past spike would drag an average up far enough to
hide the next one — which is exactly the case the whole feature exists for.

A **spike** is unusual when all three hold:

1. it is at or above the watcher's **least figure** (the table above), so a quiet group does not
   alert on two joins;
2. it is at or above `normal + spreads × spread`, where `spreads` is **6 / 4 / 2.5** for low /
   normal / high sensitivity;
3. it is at least **half again** the normal figure. Without this a big steady number — three hundred
   joins an hour, give or take two — would alert on a wobble of six.

A **drop** (only `active-drop`) is unusual when normal is at or above the least figure and the
current figure is at or below `normal × share`, where `share` is **0.5 / 0.65 / 0.75** for low /
normal / high. The least figure is checked against *normal*, not against the current figure: a week
with three active members out of a usual five hundred is the alert, not the thing that silences it.

Nothing fires with **fewer than five earlier windows** (three, for the weekly watcher, which only
ever has four). A deployment two days old does not know what its own Friday looks like, and
guessing is worse than saying nothing.

**Score** — how many spreads the window sits from normal — is stored on every alert. It is what
"much worse" is measured in (§8.4).

`room-unwatched` is the one watcher that is not a comparison, because it is a state and not a
change: a room exactly as full as every other Friday still wants somebody in it. Its bar is the
group's own normal busiest room (the middle of the last 14 days' room peaks) times **2 / 1.5 / 1**
for low / normal / high, and never below its least figure.

### 8.3 What an alert holds

- The watcher, its label, and what the figure counts in plain words ("joins", "flags").
- The window, the figure, normal, the spread, the score, and the sensitivity it fired at.
- Every matching earlier window, stored as JSON, so anybody can check the rule's arithmetic.
- The world or room, for the two room watchers.
- A path into Modbot: the members list filtered to the people who joined in that window
  (`/?joinedFrom=…&joinedTo=…`), the Flags page, the audit log, the Live page, or an analytics page.
  The Discord card turns it into a link when the deployment's public address is set.
- Optionally **one sentence** written by the model, under the same rules as an insight: it is given
  the figures object above and nothing else, it is told never to name anybody or suggest an action,
  and it is asked for at most 40 words. Its calls are recorded against the **Insights** feature's
  budget (§6), because an alert sentence and an insight are the same kind of spend from the same
  settings.

**If AI is off, at its spend limit, or the call fails, the alert still goes out with the figures
alone.** The sentence is a courtesy; the figures are the alert. A provider having a bad afternoon is
not recorded on the alert either, because an error about the model is not something to put in front
of a moderator who wanted to know about forty joins.

### 8.4 Where alerts go

- **A card** at the top of My Group and of Health, listing alerts from the last 24 hours that nobody
  has hidden. Reading needs **View analytics**, the same as the figures they are drawn from.
- **A Discord post** to the channel chosen for alerts — its own picker, so it can be somewhere
  noisier than the insights channel. One card: the figure, what normal is, the window, and a link.
  Posted by `AlertPoster` in Modbot.Discord, which reads the alert rows the same way insights are
  posted; Modbot.AI still knows nothing about Discord. Given up on after six hours, because an alert
  about an hour last night is not news.
- **A fact**, `modbot.insight.alert`, whose subject is the watcher and whose payload is the figures.
  It is sendable like any other fact, so the Discord event routes can put it anywhere else too.
- **Dismissing** one hides its card and keeps the alert and the fact. It is not a decision about
  anything; it is a card being put away.

**Repeats are held down.** After an alert, the same watcher says nothing for the **quiet time**
(default 6 hours) unless it gets **much worse** — twice as far from normal as the one that was
posted. Without the override a burst that keeps growing would be reported once, at its smallest.

### 8.5 Checking, and what it reads

The checks run in the existing insights background service, which already wakes once a minute. The
checker keeps its own spacing in the database (`last_checked_at`) and returns immediately the rest
of the time, so this is one small read a minute and a real pass every **15 minutes**. The time is
always `IModbotClock`.

A window ends at **now rounded down to the quarter-hour**, so this window and the matching windows
on earlier days line up exactly. Each window is judged once; a watcher records the window end it
last judged.

A real pass reads **four things at most**, and two of them only when something needs them:

1. **One grouped count over the fact log** — every type any watcher counts, over 15 days, bucketed
   by how many days back the window is. The range sits on the `(type, occurred_at)` index and prunes
   to two or three partitions; the bucketing is arithmetic on rows that range already picked.
2. **The same again for new accounts**, joined to `vrchat_user` for the account's age.
3. **The open group rooms**, their head counts and who is watching them — only while a room watcher
   is on. This is the Live page's own reader, which that page already runs every five seconds.
4. **Active members per week**, from the daily totals — only once a UTC day, because the figure is
   whole days and cannot change inside one.

Nothing here reads a person's id or name except where the live-room reader needs one to answer "is
anybody watching", and that answer is a yes or no by the time it leaves the reader.

### 8.6 Settings

Settings → AI → **Alerts**, its own sub-tab: the Insights tab already carries four cards, and ten
watchers would bury them.

- **Discord channel** — where alerts are posted. May differ from any insight channel.
- **Quiet time** — none, or 1 to 48 hours. Default 6.
- **AI sentence** — on or off. Off means the figures go out on their own.
- **What is watched** — one control per watcher: off, low, normal, high. Every watcher is off until
  somebody turns it on, so a deployment that upgrades into this feature gets nothing until it asks.

### 8.7 Storage

- `modbot_alert` — one row per alert.
- `modbot_alert_watch` — one row per watcher: sensitivity, and the window last judged.
- `modbot_alert_settings` — one row: channel, quiet time, AI sentence, when the checks last ran.

Alerts hold no personal data (§8.3), so like insights they are outside retention and purge-user.

### 8.8 Not in this version

- Alerts by email. The spend-limit notices have that plumbing; nobody has asked for it here.
- A watcher for VRChat presence dropping off, which would need the desktop clients to be a reliable
  measure of anything, and they are not — they see what their moderators happen to be looking at.
- Choosing the window length per watcher. An hour and a week cover what was asked for, and a knob
  nobody turns is a knob that goes wrong.
