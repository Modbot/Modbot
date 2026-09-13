# Adding your moderators

Modbot has its own accounts, separate from VRChat and Discord. Each person on your team signs in
with their own username and password, so everything Modbot records says who did it. This page is
how you get them in.

## Roles

A role is a name for a set of permissions. Every account holds one or more roles, and what a
person can do is everything their roles allow, added together.

Three roles exist from the start:

| Role | What it allows |
|---|---|
| **Administrator** | Everything, including things added in future versions. |
| **Moderator** | Kick, ban, warn and unban; attach and view evidence; read the audit log; see members and analytics. |
| **Viewer** | See members, history and analytics. Change nothing. |

You can change what Moderator and Viewer allow, and you can make your own roles on the **Roles**
page — each permission there has a plain label and a line saying what it covers. Administrator
cannot be changed, and no built-in role can be deleted. A role people hold cannot be deleted
either; move them to another role first.

Two rules keep this honest:

- You can only give people permissions you have yourself. Someone who manages users but is not an
  administrator cannot make an administrator.
- Modbot will not let you disable or demote the last administrator.

## Inviting somebody

On the **Users** page, press **Add someone** and choose **Send them a link**. Pick the roles they
should have and Modbot gives you a link. Send it to them on Discord, or however you talk.

The link works **once** and expires after **72 hours**. If the account that made it is disabled
before it is used, it stops working. Modbot keeps only a fingerprint of the link, so it cannot be
shown again — if it is lost, make a new one and take the old one back from the list on the page.

The person opens the link, picks their own username and password, and is signed in. Nobody but
them ever knows their password, which is why this is the way to add people.

### Setting a temporary password instead

If somebody is standing next to you, **Set a temporary password** creates the account on the
spot. Tell them the password in person and ask them to change it from their **Account** page
straight away. Until they do, two people know it, and Modbot cannot tell you which of them did
something.

## Linking a VRChat account

Every account has to be linked to the VRChat account of the person behind it before it can do
anything — yours included, during setup. It takes a minute:

1. Modbot shows a button that opens your VRChat profile in a new tab.
2. Copy your user id, or the address of the page, and paste it into Modbot. Either is fine.
3. Modbot gives you a short code like `modbot-7F3K9Q`. Put it anywhere in your VRChat bio, save,
   and press **Check**.

Once Modbot sees the code in the bio the link is confirmed and you can take the code out again.
Codes last thirty minutes and can be checked six times; if one runs out, start again. One VRChat
account can be linked to one Modbot account.

Until the link is done, the only things an account can do are finish the link and sign out.

## Changing roles, disabling, re-enabling

Open a person on the **Users** page. Tick or untick roles and save; the change takes effect on
their very next click, not their next sign-in.

**Disable** ends every session the person has right away and stops them signing in. Nothing they
did is removed from history — accounts are never deleted, because the record of who did what
refers to them. **Enable** lets them sign in again. Old sessions do not come back.

## When somebody forgets their password

Two ways, and you do not need both.

**They ask Modbot.** The sign-in page has **Forgot password?**. Modbot sends a reset link by email
if you have set up email and their account has an address, otherwise by Discord direct message if
you have set up a Discord bot and their account has a Discord user id. For this to work, an
administrator has to have saved the **public address** of your Modbot in Settings — the address
people type to reach it. That address is the only thing a sent link is ever built from. If none
is saved, the sign-in page says so and the second way is the one to use.

**You make them a link.** Open the person on the Users page and press **Make a reset link**. It
works once, for 24 hours. Using it sets a new password and signs the account out everywhere.

Nothing is ever sent to a person's email or Discord except a reset link they asked for.

## Your own account

The **Your account** page, from the button with your name at the top right, is where you change
your username or password, add the email address or Discord user id a reset link can reach you
at, link a different VRChat account, and **sign out everywhere** — every browser, every machine,
including this one. Changing your password signs you out everywhere else and keeps the browser
you did it in.

## What Modbot writes down

Every one of these is recorded in Modbot's own log, visible to anyone who may see the operational
log: accounts created, invite links made and used, roles changed, accounts disabled and enabled,
passwords changed, reset links made and used, VRChat accounts linked, every sign-in and every
failed sign-in. A failed sign-in records the username that was tried and where from — never the
password.

Repeated failed sign-ins slow down rather than lock anybody out: after a few wrong passwords each
attempt waits a little longer, up to twenty seconds. A correct password always works after the
wait, so nobody can keep you out of your own Modbot by hammering your username.
