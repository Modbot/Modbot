# Modbot — One API Grammar

- **Date:** 2026-09-28 (built 2026-10-01)
- **Status:** Implemented with this document
- **Covers:** the error shape and its codes; one name per kind of query parameter; addresses and
  methods; how numbers are described; calling the API from a web page (`MODBOT_CORS_ORIGINS`); a key
  learning its own permissions; the writes a group's own bot needs (a member's group role, a group
  invite, everyone's notes, Discord ban, unban, remove and timeout)
- **Implements:** task discovery TASK-058; API keys design §1 ("a group that wants a bot of its own")
- **Related:** foundation §3.1.1 (opaque ids), §4.3.1 (never retry a 429), §4.3.4 (rate limits),
  §5.9.1 (who asked); API keys design §3.2 (a key is capped by its account); auto-invites design §5;
  `docs/content/docs/api/conventions.mdx` and `errors.mdx`, which say the same to a reader

---

## 1. What this is for

The API grew a feature at a time. By September it answered errors in three shapes, named free text
`search`, `q` or `text`, named a person `id`, `userId`, `vrchatUserId`, `vrchat` or `subjectId`,
deleted with both `DELETE` and `POST .../delete`, described every integer as "integer or string",
could not be called from any web page but one, could not tell a key what the key itself may do, and
lacked the handful of writes a group's own bot reaches for first.

"Modbot as a platform" needs one grammar a developer can learn in an hour. Each section below is
one rule, how it is enforced, and how the old way keeps working: **everything here is additive for
at least one API version.** No successful answer changes shape; old names and addresses still work.

## 2. Errors: problem+json with a code

Every error under `/api` is `application/problem+json` (RFC 9457):

```json
{ "type": "https://docs.modbot.co/api/errors/#not-found", "title": "Not found", "status": 404,
  "detail": "No such flag.", "code": "not-found", "error": "No such flag." }
```

- **`code`** is the stable, plain, kebab-case word a program compares (`Problems.All` is the list).
  Never renamed or reused; new ones may be added, so a client falls back to the status.
- **`error`** repeats `detail`. It is what the web app's `refusalMessage` and every script written
  before this read; it stays for one API version.
- **Any other field an endpoint returned is kept** (`missingGroupPermission`, `caseId`, a connection
  diagnosis), and an endpoint's own `detail` is never written over.

**Where.** Not at each endpoint. `MapModbotApi` maps everything inside one group with no prefix, and
`ProblemFilter` on that group reshapes the result an endpoint returned before it runs: a status of
400 or more with no body, `{ error }`, an ASP.NET `ProblemDetails`, or any JSON object. Text, files
and an `error` that is an object (a protocol of its own) pass through. `ProblemMiddleware` gives a
body to an error that left the pipeline without one (a 405 from routing, a binding failure), using
the framework's own test: not started, no content type. `ProblemAuthorizationResultHandler` runs
the framework's handler and then writes the 401 or 403 body, naming `neededPermissions` from the
endpoint's `RequiresFlag`, or `vrchat-not-linked` when that was the failed requirement.

An endpoint that knows a more precise code than its status says so with `Problems.Of(status,
sentence, code)`. The group page's VRChat refusals do (`vrchat-refused`, `vrchat-rate-limited`,
`not-set-up`, `no-group`); the rest take their status's code.

**Not covered:** the companion protocol (`/api/v{n}/...`, its own codes), MCP's OAuth endpoints
(`/mcp/*`, outside `/api`, OAuth's own error shape), and an *empty* answer on the VRChat proxy,
which is VRChat's and passes untouched (the proxy's own refusals are results and are shaped).

## 3. One name per kind of query parameter

| Common name | Endpoint's own, still accepted |
|---|---|
| `search` | `q` (`/api/search`, `/api/audit`), `text` (`/api/logs`) |
| `status` | `state` (flags, Discord members, reviews, giveaways) |
| `vrchatUserId` | `id` (`/api/vrchat-users/*`, membership, one repeat offender), `userId` (cases), `vrchat` (flags, watches) |
| `discordUserId` | `discord` (flags, watches) |

`QueryAliases.Table` is the whole list. A middleware copies the common name to the endpoint's own
before binding, only when the own is absent, so the own wins when both are sent and nothing that
works today changes meaning. The OpenAPI document names the common one and mentions the other.

A table rather than an attribute per endpoint: the grammar reads in one place, and adding a name
never means touching a feature. Only where the parameter means exactly the common name: the notes
list's `userId` and the audit log's `person` take Discord and Modbot ids too, so they are left alone.

## 4. Addresses and methods

What an endpoint acts on is in its address; the method says what happens. The three deletions that
were `POST .../delete` (a user, a group role, a group post) now also answer `DELETE` at their own
address, and the two changes that were `PUT` to the list with the id in the body (a group role, a
group post) also answer `PUT /{id}`. The old routes still answer exactly as they did; the
`ReplacedBy` marker makes the document call them deprecated and name the new one.

This narrows the group page's "the id travels in the body" (its endpoints' remarks). The reason
given was that a route id invites a format check; a route parameter with no constraint checks
nothing, so foundation §3.1.1 holds in the address as it does in a body. A body naming a different
id from the address is refused before anything reaches VRChat.

## 5. Numbers

