# First-run setup code design

- **Date:** 2026-09-29
- **Status:** Built.
- **Covers:** the code the setup wizard asks for until the first staff account exists, where it is
  printed, and the address the Compose files open Modbot's port on
- **Narrows:** foundation spec §7.1 ("with no `ModbotUser` rows present, every route redirects to
  `/setup`"), which left the wizard open to anyone until the first account existed. The rule after
  the first account (a session holding ManageSettings) is unchanged.

---

## 1. The problem

Until the first account existed, every wizard endpoint let anyone through
(`OnboardingAccessFilter`: no account, pass), and step 1 made the caller an Administrator and signed
them in. Both Compose files published port 8080 on every address of the machine. A public Railway
address or a VPS port is found by scanners within minutes, so whoever reached a fresh deployment
first owned it, and the group's VRChat login was then typed into a stranger's server.

The connection check made it worse: it saved a proxy address, user and password to `Settings`
before running the check, for anyone, before any account existed.

## 2. The code

**Made:** `SetupCode`, a singleton in memory. No table and nothing on disk, so nothing to clean up
and nothing in a backup. It is `MODBOT_SETUP_CODE` when that is set; otherwise twelve letters from
a set of thirty with the easily misread ones left out (no 0/O, 1/I/L, 5), in three groups of four,
from `RandomNumberGenerator`. About 59 bits. A new process makes a new one, so each boot without an
account prints a new code and the old one stops working.

**Printed:** once, at startup, after migrations and after the demo's seeding, and only when no
account exists: `Setup code: XXXX-XXXX-XXXX` at Information. When an account exists the code is
dropped and nothing is printed.

The line is tagged `LogArea.ConsoleOnly` and reaches the console only. The log files, Seq and the
database log leave it out. The database log matters most: it is the **Modbot's log** page and it is
sent on to Modbot Cloud, and the code must not leave the machine. The console is what
`docker compose logs` and a host's deploy log show, which is exactly "someone who can run this
server".

**Typed:** case, spaces and dashes are ignored, since the code is read off a console. The compare is
constant-time (`CryptographicOperations.FixedTimeEquals`). A value that is nothing but dashes and
spaces counts as unset, so it can never match an empty answer.

**Dropped:** when step 1 commits the first account, and at startup when an account already exists.
A dropped code matches nothing.

## 3. Where it is asked for

In `OnboardingAccessFilter`, for **every** wizard endpoint while no account exists, in the
`X-Setup-Code` header. Missing or wrong: 401 with `{ "error": "Wrong setup code." }`.

Every endpoint rather than only step 1, because the web app calls only step 1 before the account
exists anyway, and asking everywhere means nothing at all is saved before then. That closes the
connection check's early save without changing the check itself.

`/api/onboarding/status` has no filter and stays open: the web app asks it whether to show the
code field. `/health/*` are untouched.

Once an account exists the filter's second half applies as before: a signed-in account holding
ManageSettings. The code is not a stand-in for a session then, even if someone still has it.

The web app shows a **Setup code** field above Username on step 1 while `hasAdministrator` is
false, and sends it as the header. No other screen changes.

## 4. The port

Both Compose files (`docker-compose.yml` and the landing site's copy that `get.sh` downloads) now
publish `${MODBOT_BIND:-127.0.0.1}:${MODBOT_PORT:-8080}:8080`, as Seq already did. A fresh install
is then reachable from its own machine only until the operator chooses otherwise, and the code is a
second lock rather than the only one.

- `MODBOT_BIND=0.0.0.0` opens it to other machines again. It is read by Compose only; Modbot never
  sees it.
- A reverse proxy installed on the host reaches `127.0.0.1` as before. A proxy in its own container,
  or on another machine, needs `MODBOT_BIND`.
- Reaching Modbot directly on `http://host:8080` from another machine already failed at sign-in,
  since the session cookie is Secure and only `localhost` is exempt.
- Railway uses neither file and is unaffected; there the code is the lock.
- An existing install picks the new default up the next time its Compose file is replaced.

## 5. What was left as it was

- The connection check still saves before it checks. Only who may call it changed.
- No new VRChat endpoint, no new table, no new screen.
- Demo mode seeds an account before the code would be printed, so a demo is never asked for one.

## 6. Tests

`SetupCodeTests` (Api): no code and a wrong code are refused on a fresh database with no account
made; the right code, in any case and without dashes, makes the account; after the first account the
code is dropped and refused, and so are anonymous callers; every wizard endpoint asks for it; the
connection check saves no proxy without it; status needs none; the code's shape, the environment
variable, and a dropped code. `ConsoleOnlyTests` (Core): the line stays out of the database log and
the log files. The onboarding test helpers send the host's code when there is no session.
