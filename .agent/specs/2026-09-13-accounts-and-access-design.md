# Modbot — Accounts and Access

- **Date:** 2026-09-13
- **Status:** Draft, awaiting review
- **Covers:** staff accounts beyond the first administrator — roles, permissions, invite links,
  password resets, sessions, and the screens that manage them
- **Depends on:** M0 (fact log, `ModbotUser`, cookie sessions, `IModbotClock`)
- **Implements:** foundation §7.2, §7.3; extends §5.9.2's "Auth" row
- **Related:** foundation §5.8 (accountability), §5.9.1 (attribution must survive account changes),
  §6.3, `.agent/docs/security.md`

---

## 1. What this adds

Today a deployment has exactly one account: the administrator the wizard created. Nobody else can
sign in, so every moderation action Modbot ever records is attributed to that one person, which
defeats §5.8 before it starts. This design lets the administrator bring the rest of the team in
and give each person only what they need.

The pieces: **roles** (a named set of permissions), **user management** (create, invite, disable,
change roles, reset a password), **sessions that actually end** when an account is disabled or its
password reset, **a fact for every account event**, and the web pages for all of it.

## 2. What is already settled, and what this narrows

| Foundation decision | Here |
|---|---|
| §7.2: cookie sessions, `PasswordHasher` standalone, no Identity | Unchanged. |
| §7.3: a permission bitfield, enforced by `RequiresFlag` | Unchanged as the primitive. Roles are a layer over it (§3). |
| §6.3: `ModbotUser` carries a permission bitfield | **Narrowed.** The bitfield is now computed from roles and no longer stored on the user (§3.3). |
| `ModbotAuth`: permissions travel in the cookie, so a change takes effect at next sign-in | **Reversed.** Every request checks the account once (§5). The trade was made for a dozen staff who change permissions a few times a year; disabling someone who is mid-incident is exactly the case where "next sign-in" is wrong, and the check is one primary-key read. |
| §5.9.2: auth events are Modbot-side audit entries, moderation retention | Unchanged, and now actually written — the `Login`/`LoginFailed`/`PasswordChanged` constants existed but nothing produced them. |
| §7.2: optional Discord OAuth, "require Discord login" setting | **Deferred.** A typed-in Discord user id ships, used only to deliver reset links (§4.2). |
| §7.1: five wizard steps | **Extended.** A sixth step, linking the administrator's own VRChat account, sits after the connection check (§4.3). |
| §6.3: `ApiKey` | Out of scope here. |

## 3. Roles

### 3.1 Shape

A role is a name, a one-line description and a `ModbotPermissions` value, stored in `modbot_role`.
A user holds one or more roles through `modbot_user_role`. **A user's permissions are the union of
their roles' permissions.** There is no per-user override: the foundation spec never asked for one,
and an override is the thing that makes "why can Alice do that?" unanswerable from the roles page.

### 3.2 Built-in roles

Seeded by the migration with fixed ids, so code and database agree on which row is which:

| Role | Permissions | Can be edited | Can be deleted |
|---|---|---|---|
| **Administrator** | `Administrator` (everything, including permissions that do not exist yet) | No — the one role that must always mean "everything" | No |
| **Moderator** | view members, profiles, analytics, audit log; kick, ban, unban, warn; view and upload evidence | description and permissions | No |
| **Viewer** | view members, profiles, analytics, audit log | description and permissions | No |

Custom roles can be created, renamed, edited and deleted by anyone holding the new
**`ManageRoles`** permission (`1L << 19`; bit 18 is taken by the user-profile work). Deleting a role that people still hold is refused —
move them first — because silently stripping permissions from three accounts is not what
anybody pressing Delete on a role expects.

### 3.3 Where the old bitfield goes

`modbot_user.permissions` is carried into roles by the migration and then dropped. Every account
holding the `Administrator` bit gets the Administrator role. An account with any other non-zero
value gets a custom role named `Carried over: <username>` holding exactly that value, so nothing
anyone was allowed to do changes. In practice only first administrators exist today; the second
rule is there so the migration is correct rather than probably correct.

### 3.4 The last administrator

Disabling an account, or changing its roles, is refused when it would leave no enabled account
holding the `Administrator` permission. A deployment with no administrator has no way back except
editing the database, and the guard is cheaper than the support conversation.

