# Modbot — The VRChat Proxy, and Buckets for What a Moderator Presses

- **Date:** 2026-09-17
- **Status:** Implemented with this document
- **Covers:** why a moderator's ban waited behind the sweeps and what changed (§2); the
  `users.lookup` class (§2.3); `/api/proxy/vrchat/{path}` — who may call it, whose session a
  request goes out on, what is and is not forwarded, how it is paced (§3–§5); the
  `vrchatProxy.enabled` switch and Settings → VRChat Proxy (§6)
- **Implements:** foundation §4.2's reserved room as a bucket (§4.3.5, added with this document);
  the `UseVRChatProxy` permission
- **Related:** foundation §4.1 (the gate), §4.2.5 (the users lane), §4.3.1 (hierarchical budgets,
  cold stop), §4.3.4 (the standing instruction); M4 §12.2 (`groups.moderate`); API keys design §3
  (keys, and how a request is authenticated); MCP server design (a feature behind a switch that
  answers 404 while off)

---

## 1. What this adds

Two things, one small and one new, that share a reason: **calls a person is waiting on must not
queue behind calls a timer started.**

1. The rate limiter's backstop becomes two buckets. What a moderator presses — a kick, a ban, an
   unban — draws from an `interactive` bucket sized to the room foundation §4.2 already reserves
   for it, and never from the `global` bucket background sync keeps empty. A person's read of one
   user gets a class of its own, apart from the profile sync's.
2. A proxy. `GET/POST/PUT/DELETE /api/proxy/vrchat/{path}` forwards a request to
   `https://api.vrchat.cloud/{path}` and answers with whatever VRChat said — as the group's
   service account for a caller with a Modbot key, or as the caller for one with their own VRChat
   cookie. So a script, a tool or a VRChat client library can use the account without holding its
   password, on the same session and the same pacing Modbot uses for itself.

## 2. The contention, and what changed

### 2.1 What was actually shared

`groups.moderate` looked isolated: its own class, its own lane, so a ban never queued behind a
member page in a lane (M4 §12.2). It was not. The class was `CountsAgainstGlobal: true`, so every
ban also took a token from `global` — and `global` is a bucket of burst one at 2 req/s that the
scheduled classes drain at 1.425 req/s. Three things made that a wait:

- The **priority queue is per lane**. A moderator's call preempts background calls *in its own
  lane*. The global bucket is shared across lanes and has no queue at all: a request that finds it
  empty sleeps until the next token and then re-checks.
- The **sleep is a race**. A sweep that woke at the same instant could take the token first, and
  the ban would sleep for the next one. With three sweeps active, a ban could lose several times.
- Nothing **reserved** the room. §4.2 said 0.55 req/s was for interactive work; the arithmetic was
  right, because the scheduled classes' own caps sum to 1.425, but the reservation existed only as
  a subtraction on the settings screen. The bucket the room was supposed to be in was the bucket
  everybody drew from.

`users.read` and `users.profile` had the other half of the problem. Both are exempt from `global`
and have their own lanes, so a moderator-driven read there never waited for the sweeps — but the
one per-person read a person waits on, the account-link check that reads a bio for its code, ran
on `users.profile`, which is the background profile sync's bucket. A cold stop the sync earned
stopped account linking, and a person's lookups spent the sync's allowance.

### 2.2 The backstop is two buckets

A class now names its backstop (`RateLimitClassOptions.Backstop`) instead of saying whether it
counts against the global one:

| Backstop | Cap | Who draws from it |
|---|---|---|
| `global` | 2 req/s (§4.2) | the scheduled classes, worlds and instances, the calendar, `users.groups`, `groups.invites`, the proxy on the service account |
| `interactive` | 2 − 1.425 = **0.575 req/s** | `groups.moderate`, `moderation.write` |
| *none* | — | `users.read`, `users.profile`, `users.lookup`, `users.search`, `auth`, `auth.verify`, the proxy on a caller's own cookie |

