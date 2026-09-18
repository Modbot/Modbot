# Modbot — VRChat Files, and Which Picture Is a Person's Face

- **Date:** 2026-09-17
- **Status:** Implemented with this document
- **Covers:** why every face in Modbot went blank (§2); `GET /api/files/vrchat` — who may call it,
  which addresses it accepts, what it refuses (§3–§4); **why it is not rate limited and must not
  be given a bucket** (§4.3); the disk cache, its key and how it is kept under its cap (§5); the
  three new profile columns and the one rule that picks a face (§6–§7)
- **Implements:** the `IVRChatGate.FetchFileAsync` gate call; `Settings.VRChatFileCacheBytes`
- **Related:** foundation §4.1 (nothing builds a VRChat client outside the gate), §4.3.4 (ask
  about the rate limit before using a new endpoint), §4.4 (the clock); evidence storage design
  §3.1, §4.2 (the `/app/data` mount), §10 (serving somebody else's bytes safely); VRChat proxy
  design (the other route that goes to VRChat on a caller's behalf); user profile sync design §4

---

## 1. What this adds

Two things that turned out to be the same problem.

1. **A file route.** `GET /api/files/vrchat?url=…` fetches one picture or video from VRChat on
   Modbot's session and returns the bytes, cached on disk. It exists because a browser cannot
   fetch a VRChat file address at all.
2. **Three profile columns and one rule.** A person's icon, banner and represented group become
   columns of their own, and every screen that shows a face asks one shared rule which of the
   three stored pictures to use.

They are one change because they were one outage: after VRChat's API specification v1.21.0, the
column that fed every face in Modbot stopped being filled, and the field that replaced it cannot
be shown in a browser without a proxy.

---

## 2. Why the faces went blank

Two separate things happened, and they compound.

**`profilePicOverride` left every VRChat call in API specification v1.21.0 (2026-09-16).** It is
on neither `GET /users/{userId}` nor `GET /profile/{userId}` any more, and SDK 2.21.0 no longer
has a property for it. Modbot's `profile_picture_url` column is therefore empty for anyone first
seen after that date and frozen at whatever it held for everyone else. The public profile carries
**`iconUrl`** instead, which is what a person's profile shows today.

**VRChat's file addresses refuse a hotlinking browser.** A stored picture address looks like
`https://api.vrchat.cloud/api/1/file/file_…/1/file`. Fetching it needs the account's session
cookie and answers with a redirect to a delivery host. A browser has neither the cookie nor any
way to be given one — it is Modbot's session, not the moderator's — so an `<img src>` pointed
straight at it draws nothing.

So even once the icon is stored, showing it needs a route.

---

## 3. The route

```
GET /api/files/vrchat?url=<absolute VRChat file address>
```

It answers with the bytes, typed as VRChat typed them.

**Any signed-in person may call it, and no permission gates it.** These files are public on
VRChat's own delivery network to anybody holding the address, and the caller is holding it
already — it is in the profile they just read. Fetching it for them adds no reach, so adding a
permission would only mean a moderator with fewer flags sees a page of broken images.

Both ways of signing in work: the session cookie, because an `<img>` tag sends it, and an API key.

**The API's own answers keep VRChat's real addresses.** A profile's `profilePictureUrl`,
`iconUrl`, `bannerUrl` and `representedGroup.iconUrl` are the addresses VRChat gave, unchanged. A
program holding an API key wants the real one, and so does an MCP tool. Turning an address into a
Modbot address is the web app's job, and it does it in the browser
(`src/Modbot.Web/src/lib/vrchatMedia.ts`).

---

## 4. What it refuses

### 4.1 Addresses that are not VRChat's

`vrchat.cloud` and anything under it, over `https` only. That covers `api.vrchat.cloud`, where a
stored address points, and `assets.`, `files.` and the per-deployment delivery hosts a redirect
lands on — whose names change without notice, which is why the rule is the domain rather than a
list somebody has to maintain. Matched on the label boundary, so `notvrchat.cloud` and
`vrchat.cloud.example.com` are not VRChat. Anything else is a 400, and nothing is sent.

**The redirect target is checked the same way.** This is the part that matters: VRChat answers the
stored address with a `Location`, and a handler that followed it on its own would follow it
anywhere. So the gate follows redirects itself, checking each hop before sending it and checking
the address that finally answered — at most five hops. Without that, the route is an open fetcher
for whatever a caller can make VRChat point at, including this host's own network.

### 4.2 Bytes that are not a picture

Only `image/*` and `video/*`, decided by VRChat's answer and never by anything a caller says.
Anything else is a 415.

**SVG is not a picture here.** It is a document with scripts in it, and one served from Modbot's
own address — which is exactly what a proxy makes it — runs those scripts against a moderator's
session the moment somebody opens the address in a tab. The evidence store refuses it for the same
reason (evidence design §10). Every answer also carries `X-Content-Type-Options: nosniff` and
`Content-Security-Policy: sandbox`, because the bytes inside a file labelled `image/png` are still
somebody else's.

A file over **25 MB** is refused. A profile picture is a few hundred kilobytes; at that size the
address is being used for something else. The cap is enforced on the bytes as they arrive, not on
the `Content-Length` the server claimed.

### 4.3 It is not rate limited, and must not be given a bucket

**VRChat does not rate limit its file and image addresses.** The maintainer confirmed this on
2026-09-17. Foundation §4.3.4 says to ask before using a new endpoint; this is the answer to that
question, written down so nobody has to ask again — and so nobody adds a bucket later *by analogy*
with the endpoints that do have one.

Do not add an endpoint class, a lane, a bucket or a cold stop for these calls. A member list of
forty faces would then queue behind itself, every screen in Modbot would be slower than the thing
it is caching, and a picture that 429ed would cold stop a bucket over a request VRChat never
counted.

It still goes **through the gate** — `IVRChatGate.FetchFileAsync` — and that is not a
contradiction. The gate's rule (foundation §4.1) is not "everything must be paced"; it is that
nothing outside the gate may hold the session, and this fetch needs the session cookie. Going
through the gate is also how the request leaves by the operator's egress proxy, like everything
else Modbot sends VRChat.

---

## 5. The cache

**A VRChat file address names a version, so its bytes never change.** There is nothing to expire,
nothing to revalidate and no moment at which a cached file has gone stale. A file that is here is
the right file for that address for ever.

That single fact decides the whole design:

- **Key:** the SHA-256 of the address exactly as it was asked for, lower-case hex. Every path
  under the folder is therefore hex — no filename, no extension, no segment a caller chose — so a
  traversal would need a new method before it could need a defence. The same key is the **ETag**,
  which is safe for the same reason, and the browser is told `Cache-Control: private, max-age=604800`.
- **Where:** `<data root>/vrchat-files`, sharded `ab/cd/<key>` two levels deep like the evidence
  store. The same `/app/data` mount evidence documents (evidence design §3.1) — it is the same
  disk, and an operator who mounted one volume should not have to mount a second.
- **The type beside the bytes:** `<key>.type` holds what VRChat said the bytes are. A browser
  handed bytes with no type guesses, and guessing is how a picture becomes a script. A stored type
  that today's rules would not serve is treated as a miss.
- **Written then renamed:** bytes go to `<key>.partial` and are moved onto the key, so a crashed
  fetch never leaves half a picture at an address the next request would believe.

### 5.1 The cap, and what goes first

`Settings.VRChatFileCacheBytes`, **2 GB by default**, beside the other storage settings because it
is the same disk. `0` turns the cache off entirely.

When a write puts the folder over the cap, the pictures **fetched longest ago** are deleted until
it is under. A cap rather than an age, because nothing here goes stale — the only reason to delete
any of it is that the disk is not endless, and a cap is that reason written down.

Oldest by when it was fetched, not by when it was last looked at: keeping the popular ones would
need a write on every read, and a write on every read is how a cache becomes slower than the thing
it is caching.

The write time is stamped from `IModbotClock` (foundation §4.4), not taken from the filesystem, so
the order the sweep deletes in is Modbot's own clock. The sweep runs only after a miss has stored
something, so a hit costs nothing but the read.

Failing to cache is never failing to serve: the bytes are already in hand, and every problem
writing them is logged and swallowed.

### 5.2 A demo

A demo has no VRChat account, and its gate refuses everything at the door (demo mode design §3.2).
That needs no check of its own here: a hit is served from the cache, and a miss gets
`NoSession` from the gate and answers **404**. So a demo shows whatever its cache was seeded with
and nothing else.

404 rather than an error is deliberate for every deployment without a session, including one still
being set up. From the browser's side there is genuinely no picture there, and a broken image is a
better answer than a page of red.

---

## 6. The three new columns

On `vrchat_user`, from `GET /profile/{userId}`:

| Column | VRChat's field | What it is |
|---|---|---|
| `icon_url` | `iconUrl` | The user icon the public profile carries. Also on the user object. |
| `banner_url` | `bannerUrl` | The profile banner. Also on the user object. |
| `represented_group_id` / `_name` / `_icon_url` | `representedGroup` | The group the person shows on their nameplate, or nothing. |

`iconUrl` is **not** written to `profile_picture_url`. They are different fields with different
lives — the override is what a person chose and is on no call any more; the icon is what the
profile carries for anybody — and folding one into the other would make a field's disappearance
look like a person changing their picture, for everyone, on one afternoon.

Only the group's id, name and icon are kept. The rest of `representedGroup` — member count,
privacy, the owner — is about the group rather than the person, and stays in `raw_public_profile`.

Presence rules are unchanged and matter here: a snapshot only speaks for the fields its response
carried, so a body without `bannerUrl` leaves the stored banner alone while one that carries it
as `null` clears it.

### 6.1 In the history

A change fact names these under **VRChat's own field names**: `iconUrl`, `bannerUrl`, and
`representedGroup` as **one key** whose `old` and `new` hold the whole `{groupId, name, iconUrl}`
object or `null`. One key rather than three, so a version can name both groups rather than leave a
moderator reading two ids and deciding for themselves which is which.

---

## 7. Which picture is a person's face

One rule, in `Modbot.Core.Users.ProfilePictures.Best`:

> the profile picture override if set, else the icon, else the avatar thumbnail, else nothing.

Newest-first by which field VRChat actually fills. The override is what a person chose and is
frozen; the icon is what the profile carries today; the avatar thumbnail is the oldest and is on
no call for other people any more, but a row read before v1.21.0 may still hold one. A blank
string counts as nothing — VRChat's models give a missing string the value `""`, and a blank must
fall through rather than be shown as a broken image.

Everything that shows a face asks it: the members list, search, the Discord member screens and the
route picker, the profile and every version in its history, and the case-file snapshot. They had
already drifted apart once — the members list fell back to the avatar thumbnail and the route
picker did not — which is the whole argument for one function rather than six agreements.

AutoMod is the deliberate exception. Its picture check sends **every** picture the profile shows —
the override, the icon, the banner and the avatar thumbnail — because a rule about pictures should
look at all of them, not only at whichever one a member list would have picked.

---

## 8. What is not here

- **No settings screen for the cache.** The cap is a column with a default; nothing shows it yet.
  When it earns a control, it belongs beside the evidence storage settings.
- **No sweep on a timer.** The cache is only swept after it is written to, which is the only
  moment it can have grown. A deployment whose cap is lowered stays over it until the next miss.
- **AutoMod still fetches picture bytes with its own client.** `ModerationPictures` downloads
  through a client that refuses private addresses, because it also has to handle attachment
  addresses somebody else wrote. Pointing it at this cache would save VRChat a fetch and is worth
  doing; it is not done here.
