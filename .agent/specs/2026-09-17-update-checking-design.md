# Modbot — Update checking

- **Date:** 2026-09-17
- **Status:** Implemented with this document
- **Covers:** where the server and the client ask what the newest release is; what Cloud fetches
  from GitHub and from Docker Hub and how it caches it; the rate-limit reasoning; what is and is
  not sent; what happens when the Cloud features are turned off; why the server never updates
  itself
- **Implements:** central services §3 (the release host), foundation §2.7.1 (the calendar version)
- **Related:** M3 §9 (client updates), Cloud accounts and registry design (Cloud's other
  endpoints), central services §1 (the governing rule)

---

## 1. What this adds

Before this, the client checked GitHub's releases directly and the server checked nothing at all.
A server told an operator its own version on the Deployment card and had no idea whether it was
the newest one.

After this there is one place that knows what the newest release of each thing Modbot ships is —
**Modbot Cloud** — and both halves ask it:

| Who | Asks | How often | What it does with the answer |
|---|---|---|---|
| The client | `cloud.modbot.co/api/v1/updates/companion/releases.{channel}.json` | 30 seconds after start, then every 4 hours | Downloads the newer version from GitHub, installs it at the next start |
| The server | `cloud.modbot.co/api/v1/updates/server` | 1 minute after start, then every 6 hours | Shows a line on the Settings screen. **Nothing else.** |

## 2. Why Cloud and not GitHub

GitHub's API allows 60 unauthenticated requests an hour per address. That is fine for one client
on one machine and it is nothing like enough for a few thousand of them, and it is the wrong shape
anyway: every one of those requests asks the same question and gets the same answer.

Cloud asks GitHub once every fifteen minutes, with a token, and serves what it learned out of
memory. Ten thousand clients and a thousand deployments cost GitHub four requests an hour between
them.

The second reason is that the server had no feed at all. A Docker image's newest tag is not
something a running container can work out for itself, and pointing every deployment at Docker
Hub's API would repeat the same rate-limit mistake one registry along.

### 2.1 This is not a Cloud feature

**A Modbot with `MODBOT_CLOUD_DISABLED` set still asks.** That variable turns off the things a
deployment *tells* Cloud and the things it *fetches for its own working*: usage reporting, the
structured app logs, the term lists, the public instances listing. Knowing that a newer version
exists is none of those. It is what every piece of software does, it sends nothing about the
deployment, and the answer is identical for everybody.

Bundling the two would mean the deployments least in touch with the project — the ones whose
operators deliberately cut every optional tie — were also the only ones never told about a
security fix. That is exactly backwards.

The operator who wants no outbound calls at all has a switch of their own (§6), it is on the
settings row rather than in the environment, and it defaults to on.

In the code this is a separate type, `ModbotUpdateAddress`, which reads the same
`MODBOT_CLOUD_ENDPOINT` as `ModbotCloudAddress` and deliberately does not carry its `Disabled`
flag. Anything that reaches for `ModbotCloudAddress` gets the flag; update checking cannot reach
for it by accident.

## 3. What Cloud serves

### 3.1 The endpoints

All three are **public and unauthenticated**.

```
GET /api/v1/updates
GET /api/v1/updates/{name}
GET /api/v1/updates/companion/releases.{channel}.json
```

`GET /api/v1/updates` answers with every thing Modbot ships:

```json
{
  "releases": [
    {
      "name": "companion",
      "version": "2026.9.4",
      "publishedAt": "2026-09-17T10:00:00+00:00",
      "notesUrl": "https://github.com/binn/Modbot/releases/tag/companion-v2026.9.4",
      "image": null,
      "tag": null,
      "imagePushedAt": null
    },
    {
      "name": "server",
      "version": "2026.9.3",
      "publishedAt": "2026-09-16T10:00:00+00:00",
      "notesUrl": "https://github.com/binn/Modbot/releases/tag/host-v2026.9.3",
      "image": "modbot/modbot-host",
      "tag": "2026.9.3",
      "imagePushedAt": "2026-09-16T10:30:00+00:00"
    }
  ],
  "checkedAt": "2026-09-17T12:00:00+00:00"
}
```

`GET /api/v1/updates/{name}` is one entry of that list, and 404 for a name Modbot does not publish.

**Names come from the release tags**, which is what keeps this list correct without anybody
maintaining it. A tag is `<prefix>-v<version>`: `host-v2026.9.3` and `companion-v2026.9.4` today,
and anything else the project starts tagging appears under its own prefix on its first release.
`host` is served as `server`, because that is the word everything else in Modbot uses for it.
Drafts and pre-releases are left out: a pre-release is somebody's trial balloon, and telling every
deployment in the world to move to one is not what publishing it meant.

### 3.2 Where Cloud gets it

