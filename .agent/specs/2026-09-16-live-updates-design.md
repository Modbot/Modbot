# Modbot — Live Updates

- **Date:** 2026-09-16
- **Status:** Implemented with this document
- **Covers:** the live stream the Live page, the notifications and the companion overlay read: what
  it carries, who sees what, the WebSocket, long polling as its backup, and how the two clients fall
  back and come back
- **Narrows:** client protocol §1.2 and §6.1 (the overlay's push channel), M3 §6.0.3 (how the
  overlay's cache is kept warm)
- **Related:** API keys, WebSocket and webhooks design §4–§5 (the general event stream, whose
  reader, wake-up and connection cap this reuses); foundation §4.4 (one clock), §5.3 (facts)

---

## 1. What this adds

The Live page asked the server for everything every five seconds. The alerts card read once when
it opened. The sidebar's review count read when the page changed. The companion long-polled one
endpoint for flagged joins and read the roster every twenty seconds. Each was a poll, and each was
either late or wasteful, usually both.

This adds **one live stream** on the server, carrying typed events, read two ways -- a WebSocket
first, and long polling when the socket cannot be kept -- by two clients: the web app (session)
and the companion (device token). Everything that polled now redraws when something happens, and
the polls that remain are safety nets rather than the mechanism.

## 2. An event is a fact under another name

Every live event comes from a fact, keeps the fact's id, and is read with the same `FactFeed` the
general event WebSocket uses (API keys design §4.2), including its rule for ids that commit out of
order. So a client resumes with a cursor the way any other reader does, and misses nothing the
general stream would have sent it.

The kinds, and the fact each comes from:

| Kind | Fact | Carries |
|---|---|---|
| `person_joined` | `vrchat.instance.join` | the person |
| `flagged_join` | `vrchat.instance.join`, by somebody with prior kicks or bans | the person, `reason` |
| `person_left` | `vrchat.instance.leave` | the person |
| `person_here` | `vrchat.instance.presence` (already there when watching started) | the person |
| `watch_stopped` | `vrchat.instance.log-stopped` | the moderator whose watch ended |
| `room_opened`, `room_closed`, `room_changed` | `vrchat.group.instance.create` / `.close` / `.update` | the room's ids and payload |
| `alert` | `modbot.insight.alert` | the alert's figures, as the fact carries them |
| `review_opened`, `review_closed` | `modbot.review.opened` / `.closed` | the review's payload |
| `fact` | **any other fact** | its type, subject, actor and payload |

A `flagged_join` is sent **instead of** a `person_joined`, not as well: one fact, one event, one id.
A consumer that wants every join handles both kinds; the Live page does, and the overlay raises a
card for the second.

**Every fact is on the stream** (revised the same day, when the scope widened from the Live page
and the notifications to every screen): a fact without a named kind is a `fact` whose `type` says
what it is. The named kinds are the ones a client does something particular with; everything else
a screen matches on `type` -- `vrchat.group.member.*` for the member list, `modbot.review.*` for
the reviews page, and so on (`src/Modbot.Web/src/lib/liveRules.ts`).

The shape, camelCase like the rest of the web API and the companion protocol:

```json
{
  "id": "81234", "cursor": "81234", "kind": "flagged_join",
  "type": "vrchat.instance.join", "typeRaw": null, "category": "moderation", "label": "Joined an instance", "source": "Client",
  "at": "2026-09-16T20:14:07+00:00", "occurredBefore": null, "observedAt": "2026-09-16T20:14:09.2+00:00",
  "subject": { "platform": "VRChat", "id": "usr_…", "kind": "Person" }, "actor": null,
  "instanceId": "39911", "worldId": "wrld_…",
  "person": { "id": "usr_…", "displayName": "…", "trustRank": "KnownUser", "standing": "Flagged", "priorActions": 2, "flags": ["2 prior actions"] },
  "flagged": true, "reason": "2 prior moderation actions", "byThisDevice": false,
  "data": { }
}
```

- `type`, `typeRaw`, `category`, `label`, `source`, `occurredBefore`, `subject` and `actor` are
  the general envelope's (API keys design §4.4), so the audit log can decide from the event alone
  whether a fact belongs on the list as filtered, and a popup whether it is about the person on
  screen.
- `person` is described the way the roster describes people (standing, prior actions, flags,
  trust rank), read once per page rather than once per event. `trustRank` is the stored rank's
  name, or null while the profile's tags are not known.
- `byThisDevice` is for the companion: the server knows which device reported a fact, and the
  overlay does not raise a card for a join its own log just showed it. Always false on the web.
- `data` is the fact's payload, for the kinds that need it (alerts, reviews, rooms). It is left out
  for companion connections, which need the person and the kind and nothing else.
- `cursor` is what a client stores and sends back. It equals `id` today and is documented as
  opaque, as the general stream's is.

## 3. Who sees what

Decided per event, from what the caller holds at that moment, and checked again at every
heartbeat -- a permission removed mid-connection stops the next event.

| Caller | Sees |
|---|---|
| A person with **See live instances** | presence and rooms (the Live page's own permission, M3 §7.4) |
| A person with **See analytics** | alerts (the card's own permission) |
| A person with **Review tickets** | reviews (the sidebar count's) |
| A person with **See the audit log** / **See the operational log** | every fact of that log, by the audit log's own rules (API keys design §4.3) -- presence still needs **See live instances** |
| A companion device | presence in the **one instance it named**, and nothing else |

A named kind is sent when either rule allows it: the screen's permission, or the audit log's for
the fact behind it. A person with none of these is refused a ticket (403) and the poll (403). A device that has
not named an instance is sent nothing, for the reason the alert hub gave (`DeviceLocations`): the
failure worth designing against is context reaching somewhere it was not needed.

Naming an instance -- on connect, on `subscribe`, on a poll -- is the same signal the roster read
gives `DeviceLocations`: a by-product of a request the device makes anyway, never a report made for
its own sake. The old flagged-join long poll still works for clients built before this; the API
version is unchanged because everything here is an addition.

## 4. The web's two doors

| | Address | Authentication |
|---|---|---|
| Ticket | `POST /api/live/tickets` | session or key |
| WebSocket | `GET /api/live/ws?ticket=&after=` | a ticket, or a key in the header |
| Long polling | `GET /api/live/poll?after=&wait=&limit=` | session or key |

The socket takes a ticket and **never a cookie on its own**, for the reason the event socket does
not (API keys design §5.1): browsers send cookies with a WebSocket handshake from any site. The
poll takes the session like every other GET the web app makes -- a cross-site page cannot read the
answer to a credentialed fetch -- which is the one place this narrows the event stream's rule
("a key in the header, and nothing else" for its poll, because a browser had the socket). Here the
browser needs the poll precisely when it cannot have the socket.

The socket is subscribed on connect: the caller's scope decides what it is sent, and the address
carries the cursor. `hello` carries the cursor reading starts after; `event`, `heartbeat`,
`notice` (`history_trimmed`), `error` and `pong` are as on the event socket, and so are the close
codes. A client may send `subscribe` with a `cursor` to move, or -- a companion -- an `instanceId`
to follow the moderator into another room without reconnecting.

The poll answers `{ events, cursor, more, notice }` exactly as the event poll does (§5.6 there),
and shares the same wait and limit bounds, the same wake-up (`FactSignal`) and the same connection
cap: a poll and a socket from the same caller share that caller's five places.

An unexpected failure inside a socket session closes it with `1011` and the exception's name,
rather than dropping it: a client that sees a code reconnects with its cursor, where one whose
connection simply vanishes sits waiting.

## 5. The companion's two doors

| | Address |
|---|---|
| WebSocket | `GET /api/v{n}/companion/ws?instanceId=&after=` |
| Long polling | `GET /api/v{n}/companion/poll?instanceId=&after=&wait=` |

Device token in the header, resolved by the same `DeviceAuthenticator` every other companion
endpoint uses; the companion sets headers on its WebSocket, so there are no tickets. Access is
checked again at each heartbeat by hashing the same token: a revoked device no longer resolves,
and the socket closes with `4003`.

One implementation serves both sets of doors (`LiveStreams`): the reader, the gap rule, the
wake-up, the heartbeat and the cap are the same code, so the two cannot drift.

## 6. Falling back, and coming back

Both clients follow the same rule, in the same words, with the same numbers:

1. **The socket first.** Every event and heartbeat carries a cursor; a reconnect by either route
   asks for events after it, so a drop costs delay and never an event. The first connection has no
   cursor and starts from now: the page's or the roster's own read covers what came before.
2. **Back off between attempts**: doubling from one second up to thirty, with jitter.
3. **Three failed connects or drops within two minutes means polling** for five minutes, with a
   wait of twenty-five seconds (twenty on the companion, under its HTTP client's thirty-second
   timeout). A quiet poll is the ordinary answer and backs nothing off; a failed one backs off as
   a failed connect does.
4. **Then the socket again**, from where polling got to. A proxy or captive portal that will not
   carry WebSockets therefore costs some latency, never the updates; one that later starts carrying
   them is noticed within minutes.
5. **A refusal that trying again cannot fix stops the stream**: `4003`, a 401 or 403 on the ticket
   or the poll, a rejected device token. The page has to be reloaded; the companion's pairing has
   to be redone, and says so.

The state is visible in one word -- **Off**, **Connecting**, **Live**, **Polling**, **Stopped** --
on the Live page and on the companion's Servers card. Labels only (CLAUDE.md); the rules are here.

### 6.1 What the web does with it

One stream per page, however many components read it (`useLiveStream`), running only while
something is subscribed and the tab is on screen: a tab left open in the background holds nothing
open, and comes back with its cursor when looked at.

Every screen reads its own data again rather than applying the event by hand: the server has
already written the change, so reading it back is the one way the screen and the server cannot
disagree. `useLiveVersion(matches)` is the lever -- a number that goes up, settled over 400 ms so a
sweep's forty facts are one read, when an event the screen cares about arrives -- and a screen
puts it in its load's dependencies (or `useLoad`'s `version`). Only the screen on view asks: the
hook lives in the component. The rules a screen asks are pure (`lib/liveRules.ts`) and tested.

| Screen | Reads again on |
|---|---|
| Live | presence and room events (settled over 300 ms); and every thirty seconds on its own, because head counts come from the syncs. Was every five seconds. |
| Audit log | any event that passes the filters in the bar (`auditMatches`, the server's rules as far as an event can tell). At the top of the list the first page is read again and the rows above the old top are put in; scrolled down, a **N new** button counts them and inserts on press, so rows never move under the reader. |
| Members, Discord members | `vrchat.group.member.*`, `.role.*`, `vrchat.user.*` (a trust rank arrives as a profile change); `discord.member.*`, `.role.*`, `.link.*`. The counts come from the same read. |
| Bans | `vrchat.group.member.ban` / `.unban`, the ban sweep. |
| Reviews; the count beside Reviews | `modbot.review.*`. Was on page change. |
| Flags | `modbot.ai-moderation.*`. |
| Case files | `modbot.report.*`, `modbot.evidence.*`. |
| Repeat offenders | bans, kicks, removals. |
| Calendar | `modbot.calendar.*`, `vrchat.group.calendar-event.*`. Was every twenty seconds. |
| Alerts card (My Group, Health) | `alert`. Was once, when opened. |
| Person popup | any event with the person as subject or actor, on their platform: every tab, the profile card, the membership card and the case files remount. |
| World popup, instance popup | any event in that world / that room (by VRChat's number, once the room has loaded). |

### 6.2 What the companion does with it

`LiveLink`, one per paired server, driven by the overlay's ticks exactly as the roster read is:
never awaited across the network, at most one request in flight, harvested when it finishes. A
join or a leave for the current instance makes the roster read due at once (it was every twenty
seconds, and still is as a floor); a `flagged_join` that this device did not report itself becomes
the card, through the same dwell, cooldown and same-room rules as before.

The link is open only while the moderator is in one of that server's group instances -- the same
boundary the roster read keeps -- and closed the moment they leave, so no server is contacted from
anywhere else.

## 7. Why a WebSocket now, when the protocol said not

Client protocol §1.2 chose batched HTTP for **ingest** and a long poll for the one push the overlay
needs, on three grounds: bursty low-volume traffic, offline replay being the same path as sending,
and proxies that break WebSockets. The first two are about ingest and are untouched -- observations
still go by batched POST. The third is answered by the fallback rather than by avoiding the socket:
the proxy case degrades to the same events by polling, and the client comes back to the socket on a
schedule rather than giving up on it. What the socket buys is latency on the one channel where it
matters -- the room's roster changing under a moderator's eyes -- and one connection where there
were a long poll and a twenty-second read.

Plain ASP.NET Core WebSockets rather than SignalR, for the reason the event socket chose them: the
protocol is a dozen JSON messages, both clients already speak it (the companion with
`ClientWebSocket`, the browser with `WebSocket`), and SignalR would add a client library to each,
its own reconnect and its own message framing on top of one that exists.

## 8. What polls remain, and why

| Where | Was | Now |
|---|---|---|
| Live page | every 5 s | on events, and every 30 s (head counts come from syncs) |
| Audit log, members, Discord members, bans, reviews, flags, case files, repeat offenders, the popups | once, or on the page's own actions | on the events that change them (§6.1) |
| Calendar | every 20 s | on calendar events |
| Alerts card | once | on `alert` |
| Review count | on page change | on review events |
| Companion flagged joins | a 30 s long poll, repeated | the stream |
| Companion roster | every 20 s | on presence events, and every 20 s |
| Health page (20 s, 3 s while a demo resets), status rows (30 s), gate health (30 s / 15 s), demo marker (2 s while busy), the profile card's wait for a requested refresh | timers | unchanged: none of these are facts -- they are the syncs' and the gate's own numbers, which the stream does not carry |

## 9. Testing

The server: a person with each permission is sent exactly their kinds; a flagged join arrives as
one with its reason; a cookie alone does not open the socket; a cursor replays what was missed in
order; the poll answers at once, waits and is woken, answers empty at the timeout, refuses nobody
signed out; a device is sent presence in its instance only and told which joins it reported; a
subscribe moves the device's instance and records its location; the old alert poll still answers.

The companion: the socket comes first and remembers the cursor; a drop reconnects from the cursor
after a backoff; three failures mean polling then the socket again; without a socket the link polls
for good; a failed poll backs off and a quiet one does not; a rejected token stops the link; a room
change is a subscribe; leaving closes the socket. The overlay driver: a join makes the roster due, a
flagged join this device reported is not a card, the Servers card gets one word per server.

The web: the same rules over a fake socket, a fake poll and a clock the test moves.
