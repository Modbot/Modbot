# Modbot Cloud — accounts, the server registry and the term lists

- **Date:** 2026-09-16
- **Status:** Draft, awaiting review
- **Covers:** Cloud accounts, claiming a Modbot server, what a server reports, and the term list catalogue
- **Depends on:** `2026-09-11-central-services-design.md` (§1.1, §2, §4, §5), `2026-09-15-cloud-log-backup-design.md`
- **Related:** foundation §4.2.7 (term lists are data, not policy)

---

## 1. Why this exists

Modbot Cloud was built to hold the companions' event backup. Three more things now live in it,
all moved from `my.modbot.co`:

1. **Accounts.** An email address and a password, verified by mail.
2. **The server registry.** Modbot servers register themselves and report on a schedule, and an
   account can claim the servers it owns.
3. **The term lists.** The curated lists Modbot's AI moderation imports.

The maintainer's words:

> "Move termlists and instance registration into Modbot Cloud, and support downloading termlists
> from cloud and it should report full instance details to modbot cloud like usage analytics, group
> info, group image icon url, group ID. No # of users, No # of group members, No # users in
> instance, basic analytics that regularly report but mostly anonymous except group ID. We need
> group ID and name and info so we can show in my.modbot.co which just proxies to Modbot Cloud to
> get instance registration data."

The reversals this causes in the central services spec are written down there: §1.1 (no central
authentication — narrowed), §2.1.1 (my.modbot.co's database — removed), §4.6 (the registry holds
the group after all), §5.1 (the onboarding toggle — replaced by an environment variable).

---

## 2. Accounts

### 2.1 What an account is

An email address, a password, and a list of Modbot servers the person has claimed. Nothing else. No
display name, no avatar, no organisation.

**An account is not a Modbot sign-in.** It never signs anybody in to a Modbot server, no Modbot
server ever checks one, and a deployment with no account behaves identically in every respect. Staff
accounts live in each deployment and always will (accounts and access design).

### 2.2 Registering

`POST /api/v1/accounts` takes an email address and a password. Cloud stores the address folded to
lower case, hashes the password with ASP.NET Core's `PasswordHasher` — the same hasher a Modbot
server uses for its own staff accounts — and sends a verification mail through **Resend**.

- **Password rules:** at least 12 characters, at most 200. Nothing else. Composition rules make
  people write worse passwords down.
- **Until the address is verified the account cannot sign in.** That is what stops somebody
  registering with an address that is not theirs and then claiming a server with it.
- **Registering with an address that already exists answers exactly as if it were new**, and sends
  nothing. Otherwise the endpoint is a way to ask whether a given person has an account.
- Limited to 5 registrations an hour per IP address.

### 2.3 Verifying, signing in, signing out

| Route | What it does |
|---|---|
| `POST /api/v1/accounts` | Register. Sends the verification mail. |
| `POST /api/v1/accounts/verify` | Takes a token from the mail. Marks the address verified. |
| `POST /api/v1/accounts/resend-verification` | Sends it again. Same silence as registering. |
| `POST /api/v1/accounts/session` | Sign in. Sets the session cookie. |
| `DELETE /api/v1/accounts/session` | Sign out. Deletes the row and the cookie. |
| `GET /api/v1/accounts/me` | The signed-in account: its address, and whether it is verified. |
| `POST /api/v1/accounts/forgot-password` | Sends a reset mail. Silent about whether the address exists. |
| `POST /api/v1/accounts/reset-password` | Takes a token from that mail and a new password. Ends every session. |
| `POST /api/v1/accounts/password` | Change the password, knowing the old one. Ends every other session. |
| `POST /api/v1/accounts/email` | Change the address. Sends a verification mail to the **new** address; the change lands only when that token is used. |

**Tokens.** 32 random bytes as base64url, in the mail. Cloud stores only a SHA-256 of them, with a
purpose and an expiry — 24 hours for verification, 1 hour for a password reset. Using one deletes
it. This is the same shape as the desktop install secrets and the admin session, for the same
reason: reading the table must not let anyone use what is in it.

**Sessions.** The same rules as the `/admin` session already in Cloud: a cookie that is `HttpOnly`,
`Secure`, `SameSite=Strict`, `Path=/`, expiring after 30 days, holding 32 random bytes and an
HMAC-SHA256 of them under a key derived from a server secret. Cloud keeps a SHA-256 of the random
part in `account_session`. Signing out deletes the row.

Account sessions are **not** admin sessions and open nothing under `/api/admin`.

### 2.4 Mail

Resend, over its HTTP API. No SDK package: it is one `POST` to `https://api.resend.com/emails` with
a bearer key and a small JSON body, and a package to do that is a dependency to keep current
forever.

