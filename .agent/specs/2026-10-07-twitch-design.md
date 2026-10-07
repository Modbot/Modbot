# Modbot — Twitch

- **Date:** 2026-10-07
- **Status:** Steps 1, 2 and 3 built with this document: the settings, Check, the live poll, the
  `twitch_stream` table, the Live and Now cards, the Integrations row, Health, the event link, and the
  "We're live on Twitch" post. **Not built:** the Twitch schedule (step 4, the owner holds it: the
  channel is not an Affiliate) and EventSub webhooks (step 5).
- **Covers:** hearing that the channel is live by polling Get Streams with an app token, showing it,
  linking each stream to the calendar event on at the time, and posting "We're live" through the
  shared posts system
- **Depends on:** the posts design (2026-10-03: the tables, the senders, the late rule, Pause all
  posting), the calendar design, the live updates design (§3: `LiveScope`), the foundation (§4.3 rate
  limits, §4.4 the clock)
- **Source:** the read-only design report `2026-10-07-twitch-design.md` (the owner's reports folder),
  which carries the facts table and sources this spec relies on. Where this document and the report
  differ, this one is what was built, and §9 says where.

## 1. Decisions (owner, 2026-10-07)

| # | Decision |
|---|---|
| 1 | Hear that the channel is live by **polling Get Streams every minute with an app token** (client credentials). No public address, no sign-in. Webhooks are an optional later speed-up. |
| 2 | Only `type = live` streams post, after **at least 3 minutes** live, with **at most one post per 6 hours**; both editable. One post per Twitch stream id, kept by a partial unique index on the new `post.external_key`. |
| 3 | A "live" post more than **15 minutes late** is Failed ("Not sent on time."), not sent. |
| 4 | Destinations: **none ticked to start**. The operator ticks Discord, VRChat and Bluesky, with their options, in Settings → Twitch. Sending is the existing senders (claim, read-back, Try again, Pause all posting, the hourly cap). **No new sending code.** |

## 2. What Twitch says (checked on dev.twitch.tv, 2026-10-07)

| Fact | Status |
|---|---|
| `POST https://id.twitch.tv/oauth2/token` with `client_id`, `client_secret`, `grant_type=client_credentials` gives `{access_token, expires_in, token_type}`; `expires_in` is about 58 days in Twitch's example; no refresh token. | confirmed |
| `GET https://api.twitch.tv/helix/streams` takes `user_id`, `user_login`, `type`, `first`, `after`, `language`; an app or a user token is accepted; the list is empty when nobody is streaming. Fields: `id, user_id, user_login, user_name, game_id, game_name, type, title, viewer_count, started_at, language, thumbnail_url, tags, is_mature`. | confirmed |
| `GET /helix/users?login=` (up to 100) takes an app token; ids and logins that are not found are ignored (an empty `data`). | confirmed |
| Every call carries `Authorization: Bearer <token>` and `Client-Id`. | confirmed (the docs' examples) |
| A 429 comes with `Ratelimit-Reset`, "a Unix epoch timestamp that identifies when your bucket is reset to full". Limits are a token bucket, `Ratelimit-Limit` / `-Remaining` / `-Reset`. | confirmed |
| Registering an app needs 2FA on the Twitch account and gives a Client ID and, with **New Secret**, a secret shown once. The OAuth Redirect URL is a required field. | confirmed |
| The values `type` takes besides `live`, `first`'s maximum for Get Streams, the bucket size for an app token, the error body of the token endpoint, what Get Users does for a login with characters Twitch refuses, the Developer Services Agreement's terms. | **unconfirmed**. Code treats any `type` but `live` as not live; reads Twitch's `message` for an error body; takes a 400 for a bad login as a Check finding in Twitch's words. |

## 3. Tables

### 3.1 `settings` (new columns, all Twitch)

| Column | Notes |
|---|---|
| `twitch_client_id` | Not secret. |
| `twitch_client_secret_encrypted` | `ISecretProtector`. Never returned. |
| `twitch_channel_login` | Lower case, read from what was typed (a login, `@login` or a `twitch.tv` address). |
| `twitch_channel_id`, `twitch_channel_name`, `twitch_checked_at`, `twitch_problem` | What Check found. Cleared when the client id, the secret or the channel changes. |
| `twitch_stopped_until` | The rate stop. Kept when the credentials are forgotten. |
| `twitch_live_on` | The poll's switch. On only over a Check that passed. |
| `twitch_polled_at`, `twitch_poll_problem` | The last answer and the last poll's problem, for Settings and Health. |
| `twitch_post_after_minutes` (3), `twitch_post_every_hours` (6) | Decision 2, editable. 1 to 60 and 1 to 168. |
| `twitch_post_title`, `twitch_post_text` | The template. Null is the built-in one: "We're live on Twitch" and `{title}` then `{link}`. |
| `twitch_post_places` jsonb (`{}`) | The ticked sites and their choices (`TwitchPostPlaces`): `discord {channelId, roleId, publish}`, `vrChat {visibility, roleIds, notify}`, `bluesky`. A site that is absent is not ticked. |

### 3.2 `twitch_stream`

One row per Twitch stream id, written when the poll first sees it live.

| Column | Notes |
|---|---|
| `id` text (PK) | Twitch's stream id. Opaque. |
| `started_at`, `first_seen_at`, `last_seen_at`, `ended_at` | `started_at` is Twitch's; the others come from `IModbotClock`. |
| `type` | Twitch's word. Anything but `live` never posts. |
| `title`, `category`, `viewers`, `peak_viewers` | As Twitch said, cut to 200 characters. |
| `event_id`, `event_set_by_staff` | The linked calendar event (SET NULL on delete). A person's choice, clearing included, is never changed by the poll. |
| `post_decided_at`, `post_id` | Decided once (§5). |
| `update_sent_at` | When a changed viewer count last went out as a fact. |

### 3.3 `post`

`external_key` (varchar 64, null) and the partial unique index `ux_post_kind_external_key` on `(kind,
external_key) WHERE external_key IS NOT NULL`. New kind `twitch_live` (`PostKinds.TwitchLive`).
The post carries no `event_id`, so the calendar's `(event_id, kind)` indexes never meet it.

## 4. Asking Twitch

- **`TwitchClient`** (Core, a singleton) on the named client `twitch`: no redirects, no proxy, no
  cookies, 20 s, 1 MB answers. Twitch's two hosts are constants, so `PictureLinks.GuardedHandler` is not
  needed (no address from a setting is fetched) and `PublicAddresses.IsPublic` is not used. Three
  calls: the token, Get Users, Get Streams. Nothing retries.
- **`TwitchSignIn`** keeps the app token in memory, renews it in its last hour, drops one Twitch
  refused (401) and asks again once, and makes at most one token request a minute per client id and
  secret (a refusal is remembered for that minute so a wrong secret is not a loop). The kept record
  holds a hash of the secret, never the secret.
- **A 429 is never retried.** `TwitchErrors.StopUntil` takes `Ratelimit-Reset`, held to between 1 minute
  and 24 hours from `IModbotClock`'s now; no reset means 15 minutes. It is written to
  `twitch_stopped_until`, and no call of any kind, Check included, goes before it.

## 5. The poll (`TwitchLivePass`, every minute)

`TwitchLiveService` runs the pass in a scope once a minute. A pass with Twitch not set up, or Live off,
reads the settings row and nothing more.

1. Ask Get Streams for the channel id. A failure writes `twitch_poll_problem` (and the stop, for a 429)
   and ends the pass. A success writes `twitch_polled_at` and clears the problem.
2. **Update the streams** (one transaction, facts included): a stream that is no longer the live one gets
   `ended_at` and a `modbot.twitch.offline` fact; a stream seen for the first time gets a row, its
   event link (§6) and `modbot.twitch.online`; one seen again updates its row (a stream that drops out and
   comes back under its Twitch id is one row), and writes `modbot.twitch.update` when its title or category
   changed, an event was linked, or the viewer count moved and five minutes have passed since the last
   update.
3. **Decide each stream's post once** (`TwitchRules.Decide`): not `live` → no post; first seen more than
   15 minutes after it started → no post; not yet live for the minimum → wait; the last "live" post
   less than the cool-down ago → no post; no site ticked → no post; otherwise make it. A stream that
   ended undecided is left open for an hour (it may come back under the same id) and then settled with no
   post.
4. **Make the post** (`TwitchPosts`): the template filled with the stream's title, category and the
   channel's link; checked and copied onto a `Post` by the Marketing tab's own code
   (`PostRequests.CheckAsync` and `Apply`), `kind = twitch_live`, `external_key = <stream id>`, status
   scheduled, `send_at` now, no writer. A post that already exists for the stream id is found, not
   made again; the unique index is the database's own guard. The fact `modbot.post.create` is written
   with no actor.

The senders do the rest. **Late** is `PostRules.LateLimitFor(post)`: 15 minutes for `twitch_live`, an
hour for any other, in `IsLate` and in the Bluesky sender's read-back.

## 6. Linking a stream to an event (step 3)

`TwitchRules.ChooseEvent`: the events that are scheduled, open or finished (not draft, cancelled or
deleted) with a date running from an hour before its start to its end at the moment the stream
started, by `CalendarRepeat.Between` (a repeat by its rule; a date moved on its own where it was
moved to; a cancelled date not at all). **Exactly one** is linked. None or several leave it empty:
Modbot does not guess between overlapping events. While a live stream has no event and nobody has chosen
(`event_set_by_staff`), the choice is made again each pass, so an event made after the stream began is
found. `PUT /api/twitch/streams/{id}/event` (Manage calendar) links or clears, sets `event_set_by_staff`
and writes `modbot.twitch.link`. The calendar's event read carries `twitchStreams`; the event shows
"Streamed on Twitch" with Open, and with Manage calendar, Remove and "Link a Twitch stream".

## 7. API

| Route | Needs | |
|---|---|---|
| `GET/PUT/DELETE /api/settings/twitch`, `POST …/check` | Manage settings | Audited as `twitch`, the secret only as changed. A body that is wrong answers 400 with `problems`, all at once. Each site ticked is checked the way the Marketing tab checks a post. |
| `GET /api/twitch/live` | See live instances | The card: `on`, the channel name, the stream or null. |
| `GET /api/twitch/streams` | Manage calendar | The 20 most recent streams, for the link. |
| `PUT /api/twitch/streams/{id}/event` | Manage calendar | |
| `GET /api/twitch/health` | See the operational log | The Health card. |

## 8. The web

- **Settings → Twitch** (Connections, Manage settings): cards App (Client id, Client secret, Channel,
  Save, Check, Forget), Live (once Check passed), Live post (Title, Text, buttons Stream title / Category /
  Link, Minutes live, Hours between posts, and Discord / VRChat / Bluesky with their choices). No
  explanatory text anywhere; the how-to is the docs page.
- **Live on Twitch card** on Live and Now, for See live instances, only while the channel is live.
  It redraws on `modbot.twitch.*` facts (`LiveScope.TwitchTypes` sends them to See live instances alone).
- **Integrations**: a Twitch row, Needs setup / Failed / Set up / Live.
- **Health**: a Twitch card for the Check problem, the poll's problem, a limit with its end, and five
  minutes of silence; hidden when all is well.

## 9. Facts

Subject: the Twitch stream id (Modbot platform). Operational category.

| Fact | Written by | Data |
|---|---|---|
| `modbot.twitch.online` | the poll | title, category, `startedAt`, viewers, `eventId` |
| `modbot.twitch.update` | the poll | title, category, viewers, `eventId` |
| `modbot.twitch.offline` | the poll | title, category, `startedAt`, `peakViewers` |
| `modbot.twitch.link` | a person | title, `eventId`, `wasEventId` |

## 10. What leaves the server

| To | What | Seen by |
|---|---|---|
| `id.twitch.tv` | the client id and secret | Twitch |
| `api.twitch.tv` | the app token, the client id and the channel's name (Check) or id (every minute) | Twitch |
| the ticked sites | the live post's title and text | the readers of that Discord channel, VRChat group, or the public on Bluesky |

Never sent: member names or ids, head counts, moderation data. What Twitch sends back (a title, a
category, a viewer count) is Twitch's public data and goes to staff screens, the audit log, and the
post template. Operator switches: Live, each site's tick, Pause all posting, Forget.
Written down for the user in `docs/content/docs/privacy.mdx` and `integrations/twitch.mdx`.

## 11. Deviations from the design report

1. **The viewer count.** The report says a fact on the live stream updates the card. A fact a minute
   would fill the log, so a changed title, category or event goes out at once and a changed viewer
   count at most every five minutes.
2. **First seen late.** A stream the poll first sees more than 15 minutes after it started (the poll
   was switched on mid-stream, or Twitch was out) does not post: the report's rule for a late post
   (decision 3) is applied to when the poll found it, so enabling Live in the middle of a stream does
   not announce an old one.
3. **A stream that ends early** is settled with no post only an hour later, so a dropout under the same
   stream id is not mistaken for a test stream.
4. **The event is looked for again** while the stream is live and unlinked and nobody has chosen, so
   an event made after the stream began is found.
5. **Integrations** says Set up and Live (the report's words) beside the other cards' Needs setup and Failed.

## 12. Not built, and not checked

- The Twitch schedule (report step 3 A, here step 4) and the webhooks (step 5). The data and settings
  here do not block either.
- Nothing was tried against the real Twitch: only against a scripted one (`FakeTwitch`).
- Twitch's Developer Services Agreement; the values of `type`; Get Streams' `first` maximum; the app
  token's bucket size; what Get Users does with a login of characters Twitch refuses (§2).
- `PRIVACY_POLICY.md` has a table of services Modbot talks to that lists Google Calendar but not
  Bluesky or Twitch. It is the developer's file and was left alone.

## 13. Tests

Written, not run (the testing pass runs them). Core: `TwitchRulesTests` (channel text, template, the
post decision, the event choice, places, the late limit), `TwitchErrorsTests` (sorting, the stop).
Api: `TwitchSettingsTests` (secret, Check, 429 never repeated, audit, places checked),
`TwitchLivePassTests` (rows, facts, one post per stream across restarts and dropouts, the cool-down,
minimum time, types, nothing ticked, the unique index, the event link, a 429), `TwitchEndpointsTests`
(the card, linking, Health, who is sent the updates). `FakeTwitch` in `Modbot.TestSupport` is the
scripted Twitch. Web: `twitch.test.ts` and the Twitch cases in `integrations.test.ts`.
