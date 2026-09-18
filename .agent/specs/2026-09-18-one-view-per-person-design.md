# Modbot — One view per person

- **Date:** 2026-09-18
- **Status:** Implemented with this document
- **Covers:** tying a person's VRChat account, Discord account and Modbot account together;
  which addresses open the person popup; what the popup shows and which tabs it has; the
  permission on each part; `GET /api/people`; `?account=` on the audit log
- **Implements:** foundation §10.2 (the subject popup and the `?subject=` stack), §5.9 (one
  audit log, not two systems), §5.9.1 (attribution), §3.1.1 (ids are opaque)
- **Related:** accounts and access design §4.3 (the required VRChat link), §6 (account facts);
  Discord account linking design §2 (the proved link), §11 (`ManageDiscordLinks`)

---

## 1. The problem

One human being is up to three records in Modbot:

- a **VRChat account** (`vrchat_user`, and the group membership and bans that hang off it),
- a **Discord account** (`discord_member`, their messages, voice and roles),
- a **Modbot account** (`modbot_user`) — the thing they sign in with, if they are staff.

Until this document each was a separate screen and none of them reached the third. A moderator
following a Discord card landed on the Discord popup, which pointed at the VRChat popup, which
pointed back. A moderator following a row of the operational log — *mira changed alice's roles* —
landed on nothing at all: a Modbot account was not a subject, so clicking it opened the VRChat
person popup on a Modbot id that VRChat has never heard of.

And a Modbot account's own record — its sign-ins, its role changes, the settings it changed and
every kick and ban it pressed — was **on no screen**. The facts existed; nothing read them back.

The maintainer's words: *"it all needs to be linked in one view so we can easily view and access
them all and all relevant history."*

## 2. What this adds

- `GET /api/people` — one person, whichever of their three accounts you name.
- `?account=` on `GET /api/audit` — one Modbot account's whole history.
- `?subject=account:<id>` in the web app's address vocabulary.
- One popup for all three, replacing the separate Discord person popup.

Nothing is stored. There is no new table and no new column: everything here is read from
`modbot_user`, `discord_account_link` and the fact log, which already held all of it.

## 3. Resolution

### 3.1 Only two things tie accounts together

1. **The proved link** — an active row in `discord_account_link`, where both sides were proved
   (Discord account linking design §2).
2. **What a Modbot account records** — `modbot_user.vrchat_user_id`, proved by the bio code
   (accounts and access design §4.3), and `modbot_user.discord_user_id`, which an administrator
   **typed in and nobody proved**.

Nothing else ties anything. **Display names are never compared** — two people called Twin are two
people — and **an id's shape is never read** (foundation §3.1.1). An ended link ties nothing: the
row stays for the history, and `IsActive` is what resolution asks.

### 3.2 `foundBy` travels with each account

Every account in the answer carries what tied it:

| `foundBy` | Means |
|---|---|
| `asked` | the account the link named. Nothing was resolved to reach it. |
| `link` | the proved Discord account link. |
| `account` | an id recorded on a Modbot account. |

This is not decoration. A Discord id typed onto a staff account and a Discord account that proved
a link are **different claims**, and a screen that drew them identically would be inventing the
stronger one. So the card shows a link's date, roles and **Unlink** only for `link`, and the
weaker case gets the name and the id alone. Where both exist, the proved link wins.

### 3.3 The rules, exactly

Arriving by a **VRChat** id:

- Discord: the active link, else the id typed on the Modbot account whose `vrchat_user_id` matches.
- Modbot account: the account whose `vrchat_user_id` matches.

Arriving by a **Discord** id:

- VRChat: the active link, else the `vrchat_user_id` on the account whose `discord_user_id` matches.
- Modbot account: the account whose `discord_user_id` matches, else the account whose
  `vrchat_user_id` matches the VRChat account just resolved.

Arriving by a **Modbot account** id:

- VRChat and Discord: what the account itself records; Discord falls back to the active link on
  the resolved VRChat account.

An account that ties to nothing **answers with itself and nulls**. That is the honest answer, and
a view that opens showing one account and saying the other two are missing is more useful than an
error page.

### 3.4 The old address is unchanged

`?subject=<vrchat id>` is what every Discord card, every reset email and every link anybody has
already pasted contains, and foundation §10.2 fixes it. **It still means exactly what it meant.**
Resolution happens behind it: the address names the VRChat account, the server ties the rest to
it, and the audit log's own `?subject=` filter is untouched — `/api/audit?subject=usr_…` still
returns facts whose subject is that id and nothing more.

`discord-person:` is unchanged too. `account:` is new, and a link that carries it opens the same
popup. Three values rather than one because **a link has to say which kind of id it holds**: the
ids are opaque, so a bare value cannot be told apart.

## 4. One view, and how its history is arranged

### 4.1 One popup

The Discord person popup is **gone as a popup**. Its parts — who they are in the server, their
comings and goings, their messages, their activity — are parts of the person popup now. The popup
is still called **Person**, because that is what it is about: a person, not an account. Every kind
of link lands on it.

The left column carries identity for each account, and **says which are missing**: *No VRChat
account.*, *No Discord account.*, *No Modbot account.* — but only where the reader may be told
(§5).

### 4.2 Merged, or separate tabs? Both, deliberately

**Logs is merged.** *What happened to this person* is a question that spans their accounts, and
answering it should not require knowing which account to click first. So Logs is every fact about
or by any of their accounts, newest first — the VRChat subject and actor halves, the Discord
subject and actor halves, and the Modbot account's own history, merged and cut.