`interactive`'s cap is *defined* as the ceiling minus the scheduled sum (`VRChatRateLimits.
InteractiveRoomPerSecond`), and `BudgetCoverageTests` asserts the three add up, so a change to one
without the others fails a test rather than quietly changing the ceiling.

**Does the interactive bucket count against `global`?** No, and this is the decision to record.
Chaining it through `global` would have left the race in place. Sizing the two so they sum to the
ceiling keeps the ceiling's meaning: background sync is capped at 1.425 through `global` (whose
own 2 req/s cap still bounds a burst across lanes), moderators at 0.575 through `interactive`, and
the account-wide total at 2 — but the two never share a token. The `global` bucket keeps its
2 req/s number because that is the number operators configure and the settings screen shows; its
only real consumers are now the scheduled classes, and the room left it displays is the
`interactive` cap.

**What a 429 does**, per §4.3.1's rules with one clarification:

- On a class under `interactive`: the class is cold-stopped, `interactive` is halved as its
  ancestor, and `global` is halved as evidence — the treatment `users.read` already gets. A rate
  limit on the service account is evidence about the service account wherever it lands.
- On a class under `global`: `global` is halved; `interactive` is not touched. A cold members
  bucket must not slow a ban, which is the whole point, and a rate limit on the sweeps' backstop
  says nothing about the moderator's.
- On `proxy.passthrough` — a caller's own account — nothing but that bucket is touched (§4).

`moderation.write` moves with `groups.moderate`: it is unused, but the day it is used it should be
paced with the rest of what a moderator presses. `users.groups` and `groups.invites` stay on
`global`: §4.3.4.1 decided `users.groups` gets no exemption on evidence, onboarding runs before
any sweep exists, and neither has the contention this section is about.

### 2.3 `users.lookup`

One person, read because somebody is waiting for the answer. Today that is the link check
(`VRChatBioCheck`), which moves onto it; anything later that fetches one user by id for a person
pressing a button belongs here too. Its own class and lane, so a cold stop earned by the
background profile sync never stops a person linking their account and a person's lookups never
spend the sync's allowance.

The rate is **1 req/s** — §4.2.5's original figure for the users lane, deliberately under the
3.5 req/s the maintainer measured, because these reads land on the same two endpoints the two sync
classes already run at that rate. Not a new endpoint, so §4.3.4's standing instruction is not
triggered; a third bucket at the full rate on the same endpoint would have been, in effect, raising
it. Exempt from both backstops for the reason its siblings are: the exemption rests on evidence
about the endpoint, not about who asked.

## 3. The proxy: who is calling

`/api/proxy/vrchat/{**path}`, mapped for `GET`, `POST`, `PUT` and `DELETE`, allows anonymous
callers at the pipeline level because one of its credentials is something no authentication scheme
knows. Authentication still runs — a Modbot key in the header or the session cookie arrives as
`http.User` — and the endpoint decides in this order (`VRChatProxyCallers`):

1. **The switch.** `settings.vr_chat_proxy_enabled`, off by default. While off, everything
   answers `404 { "error": "The VRChat proxy is off." }` before any credential is read, for the
   MCP server's reason: off should look like not here.
2. **Something already signed in** — `Authorization: Bearer mbk_…` or the web app's session.
   The account needs its VRChat link (as everywhere) and the **`UseVRChatProxy`** permission
   (bit 27, "Use the VRChat proxy"). The request goes out as the service account.
3. **A key in the header that the key handler refused** is a bad key, not an invitation to try
   the cookie: `401`.
4. **The `auth` cookie.** VRChat's own API reads its session from a cookie named `auth`, and every
   VRChat client library sends whatever it was given as that cookie. A value
   `ApiKeySecrets.LooksLikeKey` recognises is a Modbot key, resolved by `ApiCallers.ForKeyAsync`
   exactly as a header key is — same cap by the account, same last-used stamp, same one-sentence
   refusal — and the request goes out as the service account with the same permission check.
5. **Anything else in the `auth` cookie** is somebody's own VRChat session. The request goes out
   with the caller's `Cookie` header as they sent it and nothing of Modbot's; the cookie is never
   read, stored or logged. No Modbot account is involved, so no permission applies.
6. **Nothing** is `401` with `WWW-Authenticate: Bearer`.

Why the permission is its own bit: a proxied request can reach any endpoint the service account
can, read or write, which is more than any one Modbot permission grants, so no existing permission
implies it. It is not on the built-in roles; Administrator holds it. The playground on the settings
tab uses the signed-in session with the same permission a key needs — there is one rule, not one
for programs and a looser one for people.

## 4. Whose session, and how it is paced

Every forwarded request goes through `IVRChatGate.ForwardAsync`, the gate's one call that is not
an SDK call — and the reason it is on the gate at all. Nothing outside the gate holds the session
cookie, so nothing outside the gate can send it; the request goes out on the same client, through
the same limiter, with the same User-Agent, and a 429 cold stops its bucket like any other.

| | `proxy` — service account | `proxy.passthrough` — caller's own cookie |
|---|---|---|
| Session | the gate's one session; a 401 is renewed once, like any call | none: no jar, no credentials, the caller's cookies only |
| Egress proxy (§2.3.1) | the session client's | the same, on a client of its own — the reason for one is this host's network |
| Cap | **0.3 req/s**, own lane | **0.5 req/s**, own lane |
| Backstop | `global` | none |
| A 429 halves | the class and `global` | the class only (`ServiceAccount: false`) |

**Why 0.3.** The limiter cannot see which VRChat endpoint a forwarded request reaches. A proxied
read of the member list spends the proxy's bucket beside the sweep that already reads that
endpoint, and a 429 it earns is recorded on the proxy while the sweep's bucket learns nothing. The
cap is therefore the bottom of §4.3.4's provisional range, and the docs say plainly that the proxy
is for trying an endpoint and for one-off scripts, not for sweeping. It counts against `global`
because it goes out as the service account, and the account-wide limit the backstop stands for
applies to it like anything else.

**Why pass-through is paced at all.** It is a different VRChat account, so VRChat's per-account
limits are the caller's problem — but the request leaves from this host's address, and Cloudflare
does not know whose cookie it was. A burst from one caller could have the host blocked for the
service account too (§2.3.1 exists because that block is real). So it is paced, on a bucket of its
own, at a guess kept low. It is not counted against `global` and a 429 on it halves nothing else,
because a stranger's rate limit is not evidence about Modbot's account and must not be able to
slow every sweep. A cold stop on it is answered `429` like the other, and the health page shows
it as `proxy.passthrough`.

Pass-through never touches the gate's state. A Modbot with no service account configured still
forwards a caller's own requests; that is what makes it useful to a tool that already has a
session.

## 5. What is and is not forwarded

Headers are **allowed by name, not refused by name** (`VRChatProxyCall`). A denylist of hop-by-hop
headers would still have forwarded whatever a caller's client library adds next year — a tracing
header, a second credential — under Modbot's name and, on the service account, with Modbot's
session. What a caller may say to VRChat is what an API call says.

Forwarded: the method, the path, the query string, the body with its `Content-Type`, and
`Accept`, `Accept-Language`, `If-None-Match`, `If-Modified-Since`. On the caller's own cookie,
`Cookie` too. Set by the gate: `User-Agent` (always Modbot's, naming the operator) and the
developer-contact headers.

Never forwarded, whatever the list says: `Authorization` (on this route it is a Modbot key), and
on the service account `Cookie` (which is where a client library puts the Modbot key it was given
as an `auth` cookie).

Returned: VRChat's status and body as they came, `Content-Type`, `ETag`, `Last-Modified`,
`Cache-Control`, `Retry-After`, `CF-Ray`, and `X-Modbot-Proxy-Account: service | own`. On the
service account, a `Set-Cookie` from VRChat is kept in the gate's jar and never returned — it is
the service account's session. On the caller's own cookie it is theirs and comes back.

The destination is built with `UriBuilder`, so a path beginning `//other.host/` stays a path on
VRChat's host. Bodies are capped at 1 MB in and 4 MB out; VRChat's API takes JSON, not uploads.