| Variable | Meaning |
|---|---|
| `RESEND_API_KEY` | The key. **Unset means Cloud sends no mail**, so registering, verification and password resets are refused rather than silently swallowed. |
| `MAIL_FROM` | The From address, e.g. `Modbot <noreply@modbot.co>`. Required when the key is set. |
| `CLOUD_PUBLIC_URL` | Where Cloud is reachable, for the links in the mail. Default `https://cloud.modbot.co`. |

The key is read from the environment, is never returned by any endpoint, and is never written to a
log — the only thing logged about a mail is whether it was accepted and, on a failure, the status
code.

---

## 3. The server registry

### 3.1 Registering

A Modbot server calls `POST /api/v1/servers` once. Cloud makes a random `serverId` and a secret,
returns both, and never shows the secret again — only its SHA-256 is kept. The server stores both,
encrypted, in its own settings and sends `Authorization: Bearer <serverId>.<secret>` on every later
call.

**Cloud assigns the id rather than accepting one.** The old registry let a deployment pick its own
`instanceId`, which meant anyone could post a report as any id they could guess or read. There is
nothing to guess now, and a report that does not carry the secret is refused.

Registration is the one unauthenticated write, and it is limited to 10 an hour per IP address.

### 3.2 Reporting

`POST /api/v1/servers/report`, every **6 hours**, with a jittered first report two minutes after
start-up. A failure retries in an hour. Every report is stored as its own row in `server_report`, so
the figures can be charted; the newest values are also kept on the `registered_server` row so a list
is one read.

**What a report carries:**

| Field | Why |
|---|---|
| `publicAddress` | So the selector can offer it, and a dead address can be aged out |
| `version` | Which releases are live |
| `hostPlatform` | The OS and architecture the process runs on |
| `groupId` | Which VRChat group this Modbot moderates |
| `groupName`, `groupDescription` | So a person recognises their own server |
| `groupIconUrl`, `groupBannerUrl` | So `my.modbot.co` can show it |
| `discordConnected` | Whether a Discord bot is configured |
| `termListsImported` | **Which** lists, never their contents |
| `rateLimitColdStops` | The most valuable field: foundation §4.3's limiter is built on estimates about an undocumented system, and a spike across many deployments after a VRChat change is how the project finds out its numbers are wrong |
| `wafBlocks` | The same, for the VRChat WAF |
| `aiModerationEnabled` | Whether AI moderation is on |

**What a report may never carry, and has no field for:**

- the number of users, staff or otherwise;
- the number of group members, exact or bucketed;
- the number of people in a VRChat instance;
- any member identity, any moderation data, any fact, any profile text, any credential, any VRChat
  instance id, any log line.

Apart from the group, a report is anonymous. That is not a promise somebody has to keep on every
future change — it is the shape of `ServerReportRequest`, and the compiler checks it.

### 3.3 Claiming a server

A Cloud account can claim a Modbot server. Only its owner can.

**How ownership is proved: Modbot shows a code, the person pastes it into Cloud.**

1. An owner opens their Modbot's settings and asks for a link code. Modbot makes 8 random
   characters from an unambiguous alphabet, sends Cloud its SHA-256 over the registered server's own
   authenticated channel, and shows the code.
2. The person signs in to Cloud and pastes it. Cloud finds the one server whose stored hash matches
   and whose code has not expired, and attaches it to the account.
3. The code is single-use and lasts **15 minutes**.

**Why this direction and not the other.** The alternative — Cloud issues a code, the operator pastes
it into Modbot — would need Cloud to call the Modbot server back to confirm, and a service that
makes an HTTP request to an address a stranger supplied is a server-side request forgery waiting to
happen. This way Cloud never calls out: the proof arrives on a connection the server itself opened,
authenticated with the secret only that server holds.

**Why a stranger cannot claim someone else's server.** The code is only ever displayed inside a
Modbot that the person is signed in to as an owner, it is 8 characters from a 32-character alphabet
(2⁴⁰ possibilities) with one live code per server, it expires in 15 minutes, it is stored only as a
hash, and claim attempts are limited to 10 an hour per account and per IP address. Guessing a code
is not a route in, and reading Cloud's database is not either.

Unclaiming is `DELETE`, by the account that holds it. Deleting a server from `/admin` unclaims it
too.

### 3.4 Reading the registry

**Never publicly browsable or enumerable.** That rule from central services §4.4 is unchanged: a
list of every Modbot deployment is a map of VRChat moderation infrastructure. Three ways to read it,
all closed to the public:

