# The register page's group details, and the Modbot mailing list

- **Date:** 2026-09-17
- **Status:** Draft, awaiting review
- **Covers:** what `my.modbot.co/register` is told and what it believes, what Cloud stores about a
  visited Modbot address, and collecting and leaving the mailing list
- **Depends on:** `2026-09-11-central-services-design.md` (§2, §4.1, §4.4),
  `2026-09-16-cloud-accounts-and-registry-design.md` (§3, §3.5)
- **Related:** the Modbot server's own `GET /api/server` and its account registration tick box

---

## 1. Why this exists

Two things the maintainer asked for, which turn out to share one rule.

1. **The register page should say whose Modbot it is.** Somebody who clicks "Finish setup" lands on
   `my.modbot.co/register` and sees an address. They should see their group — its name, its icon,
   its banner — because that is what tells them the right thing was saved.
2. **A person registering an account on a Modbot can tick a box** to hear from Modbot about new
   features and updates. The address has to go somewhere the project can send from, and that is
   Cloud, not one group's deployment.

The shared rule is that **neither of them may be taken on trust from whoever sent the link**.

---

## 2. The register page

### 2.1 What the link carries

A Modbot server now links to:

```
my.modbot.co/register?url=<server>&groupId=<id>&name=<name>&icon=<url>&banner=<url>
```

`url` is what it always was. The other four are **hints for the first paint and nothing else**.

**Anybody can write that link.** It is a URL in a browser; there is no signature on it and there
cannot usefully be one, because the thing it would be signed with would have to be shared with every
Modbot in the world. So the page draws them, and the moment it has something better it throws them
away:

- The page asks the Modbot itself — `GET <url>/api/server`, which every Modbot answers without a key
  and allows a browser to read — and replaces what it is showing with that answer.
- A Modbot that does not answer leaves the hints on screen. They are a name and two pictures, they
  are saved nowhere, and a blank card would be the wrong answer for a server that is merely slow.
- The page never reads `ownerEmail` out of that answer. The operator's address is no business of a
  browser, and a browser that holds it is a browser that can leak it.

**The four parameters never leave the browser.** They are not posted to `my.modbot.co`, not passed
to Cloud, and not stored. A forged link can mislead one page for a fraction of a second and can do
nothing else at all.

### 2.2 What my.modbot.co believes

my.modbot.co asks the same question again, server-side, before it saves anything:

1. A page load with a `url` notes the visit as it always has (central services §2.3.1), and — in the
   background, so no page ever waits — asks `GET <url>/api/server`.
2. `POST /api/local-register` saves the visit and then sends the group details on, reading the answer
   the page load already got rather than asking again.
3. **What the server answered is what is saved.** If it did not answer, only the address is saved,
   exactly as before this existed. There is no path by which a group detail that a server did not
   say about itself is stored anywhere.

It asks on whichever route noted the visit — `/`, `/register` and `/go` — rather than on `/register`
alone. It is the same address either way, and a group name on the list at `/` is worth having for a
Modbot that never registered itself with Cloud.

**Asking is a request to somewhere a stranger named**, which is the shape of every server-side
request forgery there has ever been. Cloud refuses to make such a request at all (Cloud registry
§3.3: "Cloud never calls a Modbot server back"). my.modbot.co makes it, because it has to, and pays
for it with a client of its own:

| | |
|---|---|
| Timeout | 4 seconds |
| Redirects | not followed, at all |
| Response read | at most 64 KB, and only then parsed |
| Addresses | only addresses out on the public internet — never this host, a private network, carrier-grade NAT, or the link-local range where cloud metadata services live |
| Remembered | an answer for 10 minutes, a failure for 1 minute, so the two saves of one page view ask once |

The address check is made on the address actually connected to, not on the name, because a name
resolves to whatever its owner likes and can resolve to something else a moment later.

**my.modbot.co holds nothing itself.** It has no database (central services §2.1.1) and this does not
give it one: the answer is remembered in memory for a few minutes and passed to Cloud.

### 2.3 What the page shows

The banner across the top of the card, the icon and the group name beside the address, and the
buttons that were already there. No new sentence explains any of it.

---

## 3. What Cloud stores

### 3.1 `POST /api/v1/site/servers`, not a block on a visit

Two candidates, and the reason for the choice is that the two facts have different rules:

| | A visit | What a server says it is |
|---|---|---|
| About | one page view by one visitor | one Modbot address |
| Counted | yes — once per address per five minutes | never; it is replaced by the newest answer |
| Saved when nothing else is known | yes, always | there is nothing to save |
| Arrives | with the page load and again after it renders | when the address answers, which may be later or never |

Folding the details into `POST /api/v1/site/visits` would have tied a fact that must be saved
immediately and counted exactly once to a fact that arrives when it arrives. It would also have
changed the shape of an endpoint that an older `my.modbot.co` is still calling. So: a second
endpoint, behind the same `PROXY_API_KEY`, with rules of its own.

### 3.2 `visited_server`, and who wins

The table is `visited_server`: the server address (the key), group id, group name, icon URL, banner
URL, owner email, first seen and last seen.

It is **kept apart from `registered_server`**, for the same reason `page_instance` is (central
services §4.1): a report arrives over a connection the server opened carrying its own secret, and
this arrives because somebody opened a link and my.modbot.co asked whatever answered at that
address.

**Precedence: the registry wins, field by field.**

- A registered server that reported a group name is the name shown, even where a visit learned a
  different one.
