# Security

This page states plainly what Modbot protects, what it does not, and why the line is drawn where
it is. If you are deciding whether Modbot is safe enough for your group, this is the page to read.

## Secrets at rest

Modbot stores several secrets on your behalf:

- your VRChat account's password and its two-factor (TOTP) secret
- the VRChat authentication cookie it holds between logins
- your Discord bot token
- your SMTP password
- your egress proxy's credentials, if you use one

These live in the `settings` table, encrypted with AES-256-GCM.

**The encryption key is generated on first boot and stored in the same database.**

### What that protects against

One thing: casual reading of a database dump. If a backup file, a log export, or a stray `pg_dump`
ends up somewhere it should not be, the secret columns are ciphertext rather than your VRChat
password in plain view.

### What it does not protect against

**Anyone who can read your database can decrypt your secrets.** The key is a row in that same
database. This is not a subtlety or an implementation shortcut to be fixed later — it is the
design, and stating it any more softly would be dishonest.

Concretely, encryption at rest in Modbot does **not** defend against:

- an attacker who gains access to your PostgreSQL instance
- your hosting provider, or anyone at it
- a compromise of the Modbot process itself, which holds the key in memory while running
- anyone with an administrator account in Modbot's own web UI, who can change settings through it

### Why this is the right trade

Modbot is run by community groups on managed hosting, not by security teams with a key-management
service. The realistic alternative — requiring an encryption key in an environment variable — would
be **worse**, for two reasons.

First, it would not actually be safer where Modbot runs. Railway and platforms like it store
environment variables in plaintext and display them in the dashboard to anyone with project
access. A key held there sits beside the database credentials that unlock the database it protects.
The attacker who has one has the other.

Second, it would reliably break installations. A required external key is a thing to lose, and
people lose it: a redeploy from a fresh environment, a project recreated after a billing lapse, a
handover to the next volunteer who runs the group's infrastructure. Losing that key does not
degrade Modbot, it bricks it — every stored secret becomes permanently unreadable, including the
VRChat session the group's moderation depends on.

So the key lives in the database. There is no environment variable to lose, no volume to mount, and
no key-management step in a setup that is otherwise "click the template, open the URL, follow the
wizard".

### If you need real encryption at rest

Encrypt the database. That is the layer where the control belongs, and every managed PostgreSQL
provider offers it. Full-disk or volume encryption on a self-hosted instance does the same job.
Modbot's column encryption is a second, weaker layer on top; it was never meant to substitute for
the first.

## Environment variables

Modbot reads exactly three, and none of them is a secret in the usual sense:

| Variable | Required | Purpose |
|---|---|---|
| `PORT` | yes | the port to listen on; supplied by the hosting platform |
| `DATABASE_URL` | yes | PostgreSQL connection, as a `postgres://` URL or an ADO.NET string |
| `SEQ_URL` | no | a [Seq](https://datalust.co/seq) endpoint to ship structured logs to |

`DATABASE_URL` contains your database password, and it is as sensitive as the database is. Treat it
that way.

Everything else — VRChat credentials, the managed group, Discord, SMTP, the egress proxy — is
entered through the onboarding wizard and stored in the database. There is no `.env` file full of
secrets to leak, and adding one is deliberately not an option.

`MODBOT_DEBUG_LOGGING` additionally enables the Debug log streams. It is a diagnostic switch, not
configuration.

## Logs

Modbot writes its logs to `logs/` inside the container and, optionally, to Seq.

**Secrets are never written to logs.** Outbound API calls are logged with their endpoint, status and
timing, never their credentials, and Modbot's own audit trail records that a setting changed rather
than the value it changed to.

Logs do contain VRChat user ids, display names and moderation actions — they are a record of what
your moderators did and to whom. If you ship them to Seq, you are shipping that to wherever Seq
runs. Point it somewhere you control.

## Staff accounts

Modbot has its own local accounts rather than ASP.NET Core Identity. Passwords are hashed with
ASP.NET Core's `PasswordHasher` (PBKDF2). Sessions are cookie-based, `HttpOnly`, `Secure` and
`SameSite=Lax`, and expire after 14 days of inactivity.

Identity and permissions travel in the session cookie as claims, so an authorised request costs no
database round trip. Two consequences follow, and both matter operationally:

- **A permission change takes effect at the user's next sign-in**, not immediately.
- **Disabling an account blocks future sign-ins but does not end a session already open.** The
  disabled flag is checked when someone logs in, not on every request.

To cut someone off **right now**: disable the account, then invalidate the session keys (below).

### Session keys

ASP.NET Core's data protection keys sign and encrypt session cookies. Modbot **persists them in its
own database**, so a restart or redeploy no longer signs everyone out.

That is the right default — on a platform that restarts containers routinely, moderators were
otherwise being logged out for no reason they could see. But it removes something the previous
behaviour gave you for free, so it is worth stating plainly:

> **Restarting Modbot no longer ends open sessions.**

To force every session to end, delete the rows from the `data_protection_keys` table and restart.
Modbot generates a fresh key ring and every existing cookie stops validating. That is the emergency
lever; disabling an account alone does not close a session already open.

#### These keys are stored unencrypted, and that is deliberate

The key ring is written to the database as plain XML. Encrypting it would mean protecting it with
another key, and the only place to keep that key is the same database — which is the circularity
§ *Secrets* already describes.

It changes nothing about the threat model, because **anyone who can read this table has already won**.
The same database holds the encryption key for your VRChat credentials, every staff password hash,
and every fact Modbot has recorded. A session cookie key is not the prize in that scenario.

What would genuinely help is encrypting the database itself, or restricting who can reach it — both
of which are properties of your hosting, not of Modbot. See § *Secrets*.

## Reporting a vulnerability

Open a private security advisory on the repository rather than a public issue. Modbot is run by
volunteer groups who cannot patch on the same day you disclose.
