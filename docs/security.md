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
- your AI provider's API key, if you set one

These live in the `settings` table, encrypted with AES-256-GCM. Webhook signing secrets are
encrypted the same way, in `api_webhook`, because Modbot has to read them back to sign each
delivery.

API keys are different: Modbot stores only a SHA-256 fingerprint of each key, so a database dump
contains no working key. See [the API page](api.md).

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

That sentence is about **the encryption key**, and it is worth reading it narrowly, because it is
easy to mistake for a promise that Modbot never wants a volume at all. It is not. See
§ *Volumes, and what "no volume to mount" actually claimed* below.

### If you need real encryption at rest

Encrypt the database. That is the layer where the control belongs, and every managed PostgreSQL
provider offers it. Full-disk or volume encryption on a self-hosted instance does the same job.
Modbot's column encryption is a second, weaker layer on top; it was never meant to substitute for
the first.

## Volumes, and what "no volume to mount" actually claimed

The claim above is that **the encryption key needs no volume**, and that is unchanged and still true.
It was never an argument that Modbot must not use persistent storage for anything — it was an
argument about where a key lives, made in a section about keys.

Read as a blanket rule it would be wrong, and once Modbot stores moderation evidence (video and
screenshots attached to a ban report) it is visibly wrong, because a group that keeps that evidence
on a local disk obviously needs a disk that outlives the container.

The accurate statement is narrower and more useful:

> **Volume-lessness is a property of a configuration, not of Modbot.** The recommended configuration
> has no volume. Others do, deliberately.

| | **Stateless** — recommended | **Persistent** |
|---|---|---|
| Evidence files | an S3-compatible bucket | a mounted Docker volume |
| Logs | Seq, and the container's console | files on the same volume |
| Volume | none | required |
| What a redeploy costs | nothing | nothing, provided the volume is actually mounted |

Two things follow that are worth knowing before you pick one.

**The secrets argument does not change in either.** The encryption key is a row in the database in
both configurations. A volume does not become a place to keep a key just because one is present —
that would reintroduce exactly the "a thing to lose that bricks the install" failure the section
above rejects.

**An unmounted volume is the failure this design worries about most.** If you choose file storage and
the volume is not actually mounted, the evidence goes into the container and dies with it — silently,
and usually discovered months later during the dispute the evidence existed to settle. Modbot
therefore keeps a marker file in the store and checks it on every start: if the store is not the
store it was configured against, Modbot **refuses new uploads, raises a critical alert, and shows a
banner that only an administrator can acknowledge**. It keeps running, because being down does not
bring back anything that was lost and does stop it recording anything new.

If you are not certain your volume is mounted, use a bucket.

## Environment variables

Modbot reads exactly three, and none of them is a secret in the usual sense:

| Variable | Required | Purpose |
|---|---|---|
| `PORT` | no | the port to listen on; defaults to `8080` |
| `DATABASE_URL` | yes | PostgreSQL connection, as a `postgres://` URL or an ADO.NET string |
| `SEQ_URL` | no | a [Seq](https://datalust.co/seq) endpoint to ship structured logs to |

`DATABASE_URL` contains your database password, and it is as sensitive as the database is. Treat it
that way.

Everything else — VRChat credentials, the managed group, Discord, SMTP, the egress proxy — is
entered through the onboarding wizard and stored in the database. There is no `.env` file full of
secrets to leak, and adding one is deliberately not an option.

`MODBOT_DEBUG_LOGGING` additionally enables the Debug log streams. It is a diagnostic switch, not
configuration.

### One exception, and it is a prefill rather than a setting

If you deploy on Railway and add one of its buckets, Railway injects `BUCKET`, `ACCESS_KEY_ID`,
`SECRET_ACCESS_KEY`, `REGION` and `ENDPOINT` into the service. Modbot reads them **once**, at first
boot, and only to **pre-fill the storage step of the onboarding wizard** so you are not retyping
values that are already there. You still confirm and save, and Modbot tests the connection before it
saves anything.

After that they are ignored entirely. Changing one later does not move your evidence and does not
change any setting — the database remains the only source of truth, exactly as for every other
credential. That is deliberate: a variable edit that silently repointed the file store at a different
bucket is precisely the kind of quiet data loss the marker-file check above exists to catch, and it
would be perverse to introduce it through a convenience feature.

None of these five is ever required. `DATABASE_URL` remains the only variable Modbot cannot start
without.

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

Identity and permissions travel in the session cookie as claims, and **every request checks the
account once** — one indexed read — before the claims are trusted. Three things follow:

- **Disabling an account ends its sessions on their very next request.** So does using a reset
  link, changing your own password (every session but the one you typed it in), and *Sign out
  everywhere* on the account page.
- **A role change takes effect on the next request**, not the next sign-in.
- **Every account must link the VRChat account of the person behind it** before it can do
  anything but finish the link and sign out. The proof is a code Modbot gives you, placed in your
  VRChat bio and read back through Modbot's own VRChat connection. This is what lets Modbot say
  *which person* did something, not merely which username.

Failed sign-ins slow down rather than lock out: after each wrong password the next attempt waits
longer, up to twenty seconds, counted per username and per address. A correct password always
works after the wait, so an attacker cannot lock the administrator out by hammering the username.
Every failed attempt is recorded with the username tried and the address it came from — never the
password. The sign-in form answers every failure identically, so it cannot be used to find out
which usernames exist.

### Invite and reset links

People are added with one-time invite links, and passwords are reset with one-time reset links.
Modbot stores only a hash of each link, so a copy of the database yields no working links. Invites
last 72 hours and reset links 24; both are spent on first use.

*Forgot password* on the sign-in page sends a reset link by email, or by Discord direct message,
to the details on the account. **The link is built only from the public address an administrator
saved in Settings** — never from the address the request came in on, and never from
`X-Forwarded-*` headers. Anyone can send a forgot-password request for a victim's username with a
forged host header; if Modbot built the link from that request, the victim's genuine reset email
would point at the attacker's server and hand over the token when clicked. Until a public address
is saved, nothing is sent and the sign-in page says so; the links an administrator copies from the
Users page still work, because the browser showing them knows its own address.

The response to *forgot password* is the same sentence whether or not the username exists.

### Session keys

ASP.NET Core's data protection keys sign and encrypt session cookies. Modbot **persists them in its
own database**, so a restart or redeploy no longer signs everyone out.

That is the right default — on a platform that restarts containers routinely, moderators were
otherwise being logged out for no reason they could see. But it removes something the previous
behaviour gave you for free, so it is worth stating plainly:

> **Restarting Modbot no longer ends open sessions.**

To force every session on the deployment to end at once, delete the rows from the
`data_protection_keys` table and restart. Modbot generates a fresh key ring and every existing
cookie stops validating. You should rarely need it: disabling an account, or *Sign out everywhere*
on your own, ends that account's sessions on their next request.

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