## 4. Users

All under `ManageUsers`. Accounts are **never deleted** — facts reference the actor id (§5.9.1) —
only disabled and re-enabled.

| Operation | Endpoint |
|---|---|
| List users with roles, permissions, disabled state | `GET /api/users` |
| Create with a temporary password | `POST /api/users` |
| Create an invite link | `POST /api/invites` |
| List / revoke pending invites | `GET /api/invites`, `DELETE /api/invites/{id}` |
| Change roles | `PUT /api/users/{id}/roles` |
| Disable / re-enable | `POST /api/users/{id}/disable`, `POST /api/users/{id}/enable` |
| Set the Discord user id | `PUT /api/users/{id}/discord` |
| Create a password reset link | `POST /api/users/{id}/reset-link` |

And for the signed-in person, under any authenticated session:

| Operation | Endpoint |
|---|---|
| Change own password | `PUT /api/auth/password` (requires the current password) |
| Change own username | `PUT /api/auth/username` (requires the current password; same uniqueness rule) |
| Set own email and Discord user id | `PUT /api/auth/contact` |
| Sign out everywhere | `POST /api/auth/sign-out-everywhere` |
| Link a VRChat account | `GET /api/auth/vrchat-link`, `POST …/start`, `POST …/check` (§4.3) |

A username change is recorded with the old and new names, and the session cookie is re-issued so
the name in it is right. Email and Discord id exist so a reset link can reach the person (§4.2);
they are contact details, not identity, and nothing is ever sent to them without that person
asking.

### 4.2 Forgot password

`POST /api/auth/forgot-password` with a username. Modbot creates a reset link (§4.1) and sends it
**by email if SMTP is configured and the account has an email address; otherwise by Discord
direct message if a bot token is configured and the account has a Discord user id.** Email wins
when both would work. If the deployment has neither channel configured, the sign-in page says so
plainly (from `GET /api/auth/forgot-password`, which reports only what the deployment can do) and
an administrator-issued reset link remains the way.

**The response never says whether the username exists.** It is the same sentence for a real
account, an unknown one, a disabled one, and one with no way to be reached. One self-requested
link per account per ten minutes; repeated requests from one address slow down the way failed
sign-ins do (§7).

The sentence changes with the deployment and never with the account: while account email would
be queued under the daily email limit (§4.4), it adds *"Email is running behind, so it may be
late."* That is decided from the queue alone, before the username is looked up, so the two
answers are still identical for a real and an unknown username.

Two small abstractions carry this: `IEmailSender`, with an SMTP implementation reading the
settings row (host, port, username, encrypted password per §8.3), and `IDiscordMessenger`, with
an implementation in `Modbot.Discord` that calls Discord's REST API with the stored bot token —
open a DM channel, post one message. **There was no Discord code to build on**: the project was
an empty shell holding a token, so two HTTP calls with `HttpClient` are the whole of it, and the
gateway bot of §9 can replace the implementation behind the same interface later. Settings gains
a *Send a test email* button so a wrong SMTP setting is discovered on the settings page, not by a
locked-out moderator.

### 4.3 Linking a VRChat account — required

**Every account must link the VRChat account of the person behind it before it can do anything
else**, the first administrator included. Attribution (§5.8, §5.9.1) is worth little if "Alice
in Modbot" cannot be connected to a VRChat user; and the link is what lets a later milestone
show a moderator's own presence and actions beside their Modbot record.

The flow, exactly:

1. Modbot shows a button that opens `https://vrchat.com/home/user/me` in a new tab.
2. The person copies their user id or the page's URL and pastes it in. Either is accepted: a URL
   is split by `/` and the last non-empty segment taken. **The id is never validated** (§3.1.1).
3. Modbot shows a short code — `modbot-7F3K9Q` — and asks them to put it in their VRChat bio,
   then press **Check**.
