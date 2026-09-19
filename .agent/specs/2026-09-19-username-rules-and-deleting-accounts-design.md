# Modbot — Username rules and deleting accounts

- **Date:** 2026-09-19
- **Status:** Implemented with this document
- **Covers:** what characters a Modbot username may contain and what that does to accounts that
  already exist; deleting a Modbot account by emptying it rather than removing it; the name a
  deleted account is left under; the typed-username confirmation; the guards; what deleting leaves
  behind and where an old name therefore still shows
- **Related:** accounts and access design (§3.3 roles, §3.4 the last administrator, §4 the users
  page, §4.5 and §5 sessions, §6 the facts); server info and account email design §4 (every
  account has an address, and the owner is read off the oldest administrator with one); foundation
  §5.9.1 (attribution), §5.5 (purging a *person*, which is a different feature entirely), §4.4
  (`IModbotClock`)

> This is about **Modbot accounts** — the people who sign in and moderate. It is not about VRChat
> people. Purging a VRChat person (`UserPurger`, foundation §5.5) is a separate feature with
> opposite rules, and nothing here touches it.

---

## 1. What changed and why

Two things a moderator asked for, on the same screen:

1. A username could be anything. It can now only be letters, numbers and underscores.
2. An account could only be **disabled**. It can now be **deleted** — and deleting keeps every
   record the account is named on, because deleting one would be deleting someone else's history.

---

## 2. What a username may be made of

**Letters `a`–`z` and `A`–`Z`, digits `0`–`9`, and the underscore `_`. Nothing else.**

No spaces, no dots, no dashes, no `@`, no accents, no other script, no emoji, no zero-width
anything.

The reason is not tidiness. A username is typed into a sign-in box, read out over voice, pasted
into a Discord message, and matched case-insensitively against what is stored. Every one of those
goes wrong for a name with a space in it, and the last one goes silently wrong for a name with a
look-alike character in it: `alice` and `аlice` — the second with a Cyrillic а — are two accounts
that no moderator can tell apart on a screen, and in a system whose whole point is knowing who did
what, two accounts nobody can tell apart is the worst possible failure.

### 2.1 What the rule is applied to

**The trimmed name** — which is what gets stored. `  alice  ` is accepted and stored as `alice`;
`al ice` is refused, inner spaces and all. Not the raw typed string (which would refuse a harmless
trailing space) and not the normalized one (the upper-case form is a lookup key, not something
anybody types).

### 2.2 Where it lives, and every place it is checked

`UsernameRules` in `src/Modbot.Core/Users/UsernameRules.cs`, beside `EmailAddress`, which is the
same kind of rule about the same kind of field. `PasswordRules.ValidateUsername` — the name every
slice already called — now delegates to it, so no caller changed.

Every way a username can be set goes through it:

| Where | File | How |
|---|---|---|
| First-run administrator | `Onboarding/CreateAdmin/CreateAdminHandler.cs` | `PasswordRules.Validate` |
| Users page, temporary password | `Users/UserEndpoints.cs` (`POST /api/users`) | `PasswordRules.Validate` |
| Accepting an invite | `Users/InviteEndpoints.cs` | `PasswordRules.Validate` |
| Changing your own username | `Auth/Account/AccountEndpoints.cs` (`PUT /api/auth/username`) | `PasswordRules.ValidateUsername` |

The reset flow (`Users/ResetEndpoints.cs`) sets a password and never a username, so there is
nothing for it to check.

`UserAccountService.CreateAsync` — the one place in `Modbot.Core` that makes an account — throws on
a name the rule refuses. That is a backstop, not the check: by the time it fires there is no form
left to put a sentence on. It exists so a fifth caller cannot quietly get it wrong.

### 2.3 Existing accounts keep working

**The rule applies when a username is set. It is never applied when one is read.**

Somebody may already hold `Old Name`, `alice.smith` or `renée`. Those accounts sign in exactly as
they always did, and nothing asks them to change:

- `UserAccountService.FindBySignInAsync` matches the typed text against the stored
  `UsernameNormalized` **or** the stored `Email`, in one query. It reads no rule about characters,
  and a remark on it now says it must not start.
- `UserAccountService.Normalize` is `Trim().ToUpperInvariant()` and is unchanged.
- `VerifyCredentialsAsync` goes through `FindBySignInAsync`.

What the rule does catch is such a person **changing** their name: the new one has to satisfy it.
That is the right moment — it is the only moment where somebody is choosing, and where a sentence
in front of them can be acted on.

### 2.4 What a moderator is told

> A username can only have letters, numbers and underscores in it.

One sentence, no regular expression, no character class notation. The same sentence server-side and
in the browser.

### 2.5 The browser checks it too

`src/Modbot.Web/src/lib/username.ts` holds the same rule and the same sentence, and the four forms
that set a username call it before they send: the users page's "Add someone", the invite page, the
setup wizard's administrator step, and the account page's "Change your username".

