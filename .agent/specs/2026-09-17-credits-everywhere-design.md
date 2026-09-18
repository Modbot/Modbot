# Credits everywhere — the companion's Credits page, and pictures that load

**Status:** built, 2026-09-17.
**Supersedes nothing.** It extends `2026-09-16-logs-alerts-showcase-design.md` §7 (the showcase) and
narrows one thing in it: §7.4's "Modbot reads them through its own server" is a rule about a *Modbot
server*, and the companion is not one. See §3.2 here.

---

## 1. What was already built, before any of this

Worth writing down, because most of the feature existed and the work here was three gaps.

**Modbot Cloud** holds the showcase and publishes it with no credential:

| Route | What it answers |
|---|---|
| `GET /api/v1/sponsors` | The sponsor rows, in the order an administrator chose. |
| `GET /api/v1/early-adopters` | The early adopter rows, the same way. |
| `GET /api/v1/contributors` | The repository's contributors, read from GitHub. |

Sponsors and early adopters are one table, `showcase_entry`: a name, a link, a picture, and — when
the entry is a VRChat group — the group's id, its icon and its banner, plus where it sits in the
list. Cloud admin types the rows in behind the admin sign-in. Contributors are not a table: they
come from GitHub, cached six hours, with a refusal remembered for half an hour and bots left out.

**A Modbot server** reads all three through `CloudShowcase`, caches for six hours (ten minutes for a
failure), honours `MODBOT_CLOUD_DISABLED`, and answers `available: false` when Cloud cannot be
reached. **The web app** draws them on the Credits page's People tab.

### 1.1 The three gaps

1. **The companion had no Credits page at all.** The half of Modbot that runs on a moderator's own
   PC showed none of this.
2. **The pictures did not load.** A row's picture, group icon and group banner are addresses on
   somebody else's host — VRChat's, in practice — and VRChat will not serve its pictures to a page
   that is not VRChat's own.
3. **Nothing had been checked end to end** for the four fields a VRChat group carries: its id, its
   name, its icon and its banner.

Only the first two needed building. §4 records what the third turned up.

---

## 2. The pictures: fixed once, on Cloud

### 2.1 The problem, stated exactly

VRChat serves a group's icon and banner from its own hosts and refuses a request that did not come
from VRChat. Every reader of the showcase therefore drew an empty square where a picture should be.

The workaround available to the web app was to route the picture through a Modbot server's own file
route — which needs a signed-in session. That works for a browser inside Modbot and for nothing
else: not for the companion, not for anything the project adds later, and not for a second web app.
A workaround that only one reader can use is not a fix; it is a fourth thing to remember.

### 2.2 The decision: Cloud fetches on save and serves the bytes

When an administrator saves a showcase row, Cloud fetches each of its three pictures and keeps the
bytes in `showcase_picture`. Every reader is then handed **Cloud's own address** for that picture —
`https://cloud.modbot.co/api/v1/showcase-pictures/<id>` — which works with nothing to arrange.

The alternative considered was **proxy and cache**: keep no bytes, and have Cloud fetch from VRChat
the first time a reader asked, holding the answer in memory or on disk for a while. It was rejected:

- A cold cache still means a live request to VRChat while somebody is looking at a page, so the
  failure mode that this exists to remove — an empty square, sometimes, for reasons nobody can see —
  is not removed, only made rarer. Rare and invisible is the worst version of it.
- It would need its own size cap, its own timeout and its own answer for "VRChat said no *this*
  time", per reader, forever.
- A fetch on save happens with an administrator standing there. A fetch on read happens to a
  stranger. The first is the one worth having fail.

**Bytes in the database, not an object store.** There are a handful of showcase rows, three pictures
each, each capped at 2 MB: a few megabytes at the very worst, in a database that is already small
and is backed up and restored as one unit. An object store would be a second thing to run,
configure, back up and restore, for pictures that change a few times a year. If the showcase ever
grows to a scale where that is wrong, the address readers hold does not change — only what is behind
it — so this is reversible without touching a single reader.

### 2.3 What Cloud will accept

- An `http` or `https` address. (The admin surface already refuses anything else on the way in.)
- At most 2 MB, refused on the `Content-Length` when the other host is honest about it and on the
  bytes when it is not.
- PNG, JPEG, GIF or WebP, **decided by the first bytes of the answer, not by the `Content-Type` the
  other host sent**. These bytes are then served from Cloud's own domain, and serving an HTML page
  from Cloud's domain because somebody else's header called it a picture is exactly the mistake
  worth preventing.

Anything else — a refusal, a timeout, a file that is too big, a page wearing a picture's name —
comes back as nothing.

### 2.4 What a failed fetch does

**It keeps whatever copy the row already had**, as long as the address has not changed. One bad
minute at somebody else's host must not empty a picture that was working on every Modbot in the
world. When the address *has* changed, the old copy goes: it is no longer what the row says.

With no copy at all, the reader is handed the address that was typed in — the same behaviour as
before this was built, which is to say an empty square, which is the honest answer when Cloud could
not get the picture either.

### 2.5 The typed-in addresses are never changed

`image_url`, `group_image_url` and `group_banner_url` stay exactly as an administrator entered them.
That is what the admin surface shows, and what Cloud fetches from again on the next save. The three
new columns — `saved_image_id`, `saved_group_image_id`, `saved_group_banner_id` — are what readers
are actually given.

### 2.6 A new row per save, and a year-long cache

Each save writes a new `showcase_picture` row and deletes the one it replaced. The id is the
address, so an address never changes what it points at, and Cloud can answer with
`Cache-Control: public, max-age=31536000, immutable`. Clearing an address, or removing the showcase
row, takes the kept bytes with it; nothing else points at them.

