# Modbot — The server page, account email, and the updates opt-in

- **Date:** 2026-09-17
- **Status:** Draft, awaiting review
- **Covers:** `GET /api/server`; the owner-email rule and its switch; the group in the register
  link; email required and unique on every account; signing in with an email or a username; the
  "new features and updates" checkbox and its call to Modbot Cloud
- **Depends on:** accounts and access (2026-09-13), central services (2026-09-11)
- **Narrows:** accounts and access §4 ("email is optional, contact details only")

---

## 1. What this is for

Somebody adding a Modbot server on my.modbot.co types an address into a box. Today the page has no
way to find out whether that address is a Modbot at all, let alone whose. And somebody who signs
in to a Modbot has to remember which of their two names they used, because only one of them works.

Both are the same shape of problem: Modbot knows things about itself and about the people who use
it, and does not say them where they would be useful. This design adds one anonymous endpoint that
says what this server is, makes an email address something every account actually has, lets it be
typed into the sign-in box, and — since the person is making an account anyway — offers them the
project's news.

## 2. `GET /api/server`

### 2.1 Why "server"

**An instance is a VRChat instance.** `/api/instances` already means that, and the naming rule is
one word for one thing (CLAUDE.md, since 2026-09-17). The thing this endpoint describes is the
Modbot server a group runs, so it is called the server. my.modbot.co's own word for the same thing
is a server too, which is the other half of why: the two sides of this contract should not need a
translation table.

### 2.2 The answer

```
GET /api/server   → 200

{ "name": …, "groupId": …, "iconUrl": …, "bannerUrl": …,
  "ownerEmail": …, "version": …, "publicAddress": … }
```

| Field | From |
|---|---|
| `name` | `settings.ManagedGroupName` |
| `groupId` | `settings.ManagedGroupId` |
| `iconUrl` | `settings.ManagedGroupIconUrl` |
| `bannerUrl` | `settings.ManagedGroupBannerUrl` |
| `ownerEmail` | §2.3 |
| `version` | `ModbotVersion.Release` |
| `publicAddress` | `settings.PublicAddress` — what the operator saved, never the request's host |

**Every field is null until it is known**, and **a deployment that has not been set up answers 200
with nulls, not 404**. "This is a Modbot that has not been set up yet" is a useful answer to the
person typing an address; a 404 is indistinguishable from a wrong address, a proxy in the way, or
somebody else's web server.

**Anonymous and readable from any origin.** The browser reading it belongs to somebody who has no
account here and may never have one. `Access-Control-Allow-Origin: *` is written on the endpoint
rather than through the CORS middleware, because mounting middleware in every host that maps this
API, for one endpoint, buys nothing: a cross-origin `GET` with no custom headers is a simple
request and needs no preflight. An `OPTIONS` answer is mapped anyway for a client that sends one.
No credentials are allowed with it, so a session cookie can never ride along.

**What it does not say:** nothing about members, moderation, staff accounts, VRChat instances or
settings, and nothing that is a secret. Everything but the owner's address is already visible to
anyone who opens the sign-in page.

### 2.3 Who the owner is, and the switch

**The rule: the oldest enabled account holding the `Administrator` permission that has an email
address.** Oldest by `CreatedAt`, ties broken by id so the answer never depends on row order.

In practice that is the account the setup wizard made, because the wizard requires an address for
it and nothing older can exist. The rule is written in terms of age and permission rather than
"the account onboarding created" because Modbot stores no marker saying which one that was, and a
marker would be one more thing to keep true through disabling, role changes and imports. Age and
permission are already true. Disabled accounts are skipped: an owner who has been disabled is not
somebody to write to.

This is not a new rule — it is the one `AdministratorContact` already used for the contact address
VRChat is given in every User-Agent. It moves into `OwnerAccount` so both readers share it: the
address VRChat is told to write to and the address my.modbot.co shows must be the same person.

**The switch: `server.showOwnerEmail`, default on.** Stored on the settings row
(`settings.server_show_owner_email`), read and written at `GET`/`PUT /api/settings/server` under
`ManageSettings`, recorded as a `SettingsChanged` fact with before and after. With it off the field
is null and nothing else changes. On by default because the address is what tells somebody whose
server they have found, because a group's moderation contact is usually public already, and because
an upgrade should not quietly stop publishing what a deployment was already willing to publish.