**The server is what enforces it.** The browser copy exists so the answer arrives before a round
trip rather than after it. The one risk of two copies is that they drift, so the sentence and the
allowed set are written once in each file and nowhere else.

---

## 3. Deleting an account

### 3.1 The decision this narrows

`ModbotUser.IsDisabled` carried this comment:

> Disabled rather than deleted: facts reference the actor, and deleting the account would orphan
> the attribution that spec 5.9.1 exists to preserve.

That reasoning is still right, and it is not being overturned — it is being narrowed. **The row is
still never removed.** What is now possible is deleting the *person* out of it: everything that
says who the account belonged to is replaced, and the row, its id, and every record pointing at
that id stay exactly where they were. Nothing is orphaned, because nothing is removed.

The comment has been rewritten rather than left standing next to code that contradicts it.

### 3.2 What the account is left as

A deleted account is left under a name of the shape:

```
deleted_user_9fb1c47a
```

- **Lower case, underscores, hex.** That is a name the rule in §2 already accepts, so the stored
  username and the name on the screen are **one string**. Nothing has to bypass the rule, and no
  code has to know that one kind of username is special. This was the alternative to a friendly
  `Deleted User 9fb1c47a` with a separate stored form, which would have meant two names, a display
  path that has to choose between them, and a username that breaks its own rule.
- **The suffix is eight hex characters of a SHA-256 of the account's id.** Stable: the same account
  always gets the same name, computed rather than stored. A hash rather than the id's own leading
  characters because version 7 ids begin with the time they were made, so accounts created in one
  sitting would otherwise all look alike. Eight characters — thirty-two bits — is short enough to
  read aloud and to tell two rows apart at a glance.
- **Collisions cannot happen.** Eight characters make one vanishingly unlikely, not impossible, so
  `DeleteAsync` checks: if something already holds the short name, the account is left under
  `deleted_user_<the whole id, no dashes>`, which is unique by construction and still fits the
  64-character column.

### 3.3 What deleting replaces

On `ModbotUser`, in one transaction (`UserAccountService.DeleteAsync`):

| Field | After |
|---|---|
| `Username`, `UsernameNormalized` | the `deleted_user_…` name |
| `Email` | null |
| `PasswordHash` | a hash of thirty-two random bytes nobody typed |
| `DiscordUserId` | null |
| `VRChatUserId`, `VRChatDisplayName`, `VRChatLinkedAt` | null |
| `VRChatLinkCode`, `VRChatLinkCodeExpiresAt`, `VRChatLinkPendingUserId`, `VRChatLinkLastCheckAt` | null |
| `VRChatLinkChecks` | 0 |
| `Roles` | cleared |
| `IsDisabled` | true |
| `SessionsValidAfter` | now, from `IModbotClock` |
| `DeletedAt` | now, from `IModbotClock` |

Sessions end on the next request, not at the next sign-in — the same mechanism disabling uses
(accounts and access design §5). The password is hashed rather than blanked because a blank hash is
a shape the hasher never produced, and what it does with one is an implementation detail rather
than a promise.

Roles are cleared so a deleted account holds no permissions whatever happens to the row afterwards.
The **history** of its role changes is untouched — that lives in facts, not in the join table.

### 3.4 `DeletedAt`, and why it is a column

A nullable `timestamp with time zone` on `modbot_user`. Null means a live account.

It could have been read off the username prefix. It is a column because the guards on the other
endpoints need a plain answer to "is there anybody behind this row", because "when was this
deleted" is a question somebody will ask, and because a name prefix is a convention rather than a
fact. The migration (`20260919192409_AddDeletedAccounts`) adds one nullable column with no default,
so every existing row reads as not deleted — checked by hand, because a generated default of `0` or
`false` has silently switched a feature off in this repository before.

### 3.5 What deleting keeps — and where the old name still shows

**Everything the account did stays, pointing at the same id**: facts and the audit log, case files,
notes, alerts, moderation actions, reviews, imports, API keys, AI calls, roles history.

Several tables took **their own copy of the username** at the time of the event. Those copies are
**left exactly as they are**. The full list, from a search of every entity:

| Table | Column(s) |
|---|---|
| `case_file` | `author_username`, `updated_by_username`, `withdrawn_by_username` |
| `moderation_action` | `moderator_username` |
| `alert` | `dismissed_by_username` |
| `ai_call` | `username` |
| `insight` | `requested_by_username` |
| `review` | `closed_by_username` |
| `import` | `started_by_name` |
| `moderation_flag` | `dismissed_by_username`, `confirmed_by_username` |
| auto-mod term lists and topic rules | `act_set_by_username`, `trial_ended_by_username` |
| moderation test sets and rule history | `ran_by_username`, `changed_by_username` |
| `settings` | `ai_acknowledged_by_username` |
| `modbot_event` (the fact log, `jsonb`) | `actorDisplayName`, and `username` on account facts |

