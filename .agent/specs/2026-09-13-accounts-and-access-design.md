# Modbot — Accounts and Access

- **Date:** 2026-09-13
- **Status:** Draft, awaiting review
- **Covers:** staff accounts beyond the first administrator — roles, permissions, invite links,
  password resets, sessions, and the screens that manage them
- **Depends on:** M0 (fact log, `ModbotUser`, cookie sessions, `IModbotClock`)
- **Implements:** foundation §7.2, §7.3; extends §5.9.2's "Auth" row
- **Related:** foundation §5.8 (accountability), §5.9.1 (attribution must survive account changes),
  §6.3, `docs/security.md`

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
| §7.2: optional Discord OAuth, "require Discord login" setting | **Deferred.** Only the Discord user id link field ships (§4). |
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
**`ManageRoles`** permission (`1L << 18`). Deleting a role that people still hold is refused —
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
| Sign out everywhere | `POST /api/auth/sign-out-everywhere` |

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
`docs/security.md`'s "disable then delete the key ring" emergency procedure is no longer needed
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
| `modbot.user.password.reset.create` / `.use` | reset link lifecycle |
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
- **Account** page (anyone signed in): change password, sign out everywhere.
- `/join/<token>` and `/reset/<token>` live outside the app shell, next to the sign-in page.
- Navigation entries are shown only to accounts holding the relevant permission. The API returns
  permissions as **names** as well as the bitfield: the bitfield is a 64-bit integer and
  `Administrator` is bit 62, which JavaScript's number type cannot carry alongside any other bit.

## 9. Deferred

- Forcing a password change after a temporary password. Wants a per-request flag and a
  redirect; the invite path already avoids the problem, so it waits.
- Discord OAuth sign-in and "require Discord login" (§7.2).
- API keys (§6.3, §7.3).
- Sending invite and reset links by email when SMTP is configured.

## 10. Testing

Role union (pure). Invite lifecycle: once only, expiry, revoked, disabled inviter. Reset lifecycle:
once only, expiry, ends sessions. Disable ends an open session on its next request; re-enable does
not revive it. Role change is visible on the next request. Every new endpoint answers 401 without a
session and 403 without the permission. Every operation leaves its fact, and a failed login's fact
contains the username and not the password. All against real PostgreSQL, through the real cookie
pipeline.