### 2.7 The picture route is public

`GET /api/v1/showcase-pictures/<id>` needs no credential, for the same reason the three list routes
do not: every Modbot in the world draws these, and a key to read a picture the project publishes
anyway would be a credential for nothing.

### 2.8 What the web app stopped doing

The showcase pictures in `People.tsx` were drawn with `referrerPolicy="no-referrer"` — an attempt to
get past the same refusal from the browser side. It is gone. These are ordinary pictures from
Cloud's own domain now, and a workaround left in place after the thing it worked around is fixed is
a false clue for whoever reads the file next. Contributor avatars are untouched: those are GitHub's
addresses, and GitHub serves them to anybody.

---

## 3. The companion's Credits page

### 3.1 What it shows

The same three lists, in the client's own window, reachable from the sidebar like every other page
(`g c`, and in the command palette).

- A **sponsor** or an **early adopter**: their name, their picture, and where they have a VRChat
  group, the group's icon and banner. Pressing the tile opens the group's page on `vrchat.com` —
  the group wins over whatever other link the row carries, because for a VRChat group "open the
  group" is what somebody reading this page is after.
- A **contributor**: their GitHub name, their picture, and their profile.

Only an `https` link is ever opened, and only in the moderator's own browser.

### 3.2 It asks Cloud itself, never the paired server

The showcase spec §7.4 says a Modbot reads the showcase through its own server. That is a rule about
a **server**, and the reason behind it — one deployment asking Cloud a few times a day rather than
once per moderator per visit, and a deployment with Cloud turned off making no request at all — is a
reason about a deployment's own policy over its own moderators' browsers.

The companion is not a browser inside a deployment. It is a program on a volunteer's own PC, and
which Cloud it talks to is settled there and nowhere else: the `cloud` object in `settings.json` and
the `MODBOT_CLOUD_ENDPOINT` and `MODBOT_CLOUD_DISABLED` environment variables on that machine. This
is the rule the cloud event backup already follows (cloud event backup spec §3.1) and it holds here
for the same three reasons:

1. **A client with nothing paired still has a Credits page.** Routing through a server would mean
   the page is empty until a moderator has paired, which is exactly backwards for a screen whose job
   is to say who made this.
2. **A client paired with three servers would have to pick one**, and then the answer would depend
   on which — for a list that is the same everywhere.
3. **A paired server must not be able to tell the client where to send requests.** The client's
   whole trust argument is that what leaves the machine is decided on the machine. Letting a server
   name an address the client then fetches from would put a hole in it for no gain.

Turning Cloud off on that PC means **no request is made** and the page shows nothing — not a quiet
request, not a cached answer.

### 3.3 The kept copy

The three lists are written to `%APPDATA%\Modbot\credits.json` with the time they were read. On
opening, the page shows what is in that file immediately, and Cloud is asked again only when the
copy is older than six hours — the same interval a Modbot server uses, for the same reason: the
lists change when somebody types a row into Cloud admin, which is a few times a year.

The file exists so the page shows offline the same names it showed online. Nothing in it is private.
A file that will not parse is ignored and Cloud is asked again.

The read is retried every half hour while the client runs, because a PC that was offline when Modbot
started is the ordinary case and an empty Credits page for the rest of the session is a poor answer
to it. A read that is still fresh makes no request at all, so the retry costs nothing in the usual
case.

### 3.4 A Cloud that cannot be reached draws nothing

No error box, no red text, no retry button. It is not a fault a moderator can act on, and it does
not belong on a page of thank-yous. This matches the web app, which shows nothing for the same
reason.

### 3.5 The pictures

Drawn through the client's existing `GroupPictures`, the same object that fetches a paired group's
icon: one GET per address, no credential, nothing kept on disk, and a picture that will not load
leaves the name standing on its own. Because §2 made every showcase picture an address on Cloud's
own domain, these now load.

---

## 4. The three lists, checked end to end

- **Contributors really do come from GitHub** (`GitHubContributors`), on a fifteen-second timeout,
  with 404 (private repository, no token) and 403 (rate limit spent) both treated as "no
  contributors today" rather than an error, and the refusal remembered for half an hour so a rate
  limit is never turned into a ban. Nothing to fix.
- **A group's id, name, icon and banner all survive** from the admin form, through
  `showcase_entry`, through the public routes, to both readers — the admin surface already had a
  field for each. Nothing was missing. It is now covered by a test that walks the whole path rather
  than trusting the read.

---

## 5. Caching, in one table

| Where | What is kept | For how long | When it cannot be reached |
|---|---|---|---|
| Cloud | The contributor list from GitHub | 6 hours; a refusal 30 minutes | Empty list |
| Cloud | Each showcase picture's bytes | Until the row is saved again | The kept copy stays |
| A Modbot server | All three lists | 6 hours; a failure 10 minutes | `available: false`, page draws nothing |
| The companion | All three lists, in `credits.json` | 6 hours, and across restarts | The kept copy is shown |
| A browser | One picture, by its address | A year, `immutable` | — |

---

## 6. What is not built

- **No refresh button anywhere.** Six hours is the answer, on both readers.
- **The companion does not show the Libraries or Services tabs** the web app has. Those are a list
  of packages generated at build time for the server and web app, and they are not what somebody
  opens the client's window to read.
- **Cloud does not re-fetch a picture on its own.** An administrator saving the row is the only
  thing that fetches. A VRChat picture that changes goes stale until somebody saves the row, which
  is the same amount of attention the row already needed.
- **No object store.** §2.2.