The control is the one switch on the **Host & Database → Public address** card: both settings are
about what this server tells the outside world it is.

## 3. The group in the register link

`registerLink()` built `<my>/register?url=<server>`. It now appends, each URL-encoded and each left
out when the server does not know it:

```
&groupId=…&name=…&icon=…&banner=…
```

So my.modbot.co can show which group is being added **before it has talked to that server**, and can
still show it when the browser cannot reach the server at all — which is exactly the case where a
person most needs to see whether they typed the right address.

**The values come from the onboarding status, not from `/api/server`.** The app loads that status
before it renders anything and it already carries the group's id, name, icon and banner. Reading
`/api/server` instead would be a second request for four values the page is holding. They are kept
in the `myModbot` module beside the selector's origin and set from the same place the origin is,
which is why every existing caller of `registerLink` and `openRegisterOnce` is unchanged.

## 4. Email on every account

### 4.1 Required

**Every account gets an email address at creation, whether or not this server can send email.**
An account with no address is an account whose password cannot be reset without an administrator
standing next to it — and the deployments that most need a reset path are exactly the ones where
the optional field was left blank. The address is worth having before there is anywhere to send
from, because that is the order these things happen in.

Required in all four places an account is made: the wizard's first administrator (which already
required it), the users page's temporary-password path, an accepted invite, and the demo.
`UserAccountService.CreateAsync` takes the address as a parameter rather than leaving callers to
set the field afterwards, so the rule is one the compiler checks at each call site rather than one
a fifth caller can forget.

**An invite never carries an address.** The invitee types their own: the person who sent the invite
does not get to decide where the invitee's reset link goes.

### 4.2 The shape check

Something, an `@`, then something with a dot in it, no spaces, at most 256 characters
(`EmailAddress.LooksLike`). **Never a strict RFC 5322 pattern** — those reject addresses real relays
deliver to, and the typos that matter are found by mail bouncing, not by a regex. The check exists
to catch a Discord handle typed into an email box.

### 4.3 Stored lower-cased, unique case-insensitively

Trimmed and lower-cased on the way in (`EmailAddress.Normalize`), so a plain unique index over the
column **is** the case-insensitive rule: no expression index, no second normalised column to keep
in step with the first, and a lookup is an equality test against what the person typed, lower-cased
the same way.

The index is `ix_modbot_user_email`, unique, **filtered to `email IS NOT NULL`**. Accounts made
before this exist without an address and must keep signing in; a nullable column with a filtered
index is how both are true at once.

A second account claiming an address already in use is refused with *"That email address is already
used by another account."* — a 409. This is not an enumeration risk the way a sign-in answer would
be: to reach it you are already signed in with `ManageUsers`, or holding an invite somebody sent
you, or setting up a fresh deployment.

### 4.4 What the migration does to rows that are already there

`RequireAccountEmail`. A unique index added over data that already violates it fails, and a
migration that fails halfway leaves a deployment that will not start. So, in order, in the one
transaction the migration already runs in:

1. Blank addresses become null. An empty string is not an address and would collide with every
   other empty string.
2. What is left is trimmed and lower-cased — otherwise `Alice@x.com` and `alice@x.com` stay two
   rows that the new rule says are one.
3. Where two or more accounts then hold the same address, **the oldest keeps it and the others have
   theirs cleared.** Oldest by `created_at`, ties broken by id.

**What an operator should expect.** Almost every deployment has one account with one address and
nothing happens.

- *An account with no address* keeps working exactly as before. Its username and password still
  sign in. It cannot be sent a reset link and cannot sign in by email until an address is set, which
  its account page asks for.
- *Two accounts that had the same address* — someone typed the team's shared inbox into both — end
  up with the newer one cleared, and that person is in the case above. Nothing is deleted but a
  duplicated contact detail, and every `modbot.user.contact.change` fact that put it there is still
  in the audit log.
- Going back down does **not** restore a cleared address: nothing recorded what it was, and guessing
  would hand one person's address to another account.

## 5. Signing in with either

`POST /api/auth/login` takes one field that is matched against the username **and** the email
address, both case-insensitively. The form labels it **"Email or username"**.

**One query, not two.** Username first and then email would take measurably longer for an address
than for a username, and how long a sign-in takes is visible to whoever is trying names.

**The field is still called `username` in the request.** It is what every client already sends, and
renaming it would break companions and scripts in order to say something the endpoint's description
already says.