**GitHub**, `GET /repos/{owner}/{name}/releases?per_page=100`, with a token from
`GITHUB_TOKEN` — the same variable and the same shape of call the Credits page's contributor list
already uses. The repository is `GITHUB_RELEASES_REPOSITORY`, falling back to `GITHUB_REPOSITORY`
and then to `binn/Modbot`. It is separate from the contributors' repository because the client's
releases may be published somewhere the public can download them from while the source repository
is private, which is what `CLIENT_RELEASES_REPO` in the release workflow is for.

**Docker Hub**, `GET /v2/repositories/{owner}/{name}/tags`, unauthenticated, for the server image
named by `DOCKER_IMAGE`. GitHub says which version was released; Docker Hub says whether an image
an operator can actually pull is there under that version and when it was pushed. The two are
published by different workflows and can be minutes apart, so Cloud asks both rather than
assuming one from the other. When Docker Hub cannot be read the answer still names the image and
the release version as its tag — which is what an operator would type anyway — and simply omits
`imagePushedAt`.

### 3.3 The client's feed

Velopack 1.2.0's `SimpleWebSource` asks its base address for `releases.{channel}.json` — `win` or
`linux`, the two the release workflow publishes — and reads a `VelopackAssetFeed`: an `Assets`
array of `PackageId`, `Version`, `Type`, `FileName`, `SHA1`, `SHA256`, `Size` and the notes. Each
asset's `FileName` is resolved against the base address **unless it is already an absolute
address**, in which case it is followed as it stands.

That last sentence is the whole design. Cloud serves **the file the release workflow published**,
with each package's `FileName` replaced by the address it downloads from on GitHub. So:

- Cloud serves a few kilobytes of index and GitHub serves the hundred-megabyte packages. No
  download passes through Cloud and Cloud pays no bandwidth for updates.
- Every checksum in the feed is GitHub's, carried through untouched. Cloud is not in a position
  to change what a client verifies its download against.
- A package attached to an older release still resolves: Cloud builds the name-to-address map over
  every release it read, not just the newest, because a client that skipped six versions is
  offered a delta chain reaching back through them.

The channel is checked against a short pattern (letters, digits and dashes, at most 32) before it
is looked up, and an unpublished channel is a 404.

### 3.4 Caching, and the rate limit

**A request never causes a fetch.** A background loop refreshes every **15 minutes**; a request
reads whatever is in memory. The one exception is a cold start: the first reader kicks off the
first fetch and waits for it, because the alternative is a freshly deployed Cloud answering
nothing for a quarter of an hour. A failed cold fetch is not retried for a minute, so a GitHub
outage at the moment Cloud starts cannot turn every arriving request into another request to
GitHub.

A refresh costs **three GitHub calls** — one list, plus one read of each channel's feed file — and
one Docker Hub call. At four refreshes an hour that is about 300 GitHub calls a day against a
5,000-an-hour limit.

Responses carry `Cache-Control: public, max-age=300`. Short against the refresh interval, so a
deployment behind a cache still hears about a release the same day; long enough that a retry loop
somewhere cannot become traffic.

**A failed fetch serves the last known answer.** GitHub being down, rate-limited or slow must not
turn into every Modbot in the world being told nothing; the previous answer is hours old at worst
and still true. Only a **cold cache with a failed fetch** is an error, and it is a `503` rather
than an empty list — an empty list reads as "you are up to date", and that would be a lie.

## 4. What is not sent, and not kept

**Nothing about the caller is required, recorded or logged** beyond the one request line every
request to Cloud gets. No install id, no deployment id, no account, no version echoed back into
storage, no row written anywhere. There is no authentication because there is nothing to
authenticate: the answer is the same for everybody, which is also exactly what lets one cached
copy serve the world.

Velopack attaches `arch`, `os`, `rid`, `id` and `localVersion` to its feed request. Cloud
**ignores all five** — the same file is served whatever they say — and they do not reach the log
either, because Cloud's request line records the path and not the query string.

This property is not new and this document does not get to change it. The client documentation has
always promised that checking for updates "does not identify you or your install", back when the
feed was GitHub. Moving the question to a service the project operates makes the promise matter
*more*, not less, and it is what the `NothingAboutTheCallerIsNeeded` test exists to hold.

What Cloud and GitHub do see is an IP address, as any site does for any request. That was true of
the GitHub feed too and is said plainly in the client's own documentation and in `Updates.cs`.

## 5. The server

### 5.1 Asking

`UpdateCheckService` runs one minute after start and every six hours after that. One minute rather
than the reporting loop's two: the answer belongs on a screen an operator may well open in the
first minute after a deploy.

Every failure is written at **debug**, never warning or error — the same rule as
`ServerReportingService`, and for the same reason: an operator whose logs fill with errors because
somebody else's service is down would reasonably conclude their install is broken. The settings
screen is where a failed check is shown, because that is where somebody has gone to look.

The call goes through the ordinary HTTP client, never the VRChat gate and never the egress proxy.
Cloud is not VRChat, so a call to it must not spend a VRChat rate-limit budget or leave by an
address an operator set aside for VRChat.

### 5.2 Comparing