| Who | How | Sees |
|---|---|---|
| The project | `/admin`, or `Authorization: Bearer <ROOT_API_KEY>` | Every account, every server, every report, the calling addresses |
| A signed-in account | its session cookie | Only the servers that account has claimed |
| `my.modbot.co` and the landing page | `Authorization: Bearer <PROXY_API_KEY>` | The narrow read endpoints under `/api/v1/site`, and nothing else |

`PROXY_API_KEY` is a second key, separate from `ROOT_API_KEY`, because it is handed to another
service and must not also open `/admin`. It is the same value `my.modbot.co` sets as
`MODBOT_CLOUD_API_KEY`.

### 3.5 What `/api/v1/site` serves

Exactly what the page needs, and nothing else:

- `POST /api/v1/site/visits` — record that an instance URL was opened from a visitor's address.
  The address is in the body, because `my.modbot.co` is the caller and its own address is not the
  visitor's.
- `GET /api/v1/site/visits?address=` — the instances seen from one address: 90 days, 50 at most,
  most recent first, each with the group name and icon URL when a registered server reports that
  address.
- `GET /api/v1/site/counts` — how many servers are registered and how many were seen in the last 30
  days, for the landing page. Two integers.

No endpoint under `/api/v1/site` lists servers, and none takes a search term.

> **Added 2026-09-17.** `POST /api/v1/site/servers` — what my.modbot.co learned about one Modbot
> address by asking that address itself on a register visit: group id, name, icon, banner and the
> operator's email address. It is a separate endpoint from a visit because the two facts have
> different rules (a visit is counted once per five minutes; this is simply replaced by the newest
> answer), and it writes to `visited_server`, never to `registered_server`. Where both know
> something the registry wins, field by field, so `GET /api/v1/site/visits` falls back to what a
> visit learned only where a report says nothing. **The operator's email address is returned by
> nothing under `/api/v1/site` and by nothing an account reads — only by `/admin`.** See
> `2026-09-17-register-details-and-subscribers-design.md` §3.

---

## 4. Term lists

The curated lists moved from `src/Modbot.My/termlists` to `src/Modbot.Cloud/termlists`, and the
catalogue and its three routes moved with them, unchanged:

```
GET /termlists/index.json
GET /termlists/_schema.json
GET /termlists/{id}.json
```

**The shape is identical**, so a subscriber pointed at Cloud sees exactly what it saw before. Modbot
now fetches them from `MODBOT_CLOUD_ENDPOINT` instead of `https://my.modbot.co`.

`my.modbot.co` keeps the three routes as **permanent redirects** (`308`) to Cloud. A 308 keeps the
method and the body, and every HTTP client Modbot uses follows one. Anything already pointed at
`my.modbot.co/termlists/…` — an older Modbot, somebody's script — keeps working without a change.
Redirecting rather than proxying keeps one copy of the lists in one place and means a bug in
`my.modbot.co` cannot serve a stale list.

They stay public and need no account: they are data a moderator's software needs in order to
moderate, and putting a sign-up in front of them would make a Modbot that cannot work until
somebody registers.

---

## 5. What Modbot's own side does

- `ServerReportingService` registers and reports on the schedule in §3.2, through the ordinary
  `IHttpClientFactory` client. **Never the VRChat gate and never the egress proxy**: Cloud is not
  VRChat, and a request to it must not spend a VRChat rate-limit budget or leave by an address the
  operator set aside for VRChat.
- Every failure is logged at **debug**, never warning or error. Modbot is self-hosted software that
  happens to talk to an optional service; an operator whose logs fill with errors because somebody
  else's server is down would reasonably conclude their install is broken.
- Nothing waits on it. A Cloud that is slow, unreachable or permanently gone changes nothing about
  how a deployment behaves.
- `MODBOT_CLOUD_DISABLED=1` stops it before the first call. The check is made every cycle, so the
  variable can be set and the process restarted, and nothing is sent in between.
- The **Health** page says when the last report went and whether it was accepted.

---

## 6. The mailing list

Cloud also holds the addresses of people who ticked *Receive emails from Modbot about new features
and updates* while registering an account on a Modbot server. `POST /api/v1/subscribers` carries the
server's own registry credential, the same one its reports carry; unsubscribing is a link that needs
no account. It is written up in `2026-09-17-register-details-and-subscribers-design.md` §4, and is
listed here only so that this spec's picture of what Cloud holds stays complete.

---

## 7. Open questions

1. **Whether an account should be able to see its servers' reports charted on Cloud**, or only on
   the Modbot server itself. The data is there either way.
2. **What to do with a registered server that stops reporting.** Ageing a row out after a year is
   probably right, but deleting somebody's claimed server because their Modbot was off for a month
   would be worse than keeping it.
3. **Whether `PROXY_API_KEY` should be per-caller** rather than one value shared by `my.modbot.co`
   and the landing page. One key is enough while the project runs both.