4. Check fetches that user through `IVRChatGate` on the existing `users.read` class (budgeted at
   §4.2.5's 1 req/s; its rate-limit question was asked and answered when the class was added),
   interactive priority, `GetUserWithHttpInfoAsync`. If the bio contains the code the link is
   confirmed: VRChat user id and display name at link time are stored on the account, a fact is
   recorded, and the page says the code can come out of the bio now. A 429 is a cold stop and the
   page says to try later; nothing retries.
5. Codes last thirty minutes and are good once. **Check is limited** to six presses per code and
   one every ten seconds per account, so the button cannot be used to hammer the endpoint.

One VRChat account links to one Modbot account. Starting a new link replaces a pending one; a
linked account may re-link, which replaces the old link and records a fact.

**What an unlinked account can do:** sign out, read `/api/auth/me`, and finish the link. Nothing
else — every other endpoint's default authorisation policy requires the link, and the session
check keeps a `modbot:vrchat_linked` claim current so the policy is a claim comparison. The SPA
sends an unlinked person to the link page before it shows anything, and the policy is the
backstop.

**Onboarding order** becomes: administrator → VRChat account → connection check → **link your
VRChat account** → group → optional. The link step can only run once the gate is usable, which is
why it sits after VRChat verification; the status endpoint reports it as the next step while the
signed-in account is unlinked, and the wizard steps after it require the link.

The link fields live on `modbot_user`. A separate `vrchat_user` record table is being built in
another workstream; these two columns are the join key for it, not a second copy of it.

### 4.1 Invite links and reset links

Both are **one-time links**, stored in one table, `modbot_one_time_link`, with a `kind`. Modbot
sends no email (§7.4's SMTP is for notifications, and many groups never configure it); the
administrator copies the link and hands it over on Discord.

- The token is 32 random bytes, base64url. Only its SHA-256 is stored, so a database read does not
  yield working links.
- **Invite links expire after 72 hours**; reset links after 24. A link is refused once used, once
  expired, once revoked, and — for invites — if the person who created it has since been disabled.
  An invite is that person's standing offer, and a disabled account cannot make offers.
- An invite carries the role ids the new account will hold. The invitee picks their own username
  and password at `/join/<token>`, and is signed in on the spot.
- A reset link is bound to one account. Using it at `/reset/<token>` sets the new password and
  **ends every session that account had** (§5). Creating a new reset link removes that account's
  earlier unused ones.

**Invite links are the recommended way to add people**, and the docs say so: an account created
with a temporary password is, until the password is changed, an account two people can act as,
which is exactly what §5.9.1's attribution is supposed to rule out. Forcing a change on first login
is deferred (§9); the temporary-password path exists for the group whose new moderator is standing
next to the administrator.

### 4.4 Daily email limit and the email queue

*Added 2026-09-15.* Relays that community groups use cap how much they send a day, and a relay
that hits its cap drops mail or suspends the account -- the one moment that hurts most is a
moderator locked out of Modbot whose reset link never comes. So Modbot keeps its own limit below
the relay's and queues what does not fit rather than dropping it.

**The setting.** *Email limit per 24 hours* on Settings → Integrations → Email:
`settings.email_limit_per24hours`, a whole number, default **100**, minimum **20**
(`PUT /api/settings/email/limit`, `ManageSettings`; recorded as a `SettingsChanged` fact).

**A rolling window.** The count is the emails sent in the last 24 hours, not since midnight --
a calendar day would allow the whole limit at 23:59 and again at 00:01.

**Two kinds of email, and 20 kept for one of them.** Every message says which it is
(`EmailKind`):

| Kind | What | May send while the last 24 hours hold fewer than |
|---|---|---|
| **Account** | password reset links, invite links, email checks, sign-in and security notices -- anything in the account flows | the limit |
| **Other** | the test message, alerts, notifications, everything else | the limit − 20 |

So however much other email is asked for, the last 20 sends are always there for account email.
At the minimum limit of 20, other email has no room at all and waits until the limit is raised.

**One way to send.** `IEmailSender` is implemented only by `EmailSender`, which counts every
message. The SMTP relay sits behind `IMailRelay`, which nothing else calls; the registration is a
plain `Add`, so nothing registered earlier can put the relay in the limit's place.

**The queue, `email_queue`.** Every email gets a row, sent straight away or not, and the rows in
`sending` or `sent` with `sent_at` in the last 24 hours *are* the count -- there is no second
counter to drift. Columns: kind, recipient, subject, state (`queued`, `sending`, `sent`,
`failed`, `expired`), `queued_at`, `sent_at`, `expires_at`, `attempts`, `next_attempt_at`,
`last_error`, `finished_at`, and `body_encrypted`. The body is stored only while a message
waits, encrypted with `ISecretProtector`, and cleared the moment it is sent, fails or expires: a
reset link's token is in it, and §4.1 stores only hashes so that reading the database yields no
working link. Sent rows are deleted once they are a day old; failed and expired ones after three
days.

**Deciding.** Under a PostgreSQL advisory lock: a message goes now if nothing it waits behind is
queued and the window has room for its kind; it is then written as `sending` before the lock is
let go, so two requests cannot both take the last place. Account email waits only behind queued
account email; other email waits behind everything. The relay is called with no lock held. A
message sent straight away that the relay refuses is **not** queued -- the caller gets the
relay's words as before, because the test message exists to show them.

**Sending the queue.** `EmailQueueService` runs a pass every 30 seconds. Each pass first marks
expired every queued message whose `expires_at` has passed, then takes the next message --
**account email first, then oldest first** -- if the window has room for its kind, marks it
`sending` under the lock, and hands it to the relay once. A queued message goes out when the
send that filled the window turns 24 hours old. A refusal puts it back with `next_attempt_at`
5 minutes later, then 15, 45 and so on up to 6 hours; after 5 refusals it is `failed` with the
relay's sentence. It is never retried in a loop. A message a stopped process left in `sending`
for ten minutes is marked failed, not tried again, because it may already have gone.

**Links that go stale.** The reset link passes its own expiry as the message's `expires_at`. A
link still queued when it expires is marked `expired` and never sent -- a dead link arriving is
worse than none. Invite links will do the same when they are sent by email (§9); today they are
copied by the administrator and never queued.

**What people see.**

- The person asking for a reset: the §4.2 sentence, with *"Email is running behind, so it may be
  late."* while account email would be queued. Nothing about the account.
- The test message: *Queued, sends at …*, or *Queued.* when other email has no room.
- The `ResetLinkCreated` fact records `queued` and `sendsAt`.
- Settings → Integrations → Email: the limit field, *Sent in the last 24 hours* as "N of limit",
  *Queued*, *Next queued email*, and a list of queued, failed and expired messages -- recipient,
  kind, queued at, state. Never bodies. (`GET /api/settings/email`, `ManageSettings`.)
- Health: an *Email* row while any message is queued or has failed (`email` on
  `GET /api/health/sync`, `ViewOperationalLog`).

Nothing logs a body or a token; the queue's warnings name the message's id and kind only.

## 5. Sessions

`modbot_user` gains `sessions_valid_after`. Every session cookie carries a `modbot:signed_in_at`
claim stamped from `IModbotClock` at sign-in. On every authenticated request the cookie handler's
`OnValidatePrincipal` looks the account up once and rejects the session if the account is gone,
disabled, or was signed in **before** `sessions_valid_after`.

Both timestamps come from the same clock, which is what makes the comparison meaningful; the
cookie's own `IssuedUtc` is stamped from the framework's clock and is not used.

What moves the timestamp:

| Event | Effect |
|---|---|
| Disabled | every session ends |
| Reset link used | every session ends; the person signs in with the new password |
| Own password changed | every *other* session ends; the current one is re-issued |
| Sign out everywhere | every session ends, including this one |

The same per-request lookup **refreshes the permissions claim** when it differs from the roles'
union, so a role change takes effect on the next request rather than the next sign-in.
`.agent/docs/security.md`'s "disable then delete the key ring" emergency procedure is no longer needed
and the page is updated.

## 6. Facts

Every account event is a fact (§5.9). New types, all under `modbot.user.*` and `modbot.role.*`,
all moderation retention (the prefix is not a presence prefix), all in the **operational** log
(§5.9.4 — they are the "Auth" row):

| Type | When |
|---|---|
| `modbot.user.create` | an account was created, by an administrator or through an invite |
| `modbot.user.invite.create` / `.use` / `.revoke` | invite lifecycle |
| `modbot.user.disable` / `.enable` | |
| `modbot.user.roles.change` | payload carries before and after role names |
| `modbot.user.password.change` | own password changed |
| `modbot.user.username.change` | own username changed; payload carries old and new |
| `modbot.user.contact.change` | email or Discord user id set; payload says which fields, not the values |
| `modbot.user.vrchat.link` | a VRChat account was linked; payload carries the id and display name |
| `modbot.user.password.reset.create` / `.use` | reset link lifecycle; `create` says whether an administrator or the person asked, and how it was sent |
| `modbot.user.login` / `modbot.user.login.failed` | every attempt |
| `modbot.user.sign-out-everywhere` | |
| `modbot.role.create` / `.change` / `.delete` | custom and built-in role edits |

The existing `modbot.auth.login`, `modbot.auth.login.failed` and `modbot.auth.password.change`
constants are renamed into this family. Nothing has ever written them, so it is not a data
migration.

Conventions: subject is the account (`Modbot` platform, the user's id); actor is whoever did it,
with `actorDisplayName` in the payload so the audit log shows a name the way it does for VRChat
actors. **A failed login records the username attempted and the caller's address, never the
password.** Where the username matches an account the fact's subject is that account, so a
profile of the account shows the attempts against it.

Account changes and their facts are written in one transaction: an account change with no record
of who made it is the failure §5.8 exists to prevent.

## 7. Login slowdown

Failed attempts are counted in memory per normalised username and per client address, with a
fifteen-minute window. Each attempt waits `2^(failures-1)` seconds, capped at twenty, using the
larger of the two counts, before the password is even checked. **There is no lockout**: a correct
password always works after the wait, so an attacker hammering `owner` cannot keep the owner out
— they can only make the owner wait twenty seconds. The wait goes through `IDelayScheduler` so
tests can observe it without sleeping. The response for every failure stays a bare 401.

## 8. Web UI

- **Users** page (`ManageUsers`): the list with role badges and a *Disabled* marker; a
  *Add someone* dialog offering "send them a link" or "set a temporary password"; per-user side
  panel with roles, disable/enable, Discord user id, and *Create a reset link*. Links are shown
  once, with a copy button, and never again — the server does not have them.
- **Roles** page (`ManageRoles`): each role's permissions as a checklist. Every permission has a
  plain label and a one-line description supplied by the server, so the words on the page and the
  words in the API are the same words.
- **Account** page (anyone signed in): change username, change password, email and Discord user
  id for reset links, the linked VRChat account with a *Link a different account* button, and
  sign out everywhere.
- `/join/<token>`, `/reset/<token>`, *Forgot password* and *Link your VRChat account* live
  outside the app shell, next to the sign-in page. The link page is the same component the
  wizard step uses.
- Settings → Integrations gains *Send a test email*.
- Navigation entries are shown only to accounts holding the relevant permission. The API returns
  permissions as **names** as well as the bitfield: the bitfield is a 64-bit integer and
  `Administrator` is bit 62, which JavaScript's number type cannot carry alongside any other bit.

## 9. Deferred

- Forcing a password change after a temporary password. Wants a per-request flag and a
  redirect; the invite path already avoids the problem, so it waits.
- Discord OAuth sign-in and "require Discord login" (§7.2). The Discord user id on an account is
  typed in, not proven, and is used only to reach the person, never to sign them in.
- API keys (§6.3, §7.3).
- Sending *invite* links by email or Discord. Reset links go that way (§4.2); invites are still
  copied by the administrator, because the invitee has no account to hold an address yet.

## 10. Testing

Role union (pure). Invite lifecycle: once only, expiry, revoked, disabled inviter. Reset lifecycle:
once only, expiry, ends sessions. Disable ends an open session on its next request; re-enable does
not revive it. Role change is visible on the next request. Every new endpoint answers 401 without a
session and 403 without the permission. Every operation leaves its fact, and a failed login's fact
contains the username and not the password. Forgot-password answers identically for a real and an
unknown username, picks email over Discord, and sends nothing when neither is configured. The email limit (§4.4): other email stops at the limit
less 20 while account email still goes; a message over the limit is queued and sent when the
oldest send turns a day old, and only once; account email leaves the queue first; a refused
queued message is tried again later and marked failed after five refusals; a queued link that
expires is never sent; forgot-password answers identically for a real and an unknown username
while email is queued; the limit refuses anything below 20; the email settings take
`ManageSettings`. The
VRChat link: a URL and a bare id parse the same, a bio without the code does not link, a bio with
it does and records the fact, codes expire and are good once, Check is limited, and an unlinked
session is refused everywhere but the link and sign-out endpoints. All against real PostgreSQL,
through the real cookie pipeline, with a scripted gate.