Versions are `YYYY.M.PATCH` with unpadded components (foundation §2.7.1), so they are compared
**one number at a time**. Sorting the strings puts `2026.1.10` before `2026.1.2`, which would tell
an operator on the newer version to update to the older one. `ReleaseVersion` is the one place
that comparison lives.

Three answers, and only the first is an update:

- the newest release is **later** than what is running — say so, and say what to pull;
- it is the **same** — say nothing beyond the version;
- it is **earlier** — say nothing. Running a build from master, or a release that was rolled back,
  is not a reason to be offered a downgrade.

Anything that does not parse as three numbers — a development build somebody renamed, a tag with a
suffix — is never newer. Telling an operator to update on the strength of a version nobody can
read would be worse than saying nothing.

### 5.3 Storing

The answer is written on the settings row: `NewestRelease`, `NewestReleaseAt`,
`NewestReleaseNotesUrl`, `NewestReleaseImage`, `NewestReleaseTag`, `UpdateCheckedAt`,
`UpdateCheckProblem`. The settings screen reads what is stored and never asks Cloud itself, so
opening a page cannot turn a screen refresh into a request to somebody else's service.

**A failed check keeps what was last known.** "We could not ask today" is not "there is no newer
version", and the screen must not say the second when it means the first. The failure goes in
`UpdateCheckProblem` and the version that was last learned stays where it was.

### 5.4 Showing

An **Updates** card under **Settings → Host & Database**, beside the Deployment card that already
names the running version:

- **Running** — this server's version
- **Newest** — what Cloud said
- **Pull** — `image:tag`, only when there is a newer release
- **Last checked**
- **Check for updates** — the switch (§6)
- **Release notes** — a link, only when there is a newer release

`GET /api/settings/updates` is the same thing as JSON, for the health or settings screens, behind
the `ManageSettings` permission like every other settings endpoint.

## 6. The operator's switch

`Settings.CheckForUpdates`, on by default, changed through `PUT /api/settings/updates` and
recorded in the audit log as a settings change like any other.

It is a setting rather than an environment variable because it is a preference an operator can
reasonably change on a Tuesday, and foundation §2.6 puts those in the database. The environment is
for the three things needed *before* the database is reachable, and this is not one of them.

Turning it off **clears what was last learned** as well as stopping the loop, so a screen never
shows an answer from a check the operator has since forbidden. Turning it back on asks again at
the next pass.

`MODBOT_CLOUD_DISABLED` does not reach this switch in either direction (§2.1).

## 7. The server never updates itself

**Modbot says what exists. It never pulls an image, never restarts itself, never writes to a
deployment platform, and there is no setting that makes it.**

A server that swapped its own code would be a server whose operator cannot say what is running,
changed at a moment nobody chose. Updating a Modbot means running a newer image against the same
database, and the new version changes the database's tables when it starts (see the Updating
page): that is a step to be taken deliberately, after a backup, by somebody who can watch it. It
is also not Modbot's to take — the container is the host's, and on Railway or Docker Compose
Modbot has no standing to replace it even if it wanted to.

The client is the opposite case and updates itself on purpose: it is one person's desktop program,
it has no database to migrate, and it installs the new version at the next start rather than under
a running session (M3 §9).

## 8. If Cloud disappears

§1 of the central services spec holds: Modbot must be completely functional with zero contact to
any project-operated service.

- A server that cannot reach Cloud shows the version it last heard of and a line saying the last
  check failed. Nothing else about the deployment changes.
- A client that cannot reach Cloud writes `Could not check for updates` to its log and tries again
  in four hours. It keeps running the version it has, and the releases are still on GitHub where
  anybody would look for them; running the newest installer over an existing install is still a
  complete update path.
- A fork or a group that mirrors the feed points its clients elsewhere at build time
  (`ModbotUpdateFeed`, or the `CLIENT_UPDATE_FEED` repository variable in the release workflow),
  and the code does not care which it is: an address on github.com is read as GitHub releases, and
  anything else as a plain folder of release files — which is the shape Cloud serves.

## 9. What this does not do

- **No channels beyond what the workflow publishes.** Central services §3.4 imagined `stable` and
  `beta`; the release workflow publishes `win` and `linux` and nothing is opted in to anything.
  When there is a beta channel, it is another file in the same release and this serves it without
  a change.
- **No `apiVersionMin`/`apiVersionMax` in the feed.** Central services §3.3 wanted the client to
  pick the newest release its server's API version supports. Servers speak a range and have never
  dropped a version, so every client can talk to every server and the newest release is always the
  right one. This stays out until that stops being true.
- **No notification.** Nobody is emailed or pinged about a release. The line is on a screen an
  operator already visits; an alert about somebody else's publishing schedule is not an incident.
- **No signature checking beyond Velopack's.** The packages are signed in CI and the client
  verifies what it downloads against the checksums in the feed, which are GitHub's own and pass
  through Cloud unchanged (§3.3). Central services §3.2 is unaffected: compromising whatever
  serves the index still yields nothing.