Modbot's own answers are distinguishable from VRChat's by the absence of `X-Modbot-Proxy-Account`:
`404` off, `401`/`403` who is calling, `413` too big, `429` Modbot's own cold stop (a client
library backs off on that, which is what it should do), `503` no session, a wait to sign in, or
the network, with the gate's own sentence as `error`.

The HTTP log records a proxied call as `{METHOD} /{path}` on its class, as it records an SDK
method name — the query string is not logged, because a search term is not something to write
down. Cookies and headers are never logged.

## 6. The switch and the screen

`settings.vr_chat_proxy_enabled` (`Settings.VRChatProxyEnabled`), off by default, read on every
request. `GET`/`PUT /api/settings/vrchat-proxy` (`ManageSettings`) answers `{ enabled, baseUrl,
publicAddressSet }`; turning it changes writes a `modbot.settings.change` fact naming the setting,
as the MCP switch does. `baseUrl` is the saved public address — or this request's own origin, for
the MCP server's reason — plus `/api/proxy/vrchat/`.

**Settings → VRChat Proxy**, after the API tab: the switch and the base URL with a copy button; a
playground with a method, a path prefilled `api/1/users/`, a body for `POST` and `PUT`, **Send**,
and the answer's status and pretty-printed body. It calls the proxy with the signed-in session.
Labels only; the reasoning is here and in `docs/content/docs/api/vrchat-proxy.mdx`.

## 7. What is not here

- **No facts for proxied requests.** VRChat's own audit log records what the service account did;
  the HTTP log records that the proxy was used, on which path, and with which class. If attribution
  to a Modbot account is wanted later, a `modbot.proxy.request` fact naming the key is the shape.
- **No per-endpoint pacing of proxied traffic.** The limiter would have to parse VRChat paths to
  know that `/groups/{id}/members` is `groups.members`. The conservative cap and the documented
  warning are the answer for now; the parse is the upgrade if the proxy turns out to be used for
  reads the sweeps already make.
- **No proxy for anything but VRChat.** The route says `vrchat` so a second one can say something
  else.