The server reads a number sent as text (the framework's web defaults), so the OpenAPI generator
typed over five hundred fields "integer or string" with a digits pattern. Answers always carry
numbers, so a schema transformer describes them as integer or number, keeping `null` where it
applies. Requests are read exactly as before.

**No id had to become text.** Modbot's numeric ids are row numbers, far below 2^53; the event
stream's ids are already strings. The one number above 2^53 is the permission bitfield
(Administrator is bit 62). Its schema now says `int64` and says to read the names, which every
answer carrying it already has beside it (`permissionNames`); `PermissionInfo.value` says the same.

## 6. Calling from a web page

`MODBOT_CORS_ORIGINS` names the origins that may call `/api` from a browser, separated by commas or
spaces, or `*`. Unset, nothing changes. Set, the framework's CORS middleware runs after routing and
before sign-in under `/api`, so a preflight is answered without a sign-in check.

- **Never credentials.** No `Access-Control-Allow-Credentials`, so a browser never shares an
  answer given to the session cookie with another origin. A page elsewhere uses a key.
- **`/api/server` keeps its own rule** (any origin, always) and is outside the policy, whose answer
  to a preflight from an unlisted origin would otherwise replace the endpoint's own.

An environment variable, not a setting: foundation §2.6 keeps configuration in the database, but
`MODBOT_MY_URL` set the precedent for a statement about where this server sits among other sites,
and nobody signed in to the app should be able to widen who may call it. `ModbotEnvironment`'s
remarks record the exception.

## 7. A key's own permissions

`GET /api/auth/me` with a key answered with the account's permissions, so a program learned its
own limits only from the event socket's `hello`. The answer now has `apiKey`: the key's id, name,
first characters, expiry, and `permissionNames` as they stand now (the key's own, capped by the
account, API keys design §3.2), which is exactly what each request with it is checked against. A
session gets `apiKey: null`. The account's `permissions` and `permissionNames` are unchanged.

## 8. The writes a group's own bot needs

Every one is permission-gated, one request, never retried (§4.3.1), records nothing unless the
platform accepted, and then writes a fact naming the Modbot account that asked (§5.9.1), beside the
platform's own record, which can only name Modbot's account or bot.

| Endpoint | Permission | Fact | Rate |
|---|---|---|---|
| `PUT /api/group/members/{userId}/roles/{roleId}` | `ManageGroupRoles` | `modbot.group.role.give` | `moderation.write` |
| `DELETE /api/group/members/{userId}/roles/{roleId}` | `ManageGroupRoles` | `modbot.group.role.take` | `moderation.write` |
| `POST /api/group/invites` | `ManageGroupInvites` | `modbot.group.invite.create` | `groups.invites` |
| `GET /api/notes/all` | `ViewAuditLog` | (a read) | none |
| `POST /api/discord/bans` | `DiscordBan` | `modbot.action.discord.ban` | Discord's own |
| `DELETE /api/discord/bans/{id}` | `DiscordUnban` | `modbot.action.discord.unban` | Discord's own |
| `POST /api/discord/members/{id}/kick` | `DiscordKick` | `modbot.action.discord.kick` | Discord's own |
| `POST /api/discord/members/{id}/timeout` | `DiscordTimeOut` | `modbot.action.discord.timeout` | Discord's own |

**Rate limits (§4.3.4).** No new VRChat endpoint is used, so no new limit was set. Giving and taking
a role are `AddGroupMemberRole` and `RemoveGroupMemberRole`, which role sync already calls through
`GroupRoles` on `moderation.write` (0.3 per second, `RateLimitOptions`); the API calls them at
interactive priority, through the same class. An invite is `CreateGroupInvite` through
`GroupInvites`, the code auto-invites use, on `groups.invites` (one per thirty seconds, the
maintainer's answer of 2026-09-19); the stored one-invite-every-thirty-seconds holds for both
callers, and asking sooner answers 429 `too-many-requests` with `Retry-After`. Because it writes the
auto-invites' own row, the person counts as invited for auto-invites' own wait, and the invite
counts in their "Invites sent". The §4.3.4 table's rows for both classes say so.

**Modbot's own account.** Neither VRChat write acts on the account Modbot signs in as, for the reason
the moderation buttons will not: taking its roles away takes away the access everything depends on.

**Discord.** Four new permissions (bits 47 to 50; 45 and 46 are kept for opening and closing
instances), apart from the VRChat `Kick`, `Ban` and `Unban`, because acting on one platform is not
permission to act on the other and Discord keeps its own apart the same way. The calls are the
gateway's existing by-id REST ones (ban sync's ban, unban and remove; AutoMod's timeout), through a
new Core interface, `IDiscordMemberActions`, so the API does not depend on the bot. The audit log
reason reads "Modbot: banned by <account>: <reason>", as a copied ban's does. "Already so" answers
`changed: false` and writes no fact. A ban made here is a bot's ban to ban sync and is not copied to
VRChat unless the operator asked for bots' bans to be. Removing a timeout early is left out: the
gateway has no call for it yet.

**Everyone's notes** are paged by the last note's time and id together, as the audit log is,
because an imported note carries its original date and so is not in id order.

## 9. Left for later

- **Paging.** Seven styles remain (page and size, offset and limit, keyset, cursors). Unifying them
  touches every list's answer and is TASK-036's.
- **An unknown `/api` address**, or a known one with the wrong method, still falls through to the
  web app's fallback (`MapFallbackToFile`), which answers with the app's page or an empty `404`
  rather than a problem. A catch-all under `/api` would fix both; it changes what those requests
  answer today, so it wants its own look.
- **Dropping `error`** from errors, and the deprecated routes, at the next API version.
- **Bulk writes and export** (task discovery's platform gaps 1 and 7) are not here.