**Every row says which account it was found under.** The merge is a convenience; it is not
evidence. A row that hid where it came from would be claiming the timeline proves more than the
two ties in §3.1 support — and a moderator reading a merged list is precisely the person who must
not be allowed to forget that a Discord ban and a group ban are different events about different
accounts.

**Account, Discord and Messages stay whole.** *What did this moderator do* is a different question
from *what happened to this person*, and answering it in a list interleaved with somebody's
VRChat joins makes it harder, not easier. Messages are a conversation, paged, hundreds long; they
do not interleave with bans at all. Profile history and Cases were already their own tabs and stay
so.

So: **Overview, Logs, History, Cases, Discord, Messages, Account, Metrics, JSON** — each tab
present only when there is an account behind it and the reader may see it.

### 4.3 Why not a page

For the same reason §10.2 gave for the popup in the first place: moderation is
interruption-driven, and a moderator who notices a name while scanning must be able to look
without losing the scan. Three accounts in one popup does not change that; it removes two of the
three navigations it used to cost.

## 5. Permissions

**A part is shown only where the caller could already read it somewhere else.** Tying accounts
together must save a moderator three screens, never hand them a fourth thing to see.

| Part | Permission | Because that is what already answers it |
|---|---|---|
| The account the link named | — | the caller had its id in the address |
| Which Discord account belongs to which VRChat account | `ViewProfile` | `GET /api/discord-links` |
| The VRChat display name | `ViewProfile` | `GET /api/vrchat-users/profile` |
| The Discord display name and member row | `ViewMembers` | `GET /api/discord/members/{id}` |
| Which person holds which Modbot account | `ViewOperationalLog` or `ManageUsers` | the operational log's `modbot.user.vrchat.link` facts; the users page |
| The Modbot account's history | `ViewAuditLog` and/or `ViewOperationalLog` | the audit log, unchanged |
| Discord messages | `ReadDiscordMessages` | the Messages tab |
| Membership, Kick, Ban, Unban | `ViewMembers`, `Kick`, `Ban`, `Unban` | unchanged |

Two consequences worth stating:

- **Withheld is not the same as absent.** `canSeeAccount` says which it is. A screen that told a
  moderator *"no Modbot account"* when the truth was *"you may not be told"* would be a lie they
  would act on.
- **An account id resolves to nothing for somebody who may not know who holds accounts.** Not to a
  VRChat id, not to a name. Otherwise the mapping the permission exists to protect would leak
  backwards through the resolution.

No new permission was added. Every question this view asks was already somebody's permission to
answer.

## 6. The account's history is a filter, not a second log

An account's history sits on **both sides** of the fact log:

- what was **done to** it — `modbot.user.login`, `.roles.change`, `.disable` — against the
  **subject**;
- what it **did** — `modbot.action.ban`, `modbot.settings.change`, every kick and ban pressed in
  Modbot — against the **actor**, with `actor_platform = Modbot` and the account's id.

Either filter alone is half the story, so `?account=` matches both:

```sql
(subject_platform = 'Modbot' AND subject_id = :account)
OR (actor_platform = 'Modbot' AND actor_id  = :account)
```

**A filter on the one log rather than a log of its own**, because foundation §5.9 says there is no
second audit system — and because everything the audit log already gets right comes free: keyset
paging, the type filter, the coverage figures, and the permission narrowing. A caller with only
`ViewAuditLog` gets the account's moderation actions; one with only `ViewOperationalLog` gets its
sign-ins and role changes; one with neither is refused, exactly as for the log itself. Asking
about an account never widens what a caller may read.

## 7. Testing

Written with this document, run in the batch's testing pass:

- each account resolves to the others, from all three directions;
- an account that ties to nothing still answers;
- two people sharing a display name stay two people; an ended link ties nothing;
- a Discord id typed onto a staff account comes back as `account`, never as `link`;
- the Modbot account is withheld — and said to be withheld — without the permission;
- an account id resolves to nothing at all for that caller;
- `?account=` returns both halves, narrowed by each of the two log permissions;
- `/api/audit?subject=<vrchat id>` filters exactly as before;
- the address vocabulary: a person is written bare, every other kind carries its prefix, a prefix
  inside an id is not a kind.

## 8. Left for later

- **No account search.** There is no "find the person behind this Modbot account by name" — the
  view is reached from a link or a fact, never from a search box. Names are not join keys (§3.1)
  and a search that guessed would be the failure this document exists to avoid.
- **The Discord side is still one server.** Modbot watches one Discord server, so the Discord
  account is one account. A deployment watching several would need the server beside the id.
- **A Modbot account with no VRChat link cannot really exist** (accounts and access §4.3 requires
  it before the account can do anything), but accounts made before that rule do exist, and this
  view opens on them rather than pretending otherwise.
- **Cases are still VRChat-only.** A case file snapshots a VRChat profile and sits beside a VRChat
  ban, so the Cases tab appears only where there is a VRChat account. Widening case files is the
  case-file design's question, not this one's.
- **The merged Logs tab reads five pages and merges them in the browser.** Correct — the newest N
  of each merged and cut to N are the newest N of all — but it is five round trips. A server-side
  "everything about this person" filter taking several subjects would be one; it is not built,
  because the filter shape (several subjects across two platforms plus an account, on both the
  subject and actor sides) is a bigger change to the audit query than this view has earned.
