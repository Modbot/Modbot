§0. What this file is

An investigation into the deployed server dying outright and being restarted by Railway, which
returns 502 for the two minutes that takes. The maintainer sees it while paging through Members,
with nothing in the log, and suspects the VRChat file proxy.

**Nothing here proves what caused the crash.** What follows separates what the evidence supports
from what it does not, records four real defects found while reading the path — three of which are
fixed on this branch — and says what would settle the question.

§1. The evidence

§1.1 What was captured

One crash reached Railway's console log:

```
Fatal error.
System.AccessViolationException: Attempted to read or write protected memory. This is often an
indication that other memory is corrupt.
   at System.Collections.Generic.Dictionary`2[[System.Net.Sockets.SocketError, ...],[Interop+Error, ...]]..ctor(Int32, IEqualityComparer`1<SocketError>)
   at System.Net.Sockets.SocketErrorPal..cctor()
   at System.Net.Sockets.SocketExceptionFactory.CreateSocketException(Int32, System.Net.EndPoint)
   at System.Net.Sockets.SocketAsyncEventArgs.TransferCompletionCallbackCore(Int32, System.Memory`1<Byte>, SocketFlags, SocketError)
   at Microsoft.Extensions.Hosting.Internal.Host.ForeachService[...](...)
```

§1.2 What that stack says, read carefully

- **`Fatal error.` is the runtime's own, not an exception the app threw.** It is printed by the
  runtime to standard error and the process ends on the spot. Nothing managed runs afterwards: no
  `catch`, no `finally`, no Serilog flush. "No error in the log" is therefore not a second symptom
  needing its own explanation — it is what this kind of death looks like.
- **`SocketErrorPal..cctor` is a type initialiser.** It runs once, the first time anything in the
  process converts a native socket error into a `SocketError`. So the crash happened on the *first*
  socket error the process ever saw — not after a long slide into a bad state.
- **`TransferCompletionCallbackCore` runs on the socket engine's own thread**, delivering the
  completion of an asynchronous socket operation.
- **The bottom frame is wrong.** `Host.ForeachService` does not call into the socket layer and
  cannot be below `SocketAsyncEventArgs`. The stack walk went off the rails part way down, which is
  itself consistent with the message: memory the runtime needed to read was not what it expected.

Put together: a socket operation completed with an error, the runtime went to build the exception
describing it, and touched memory it should not have. The corruption is not created by that code;
that code is where it was noticed.

§2. What was ruled out, and how

§2.1 Modbot's own code cannot do this directly

`grep` for `DllImport`, `LibraryImport`, `unsafe`, `stackalloc`, `fixed` and `Marshal.` across the
projects that make up the server image (`Modbot.Shared`, `Core`, `VRChat`, `Analytics`, `Evidence`,
`Discord`, `Moderation`, `AI`, `Demo`, `Api`, `Server`) finds two `stackalloc`s of a fixed-size
`Span<char>` and nothing else. Every unsafe block and every native binding in the repository is in
`Modbot.Overlay` or `Modbot.Companion.App`, neither of which is referenced by `Modbot.Server` and
neither of which is in the image. Verified by reading `Modbot.Server.csproj`'s reference graph and
the Dockerfile's publish step.

So no line of Modbot's code writes to memory it does not own. That leaves three ways this could
still be Modbot's doing: a native library loaded into the process, a managed misuse that frees a
native resource early, or the runtime itself.

§2.2 Native libraries in the image

The runtime image adds exactly one package: `krb5-libs`, because Npgsql probes for GSSAPI at
startup and the alpine loader prints an unsuppressable error without it. That means
`libgssapi_krb5.so.2` really is loaded into the process. Nothing else native is added, and no
managed dependency of the server carries a native asset (checked against `Directory.Packages.props`
— the native-carrying packages, `sherpa-onnx`, `NAudio`, `Silk.NET.OpenAL`, `Vortice`, are all
companion or overlay only).

Not ruled out, but nothing points at it: the crash is in the socket layer, not in a database call,
and GSSAPI is exercised at connection time.

§2.3 Freeing a native resource early — the one shape in the repository that can do this

Disposing an `HttpClient` built with `disposeHandler: true`, or an `HttpMessageInvoker`, tears down
the handler's connection pool. Doing that while a request is still reading on it is a genuine route
to socket state being used after it is freed, and the captured stack is exactly the shape that
produces — a completion arriving into state that is no longer there.

Three places in the server did this:

| Place | When it fires |
|---|---|
| `VRChatGate.PassthroughClient` (now `ClientWithoutCookies`) | The operator's egress proxy settings change |
| `VRChatGate.Dispose` | Container shutdown |
| `ReloadableEvidenceStore.Reload` (`src/Modbot.Api/Features/Evidence/ReloadableEvidenceStore.cs:112`) | Evidence storage settings are saved |

**None of them is on the Members path**, and none fires while somebody is paging. The pass-through
client is rebuilt only when the proxy settings change; the gate is disposed only when the host has
already stopped; the evidence store is swapped only when an administrator saves the evidence form.
The maintainer was paging through Members, changing no settings.

So this is a real defect and it is fixed, but **it is not a plausible explanation for this crash**.
It is written up here so that nobody spends the next session finding it again and believing it.

§2.4 The session client is never disposed

`_client` is replaced at `VRChatGate.cs` without disposal in three places (around the `AdoptAsync`,
sign-in and `DropSession` paths). That leaks a handler per replaced session. A leak is the opposite
of a use-after-free: nothing is torn down, so nothing can be used after it. Not a candidate. Left
alone deliberately — fixing it by adding a `Dispose` would *create* the hazard §2.3 describes.

§2.5 So what is left

Everything Modbot does in this path is managed code with no way to corrupt memory, and the one
shape that could does not fire during paging. **The most likely remaining explanation is the
runtime or the platform underneath it, which is what §5 is about.** That is a conclusion by
elimination, not by evidence, and it should be treated as such.

§3. What was actually wrong in the file proxy

Read in full: `src/Modbot.VRChat/Files/*`, `VRChatGate.FetchFileAsync` and everything it calls,
`src/Modbot.Api/Features/Files/VRChatFileCache.cs`, `VRChatFileEndpoints.cs`. Four defects, three
fixed here. None of them can corrupt memory; all of them are real.

§3.1 Fixed — two requests for one picture wrote the same temporary file

`StoreAsync` wrote to `<key>.partial`, a name derived only from the address. Two requests storing
the same address therefore opened and wrote **one file at the same time**, and the first
`File.Move(partial, path, overwrite: true)` renamed whatever mixture existed at that instant onto
the key. Worse, the second writer's file descriptor followed the file through the rename, so it
went on writing into the live cache entry while a third request was reading it.

This is not a rare interleaving. The Members page draws one face per row (`Members.tsx:272`), and
every VRChat user without a picture of their own shares one address, so several rows resolve to the
same URL. A cache miss on that address from two requests at once is the ordinary case.

And it is permanent. The cache's whole design rests on a VRChat file address naming a version, so
there is no expiry and no revalidation (files design §5): a picture corrupted this way stays
corrupted until the sweep happens to reach it.

Fixed by giving every write a name of its own (`<key>.<guid>.partial`), so the only thing two
stores share is the rename, and a rename is atomic.

§3.2 Fixed — a cache hit handed out a path, not the bytes

`Find` returned a path; the endpoint passed it to `Results.File`, which opens it later, when the
response is sent. In between, the sweep can delete the file and another store can rename a new one
onto the name. The window is small and the consequence is a failed response rather than a crash,
but it is a window for no reason.

`Find` now opens the file and hands the open stream over, with share flags that still let a sweep
delete the name underneath it. An open file keeps its bytes whatever happens to the name.

§3.3 Fixed — forty concurrent sweeps

`Sweep` ran at the end of every `StoreAsync`, walking the whole cache folder recursively. Forty
misses meant forty full-tree walks at once, deleting into each other's enumerations — and since
each one measured the folder for itself and then subtracted only what it deleted, together they
would take the folder well below the cap. On a 2 GB cache that is a lot of pointless IO on the
request path.

Now one sweep runs at a time and a store arriving during one carries on rather than queueing. The
next store sweeps. The cap limits how large the folder gets, not what it holds at any instant.

A related hazard the unique names introduced: the sweep deletes anything ending in `.partial` as
rubbish from a dead run. It now checks a set of the files this process is writing first, so it
cannot delete one out from under a store that is filling it.

§3.4 Fixed — the service account's session cookie was going to VRChat's delivery hosts

Raised by the maintainer mid-investigation and correct. `FetchFileAsync` borrowed
`session.Client!.HttpClient`, which carries the SDK's cookie jar. A cookie set on `.vrchat.cloud` is
sent to **every** host under that domain, so the `auth` cookie went to whichever delivery host
VRChat redirects a picture to — on every face in a member list, and on every hop.

Two things made it worse than it looks:

- The session client follows redirects itself. `FetchFileAsync`'s own loop exists because of that
  (its comment names the case: "the address that finally answered, which is not where it was sent
  if the handler followed a redirect itself"), so a hop could be sent, with the cookie on it,
  before Modbot's per-hop allowlist check saw it.
- The jar attaches the cookie without anything in Modbot asking it to, so the rule was invisible.

Now the fetch sends on the client that has no jar at all and follows no redirect, with the `auth`
cookie put on by hand and only when the hop's host is VRChat's API host. Every check is kept: the
allowlist per hop and on the final address, the 25 MB cap, the content types, the User-Agent and
the developer headers. Tests assert the cookie reaches the API host and no hop after it, and that
an address already on a delivery host carries none at all.

**What VRChat actually requires is not established.** The files design (§2) states the stored
address needs the session cookie and answers with a redirect; there is no note in
`.agent/research/` recording an experiment, and none was run here — checking it needs a live
signed-in session against `api.vrchat.cloud`, which this branch has no business doing. The change
keeps the design's assumption and narrows the blast radius to the one host the assumption is about.
If somebody establishes that the first request needs nothing either, the remaining hop can go
cookieless and nothing else has to move.

§3.5 Side effect worth naming

The file proxy no longer shares the session's `HttpClient`. A Members page fanning out now loads
the cookieless client rather than the one every paced API call uses. That is a change in which
connection pool is under concurrent load during exactly the activity that precedes the crash. It is
**not** a fix — there was no evidence the session client was the problem — but if the crash stops
after this deploys, that is a fact worth recording rather than treating as luck.

§4. What is suspected and not proven

§4.1 Memory, and the 25 MB cap

Each in-flight fetch reads the body into a `MemoryStream` and then calls `ToArray()`, so one fetch
peaks at roughly twice the file. The cap is 25 MB, so a pathological page could in principle hold a
lot of large-object-heap allocations at once. For profile thumbnails — a few hundred kilobytes —
this is nothing, and the Members page only ever asks for thumbnails.

Not fixed, because nothing shows it happening, and the obvious mitigation is forbidden: files
design §4.3 says explicitly that these calls must not be given a bucket, a lane or a queue, because
a member list of forty faces must not queue behind itself. A concurrency limit is that, by another
name.

The mitigation that is *not* forbidden is coalescing: when several requests want the same address at
the same moment, fetch once and give them all the answer. It does not slow distinct addresses down
at all, and it is exactly the case §3.1 showed is common. Not built here — it is a feature, not a
crash fix, and this branch should not carry one.

§4.2 The cache on an ephemeral disk

The cap defaults to 2 GB (`Settings.VRChatFileCacheBytes`) and the folder lives under `/app/data`.
On Railway, `/app/data` is **not** a mounted volume unless the operator mounted one — the
self-hosting docs only ever ask for a volume on `/app/logs` or a bucket for evidence. So the deployed
server is free to write up to 2 GB of pictures into the container's own writable layer. Whether that
is near any limit on this deployment is not known from the repository and should be checked on the
service. Running out of space there would produce `IOException`s that the cache logs and swallows,
not a crash — but it is worth knowing.

§4.3 The GSSAPI library

§2.2. Loaded, native, and not otherwise implicated. Named only so the next person knows it is in
the process.

§5. Alpine, honestly

The runtime stage is `mcr.microsoft.com/dotnet/aspnet:10.0-alpine`, so .NET runs against musl
rather than glibc.

**What I can say.** Native faults of this shape are reported more often against musl builds than
glibc ones, and the standard advice when one appears with no managed explanation is to try the
Debian-based runtime image. I searched for a known .NET issue matching this specific stack —
`SocketErrorPal..cctor`, `AccessViolationException`, musl — and **found none**. So the Alpine theory
here rests on the elimination in §2.5 plus a general prior, not on a matching report. That is
weaker than it sounds when somebody repeats it as "it's an Alpine thing".

**What it would cost.** Both stages would move: `sdk:10.0-alpine` → `sdk:10.0` and
`aspnet:10.0-alpine` → `aspnet:10.0`. `apk add --no-cache krb5-libs` becomes an
`apt-get install libgssapi-krb5-2` with the usual lists cleanup. `$APP_UID`, `ASPNETCORE_HTTP_PORTS`
and the `mkdir`/`chown`/`USER` lines work the same on both. The image grows by roughly 60–100 MB.
The `node:24-alpine` web stage is unaffected — it only produces static files.

**What it would break.** Nothing that can be seen from the repository. The publish is
framework-dependent with no `RuntimeIdentifier`, so the output is portable and nothing in it is
musl-specific. The risk is the one that does not show up in a build: this image is what every
self-hoster runs, and changing it changes their image size, their base-image CVE surface and their
`apk`-vs-`apt` habits.

**Recommendation, in order.**

1. **Turn crash dumps on first** (§6) and get one. Switching the base image may simply hide the
   fault rather than fix it, and then nobody ever learns what it was.
2. If a dump confirms the fault is in the runtime or in native code with no Modbot frame involved,
   move the deployed service to the Debian image as an experiment — Railway can run a different
   image than the published one — and watch it.
3. Only change the committed `Dockerfile` once that experiment has held, and say so in the release
   notes, because it changes what ships to everyone.

**I have not changed the base image on this branch.**

§6. What would settle it

The next crash should leave a dump. This branch wires up everything Modbot can do towards that; the
rest is two variables and a volume, and an operator has to set them.

On the Railway service:

1. Mount a volume at `/app/data`. Without one the dump goes with the container, which is the whole
   problem.
2. Set:
   ```
   DOTNET_DbgEnableMiniDump=1
   DOTNET_DbgMiniDumpType=2
   DOTNET_DbgMiniDumpName=/app/data/dumps/modbot.%p.dmp
   ```
3. Redeploy and check the startup line reads `Crash dumps are on and go to /app/data/dumps/…`.
   Modbot makes the folder itself; the runtime does not, and a dump with nowhere to go is silent.

After the next crash: `dotnet-dump analyze modbot.<pid>.dmp`, then `clrstack -all` for every
thread's managed stack and `!threads` for the native ones. What to look for is whether any Modbot
frame appears on the faulting thread. If none does, §5 becomes the answer by evidence rather than by
elimination.

Two smaller things that also now help:

- An unhandled exception on a background thread writes a `Fatal` line and flushes the sinks before
  the process goes. This was previously lost entirely, and it is a *different* way to die that could
  have been mistaken for the same one.
- An unobserved task exception is recorded. It ends nothing, which is why a background loop that has
  silently stopped working used to look like nothing at all.

§7. Changed on this branch

| File | Change |
|---|---|
| `src/Modbot.Api/Features/Files/VRChatFileCache.cs` | Unique temp name per write; type file placed before the bytes; times stamped before the rename; `Find` hands back an open file; one sweep at a time; the sweep does not delete a file this process is writing |
| `src/Modbot.Api/Features/Files/VRChatFileEndpoints.cs` | Streams the open file rather than a path |
| `src/Modbot.VRChat/VRChatGate.cs` | File fetch off the session's cookie jar; `auth` cookie sent to the API host only; no handler-followed redirects; replaced clients retired rather than disposed; test seam for the cookieless client |
| `src/Modbot.Core/Configuration/CrashDumps.cs` | New: reads the runtime's dump variables |
| `src/Modbot.Server/Startup/CrashGuard.cs` | New: unhandled and unobserved exception handlers, and makes the dump folder |
| `src/Modbot.Server/Program.cs` | Wires both in; says on every start whether dumps are on and where |
| `docs/content/docs/self-hosting/railway.mdx`, `environment-variables.mdx` | How an operator catches the next crash |

Tests written (**not run** — see CLAUDE.md, tests and CI come at the end):

- `tests/Modbot.Api.Tests/Features/Files/VRChatFileCacheTests.cs` — the same address stored 24 times
  at once comes back whole with no partial files left; 24 different addresses stored at once each
  come back whole; a file already found stays readable after the sweep takes it. Existing tests
  updated for the open-file result.
- `tests/Modbot.VRChat.Tests/Gate/VRChatGateFileTests.cs` — the session cookie reaches the API host
  and no hop after it; an address already on a delivery host carries none. The handler is now given
  to the gate rather than hung off the session client, since the fetch no longer uses that client.
- `tests/Modbot.Core.Tests/Configuration/CrashDumpTests.cs` — the variables, and the folder a dump
  would need.

`dotnet build -c Release` over the whole solution: clean, no warnings.

§8. Not done, and why

- **No base image change.** §5.
- **No concurrency limit on file fetches.** Forbidden by files design §4.3.
- **No request coalescing.** Worth doing (§4.1), but it is a feature and this branch is a bug hunt.
- **The session client is still never disposed.** §2.4 — disposing it would create the §2.3 hazard
  on the hottest path in the gate. If the leak ever matters, the fix is retirement, the same as the
  cookieless client got, not disposal.
- **`ReloadableEvidenceStore.Reload` still disposes the store it replaced.** Same hazard as §2.3 and
  its comment already acknowledges the window ("as small as it can be made"). Left alone because it
  is a different subsystem and this branch should not sprawl into it; it wants the same treatment.
- **`BytesHeld()` counts `.type` files and the sweep's total does not.** A small inconsistency in
  what the settings screen reports versus what the cap measures. Harmless at the sizes involved, and
  not touched here.