- Where the registry has nothing — a server that has not finished setting up, or one running with
  `MODBOT_CLOUD_DISABLED=1` and therefore never in the registry at all — the visit's answer is used.
- Field by field rather than row by row, because a half-set-up server has an address and no group,
  and a name learned from a visit is better than no name.
- A visit can never overwrite a registry row. Nothing writes to `registered_server` from here.

Within `visited_server` itself, a null in a new answer leaves what is stored alone — the same rule a
server's own report follows, so that a Modbot that has not chosen a group yet cannot blank out what
an earlier visit learned.

### 3.3 Where it shows up

- `GET /api/v1/site/visits` — the instance list on `my.modbot.co` falls back to the visit-learned
  group name and icon where the registry has none. Its shape does not change.
- `/admin` — the address list (`GET /api/admin/page-instances`) carries the group and the owner
  email, and the server list (`GET /api/admin/servers`) carries the owner email for the matching
  address.

### 3.4 The owner's address

Stored for one reason: so that the maintainer can reach whoever runs a deployment.

**It is never returned by any endpoint that a visitor, a page or another service can reach.** Not by
anything under `/api/v1/site`, not by `ServerView` — which a signed-in account reads about its own
servers — and not by the register page. It is on `AdminServerView` and `AdminPageInstanceView`, both
of which need the admin cookie or `ROOT_API_KEY`. It is not written to a log line: a failure to ask
an address logs the address and never the answer.

---

## 4. The mailing list

### 4.1 Subscribing

`POST /api/v1/subscribers` takes `{ "email": "…", "source": "…" }`.

**Called by a Modbot server, not by a browser**, when somebody ticks *Receive emails from Modbot
about new features and updates* while registering their account. It carries the server's own
registry credential — the same `Authorization: Bearer <serverId>.<secret>` its reports and its log
batches carry — **when it has one**. A server that runs with `MODBOT_CLOUD_DISABLED=1` never shows
the tick box at all, which is the correct behaviour for an optional service.

**A credential is not required, and that is deliberate** (changed 2026-09-17, before either side
shipped). The commonest time this box is ticked is while the *first* account on a brand-new Modbot
is being made, which is before that deployment has registered with Cloud and therefore before it has
a credential to send. Requiring one would refuse precisely the opt-ins this endpoint exists to
collect. So the endpoint takes an unauthenticated call, and the per-caller limit keys on the calling
address instead of the server id. A caller that does hold a credential is still recognised and is
limited by server, which is the stronger key of the two.

- **One row per address**, trimmed and folded to lower case. Ticking the box again — on the same
  Modbot or another one — moves `last_seen_at` and adds nothing.
- **Re-subscribing clears an earlier leaving.** Somebody who ticks the box after unsubscribing is
  asking again.
- **Validation is loose**: an `@` with something either side, a dot in the domain, no spaces. The
  same rule accounts use, and for the same reason — an address is checked by sending mail to it, and
  every clever pattern turns away somebody's real address.
- **Limits: 60 an hour per caller — the server id when it sent one, otherwise where the call came
  from — and 5 an hour per address.** Both, because either alone
  leaves Cloud usable as a way to mail a stranger: per server stops one deployment pouring in a
  list, per address stops the same address being pushed in from many deployments.
- The answer is always `202`, whether or not the address was already there. Anything else would be a
  way to ask whether a given person is on the list.

The row holds the address, where the box was ticked, an unsubscribe token, and the two times.
Nothing else: no name, no account, no link to a server or a moderation record. A mailing list is a
mailing list, and anything else kept beside it turns one into a profile.

### 4.2 Unsubscribing

**Built here, because the Modbot server deliberately does not.** A list nobody can leave is worse
than no list, and the person who wants out has an address and a link, not an account.

- `GET /unsubscribe?token=…` is a page on Cloud. It needs no account and asks nothing.
- The page posts the token to `POST /api/v1/subscribers/unsubscribe`, which marks the row. A **POST**
  rather than the page load itself doing the work, so that a mail scanner fetching every link in a
  message takes nobody off the list.
- Clicking the link twice is the same as clicking it once.

**The token is random and stored, not a signature.** A signature would need a key that must never
change and must be set at all; a Cloud whose key was rotated, or never configured, would have a
mailing list whose links were all dead — the one failure a mailing list must not have. Thirty-two
random bytes against the row cannot be forged either, and deleting the row takes its link with it,
which is the right answer too.

### 4.3 Reading the list

`GET /api/admin/subscribers` and `GET /api/admin/subscribers/counts`, both behind the admin sign-in.
A list of email addresses is exactly the thing that must not be readable with a key handed to another
service, so `PROXY_API_KEY` opens neither. `/admin` has a **Subscribers** screen: who is on the list,
where each ticked the box, the counts, and who has left.

### 4.4 Not in scope

**Sending a newsletter.** Nothing here sends anything. This collects addresses and lets people go;
whatever sends to them later is a separate piece of work, and will use the stored unsubscribe link.

---

## 5. Open questions

1. **Whether a person should be able to see or change their own subscription** without the link from
   a message. It would need an account, which is exactly what §4.2 avoids.
2. **Whether `visited_server` rows should age out.** An address nobody has opened in a year is
   probably not worth keeping, but a group name is also the only thing that makes an old entry
   recognisable.
3. **Whether my.modbot.co should ask an address it has never been shown a group for**, on a schedule,
   rather than only when somebody opens a page for it.
