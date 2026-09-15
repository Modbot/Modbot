# Modbot — AI Insights

- **Date:** 2026-09-15
- **Status:** Built (first version)
- **Covers:** Settings → AI → Insights, the insights stored from it, and where they are read
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
  output and cached tokens the provider reported, the model asked for, feature `insights`, and the
  person only when somebody pressed Generate now. A scheduled insight has no person.
- A failed call that the provider never counted records nothing.
- Before a call, the limit is read through the same shared place. When a limit is reached, a scheduled
  insight is skipped (its moment still counts as handled, so it is not written late once the limit
  resets) and Generate now answers with the limit's short message.
- The limit settings themselves are built later, on their own screen, for every feature at once.

## 7. Not in this version

- An insight straight after a big event (a room far busier than usual). It needs a rule for "big" that
  is not a guess, and the Rooms kind covers the same ground a day later.
- Discord server figures beyond the message count. The Discord analytics being built now can add
  figures to the Group kind as they land.
- Asking follow-up questions about an insight. That is AI Chat.