**They are not rewritten, and that is deliberate.** Each of those rows records what was true at the
moment it was written — who pressed the button, under what name, beside what VRChat itself
recorded. Rewriting them would make Modbot's own history disagree with VRChat's audit log and with
everybody's memory of the evening, which is reporting something false in order to look tidy. A
historical record that can be edited afterwards is not a record.

So **a deleted account's old name remains visible** on the case files it wrote, in the audit log,
and in the other rows above. That is the intended behaviour and the honest one. What is gone is the
account: the address, the password, the Discord id, the VRChat link, the permissions, and any way
back in.

The deletion's own fact carries both names (`was` and `now`), for the same reason: "somebody was
deleted", with nobody named, is not a record of anything.

*(Columns that look similar but are nothing to do with a Modbot account, and were checked and
ruled out: `discord_member.username`, `discord_account_link.discord_username`,
`discord_message.author_name`, `vrchat_world.author_name`, `giveaway_entrant.name`,
`settings.vrchat_username`, `settings.proxy_username`, `settings.smtp_username`.)*

### 3.6 Nothing in front of a moderator explains any of this

Per the working conventions, the screen carries controls and no explanations. The reasoning lives
here.

---

## 4. Confirming it

Deleting cannot be undone, so it asks for something that cannot be done by accident: **the person
deleting types the account's username out in full.**

- The browser will not enable the button until what is typed matches.
- **The server checks it too.** `POST /api/users/{id}/delete` carries `{ "username": … }` and
  refuses unless it matches the account being deleted. A confirmation that only exists in the page
  is a confirmation that anybody calling the API straight has already passed, which is to say it is
  not one.
- **How it is matched:** through `UserAccountService.Normalize` — trimmed and upper-cased — the same
  way every other username in Modbot is matched. Somebody who types `ALICE` for `alice` has read
  the name and meant it. Somebody who types `Alicia` has not.

---

## 5. The guards

All five run on the server. Four of them refuse with one plain sentence.

1. **`ManageUsers`.** The whole `/api/users` group already requires it (bit 6 of
   `ModbotPermissions`).
2. **Not your own account.** *"You cannot delete your own account."* The same guard disabling has,
   for the same reason: the one account somebody can always get wrong is the one they are signed in
   to.
3. **Not the last administrator.** `AnAdministratorWouldRemainAsync((id, false, None))` — the same
   check disabling and role changes use (accounts and access design §3.4), asked with this account
   treated as disabled and holding nothing. *"That would leave nobody who can administer Modbot."*
   A button that can lock everybody out of a deployment is not a button.
4. **The typed username must match.** §4.
5. **A deleted account has nothing else done to it.** Enabling, changing roles, setting contact
   details and making a reset link all answer *"That account has been deleted."* There is nobody
   behind the row, so none of them mean anything. Deleting an already-deleted account answers with
   the account and changes nothing — the same shape disabling an already-disabled one has.

A fact is written in the same transaction as the change: `FactType.UserDeleted`
(`modbot.user.delete`), subject the account, actor whoever did it, labelled *"Account deleted"* and
classified `Operational` like every other account fact.

---

## 6. The screen

`src/Modbot.Web/src/pages/Users.tsx`.

- **Delete this account** sits under Access, below Disable, `variant="destructive"` like the
  moderation actions. It is disabled on your own row, with the same reason on hover that Disable
  has.
- It opens a dialog titled *Delete `<username>`* with one field — *Type `<username>` to confirm* —
  and one destructive button, disabled until the typed name matches.
- A deleted account shows a **Deleted** badge in the list and its row opens nothing: there is
  nothing on the drawer that still applies to it.
- No paragraphs, hints or warnings anywhere in it.

---

## 7. What was decided against

- **Removing the row.** The original reasoning holds: facts, case files, notes, alerts and
  moderation actions all name the account id, and they would lose their author.
- **Rewriting the stored username copies in other tables.** Considered and rejected on the
  maintainer's call (§3.5): those rows say what was true then, and Modbot must not report something
  false about what happened.
- **A friendly `Deleted User 9fb1c47a` display name beside a rule-satisfying stored one.** Two
  names for one thing, and a display path that has to pick. `deleted_user_9fb1c47a` satisfies the
  rule as it stands (§3.2).
- **Letting the anonymised name bypass the character rule.** Unnecessary once the shape above was
  picked, and a rule with one exception in it is a rule every future reader has to check.
- **Allowing a deleted account to be re-enabled.** There is no password, no address and no link to
  come back to. Enabling it would produce an account nobody can sign in to and nobody can reset.
- **Applying the character rule at sign-in.** It would lock out every account made before today for
  no gain whatever (§2.3).
- **Making the confirmation case-sensitive.** The name itself is matched case-insensitively
  everywhere else in Modbot; a confirmation that is stricter than the thing it confirms is a
  puzzle, not a safeguard.