**Nothing about the answer changes.** Every failure is the same bare 401 — wrong password, no such
account, disabled, an address nobody here uses. Which now also means the sign-in form never reveals
whether an address belongs to anybody here. The slowdown (accounts and access §7) keys on what was
typed, whichever of the two it is: keying on the resolved account would hand an attacker who knows
both of somebody's names two separate allowances against one account.

Forgot-password still takes a username only. Widening it is worth doing and is not done here (§8).

## 6. The updates checkbox

**Where.** The two screens where somebody makes an account for the first time: the wizard's
create-administrator step and the invite-accept page. Not the users page's temporary-password path —
that account is being made *by somebody else*, and ticking a box on another person's behalf to sign
them up for mail is not consent.

**The label, exactly:** `Receive emails from Modbot about new features and updates`.

**Unticked by default.** A checkbox that mails somebody unless they notice it is not consent either.

**Absent** when Modbot Cloud is switched off for this server (`MODBOT_CLOUD_DISABLED`) or when the
host registered no Cloud client at all. The server says which through `canSubscribeToUpdates` on the
onboarding status and on the invite view, so the browser never shows a box that would do nothing.

**When ticked**, after the account is committed, Modbot posts to Cloud:

```
POST <cloud>/api/v1/subscribers
{ "email": "<the account's lower-cased address>", "source": "server" }
```

Through the same `ModbotCloudAddress` and the same named `HttpClient` every other Cloud call uses,
carrying the server's own bearer (`serverId.secret`) when this server has registered. **It usually
has not** — the first administrator's account is made minutes before the first report goes out — so
the call is made without one and Cloud is expected to take it either way; the bearer, when there is
one, is what tells Cloud which server the person came from.

**It is fire-and-forget from the person's side.** Its own five-second timeout rather than the
twenty the report gets, because somebody is waiting. It never blocks or fails account creation: the
account exists either way, and an account creation that failed because a mailing list was
unreachable would be the worst possible trade. A refusal is one warning line carrying **the status
code and nothing else** — an address in a log is an address in every copy of that log.

**A fact is recorded**, `modbot.user.updates.subscribe`, subject the account, and it is written
before the call so that what the person agreed to survives Cloud being unreachable a moment later.
The payload carries no address: the account already holds it, and the audit log is read by more
people than the account page is.

**Unsubscribing is Cloud's job, not this server's.** Cloud sends the mail, so Cloud owns the link at
the bottom of it. A Modbot server has no list to remove anybody from, and building a second way to
unsubscribe would be building a way for the two to disagree.

## 7. Testing

`GET /api/server` before and after onboarding: 200 with nulls on a fresh deployment, 200 with the
group, version and address once set up, anonymous in both cases, and carrying the
allow-any-origin header. The owner rule: the oldest administrator's address wins over a newer one,
a disabled administrator is skipped, an administrator with no address is skipped, a non-administrator
is never the owner, and the switch turns the field off without changing anything else.

Email: the loose shape check takes real addresses and refuses a Discord handle; the stored form is
trimmed and lower-cased; a second account with the same address in different case is refused;
creating an account with no address is refused at all four doors.

Sign-in: by username, by address, by either in the wrong case, and the 401 for a wrong password, an
unknown username and an unknown address are byte-for-byte the same answer.

The opt-in: the Cloud call is made when the box is ticked and Cloud is on; not made when it is
unticked; not made when Cloud is off; and an account is still created, still signed in and still
has its `UserCreated` fact when the Cloud call throws or times out.

The migration, against real PostgreSQL: rows with blank, mixed-case and duplicated addresses before
it, and afterwards a unique index that exists, the oldest account keeping the shared address, and
every account still able to sign in.

## 8. Not done here

- **Forgot-password by email address.** It still takes a username. Widening it means widening the
  §4.2 identical-answer guarantee to cover addresses, which is worth doing deliberately rather than
  as a side effect of this.
- **A hard gate that stops an account with no address doing anything**, the way the VRChat link gate
  does. The account page asks, and that is all. A second full-app gate is a large change to make for
  a case that only exists on deployments upgrading from before this.
- **Unsubscribing** (§6) — Cloud's.
- **Sending invites by email**, still deferred from accounts and access §9, and now slightly closer:
  the invitee types their address at the end rather than the beginning, so there is still nowhere to
  send an invite to.
