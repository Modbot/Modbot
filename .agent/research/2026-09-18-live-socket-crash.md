§0. What this file is

A second look at the deployed server dying outright and being restarted by Railway, after the
first investigation (`2026-09-18-file-proxy-crash.md`) blamed the VRChat file proxy and was wrong.

This one was asked to look at the live updates WebSocket, because the socket is opening and
closing repeatedly right before the crash. **It is not the socket either.** What it is, is a burst
of about forty-five simultaneous requests arriving in one go, and the process dying inside a tenth
of a second of them landing. That much is now established from Railway's own edge log rather than
inferred. What kills it under that burst is still not established.

Nothing here is a fix for the crash. It moves the question from "which piece of Modbot's code is
at fault" to "what falls over when forty-five connections arrive at once", which is a different and
narrower question, and it says what would answer it.

§1. What the evidence now shows

§1.1 The crash is instantaneous and it coincides with a burst

Railway keeps three log streams. The first investigation read the deploy stream. The **http**
stream — the edge proxy's record of every request, with the upstream error on the ones that failed
— had not been read, and it is where the answer is.

The last crash, 2026-09-18 19:44:30 UTC, deployment `d71ddc54`:

| Time (UTC) | What |
|---|---|
| 19:44:29.950 | `GET /api/members` → 200 in 44 ms |
| 19:44:30.012 – .016 | 8 × `GET /api/files/vrchat` → 404, each in 7–12 ms |
| 19:44:30.072 – .089 | 36 × `GET /api/files/vrchat` → 502, plus two font files |
| 19:44:31.567 | `GET /api/members` → 502 |
| 19:44:34.959 | `GET /health/ready` → 200; the new process is up |

Every one of those 502s carries an upstream error from Railway's edge, and the first attempt of
each says the same thing:

```
"error": "connection closed unexpectedly", "duration": 63
```

followed by two retries that both say `connection refused`. That is not a slow server or a
refusing server. It is a server that **accepted the connections, held them for about 60 ms, and
then stopped existing**, after which nothing was listening on the port at all.

So the process died at roughly **19:44:30.07**, which is between 50 and 70 milliseconds after a
page of about forty-five faces asked for its pictures all at once.

§1.2 The earlier crash has the identical signature

The 06:11 crash, deployment `38bb2800`, which is the one that left the `AccessViolationException`:

```
06:11:51.319  GET /api/files/vrchat  502  "connection closed unexpectedly"  duration 93
06:11:51.320  GET /api/files/vrchat  502  "connection closed unexpectedly"  duration 94
   … about thirty more, all /api/files/vrchat, all cut at 94–99 ms …
06:11:51.342  GET /api/live/ws       0    (no upstream error)
```

Same route, same burst, same "closed unexpectedly at about 95 ms". **Two for two.**

§1.3 The live WebSocket is a bystander

It is present at both crashes because it is present on every page: the web app holds one live
connection for the whole tab (`src/Modbot.Web/src/lib/useLiveStream.ts`), and every page draw that
remounts the subscriber drops it and opens another. Paging through Members quickly therefore
produces exactly the open/close pattern in the deploy log — four in twenty seconds — for the same
reason it produces the request bursts: each page draw is a new set of both.

One number in the brief that pointed at the socket does not mean what it looks like.
`GET /api/live/ws responded 101 in 9886.8 ms` is `UseSerilogRequestLogging` reporting how long the
**whole request** took, and a WebSocket request lasts as long as the connection. It is a
connection that lived for 9.9 seconds, not an upgrade that took 9.9 seconds. At the 19:44 crash
the equivalent figure was 7.8 s, and at 06:11, 0.5 s. There is no ten-second anything.

§1.4 It is not memory, not processor, and not a stop the platform asked for

Read from the Railway service (`Modbot`, project `hopeful-motivation`, production), 24 hours at
60-second sampling:

| Measurement | Limit | Peak | Average |
|---|---|---|---|
| Memory | 32 GB | 0.53 GB | 0.29 GB |
| Processor | 32 | 0.68 | 0.017 |
| Disk | — | 0 GB | 0 GB |

An out-of-memory kill would need a burst from 0.5 GB to 32 GB inside one sampling gap, on a page
of forty-five requests that each read one settings row and answer 404. That is not credible, and
memory exhaustion should be treated as ruled out rather than merely unlikely.

Nor was it asked to stop. A SIGTERM shutdown is loud — `Microsoft.Hosting.Lifetime` writes
"Application is shutting down…" at Information, and the "Application started" line from the same
logger does appear on every boot, so that category is not being filtered out. There is no shutdown
line before either crash.

§1.5 There was no VRChat fetch during the crash

This is the fact that undoes the first investigation's theory.

Those forty-five requests answered **404 in 3 to 12 milliseconds**. The only ways
`VRChatFileEndpoints.ServeAsync` can answer 404 that fast are the operator's switch being off
(`src/Modbot.Api/Features/Files/VRChatFileEndpoints.cs`, the `VRChatImagesProxied` check, which is
the first thing the handler does) and `VRChatFileOutcome.NoSession`. The second is contradicted by
the deploy log, where VRChat calls are succeeding in the same seconds
(`VRChat GetGroupInstances groups.instances -> 200 in 45 ms` at 19:44:26).

So with the switch off, each of those requests is: authenticate, read one settings row, return a
404 JSON body. No cache, no disk, no `HttpClient`, no VRChat. **Nothing on that path can corrupt
memory or end a process**, and the server dies on it anyway.

That is a much stronger statement than the first investigation's §2.5, which reached "probably not
Modbot" by eliminating everything else. This reaches it by watching the crash happen on a code path
that does almost nothing.

§1.6 An open question that is not the crash

With the switch off, the browser should not be asking at all. `vrchatMedia()`
(`src/Modbot.Web/src/lib/vrchatMedia.ts`) returns the raw VRChat address rather than a Modbot one
when `proxied` is false, and `App.tsx` sets that from `vrchatImagesProxied` on the onboarding
status, which it reads before anything draws. The deployed commit has all of that
(`b829dc00`, 2026-09-17, is an ancestor of the deployed `f3aeb675`).

Yet forty-five proxy requests went out, on a full page load where the status came back at
19:44:22.088 and the faces were asked for at 19:44:22.26 — well after.

Either the switch is actually on and something else explains 404s in 3 ms, or the switch is off and
the browser is ignoring it. **Not settled here**, and it needs the deployment to settle it: read
`Settings.VRChatImagesProxied`, and watch the Members page in a browser's network tab. It matters
because a switch that does nothing is a bug in its own right, and because turning it off is what
the maintainer did to test the proxy theory — if it never took effect in the browser, that test
proved less than it seemed to.

It does not change §1.5: the requests were answered in 3 ms either way, so whatever the switch
says, no file was fetched from VRChat during the burst.

§2. What was ruled out about the live socket, and how

The brief asked five specific questions about `LiveSocketSession`. All five were read through; none
of them is the crash.

§2.1 Can the request end while a receive is still pending on the socket?

**No.** `LiveSocketSession.RunAsync`'s `finally` awaits `receiving` unconditionally before it
returns, and a `finally` runs however the `try` ended. The two-second wait above it
(`Task.WhenAny(receiving, Task.Delay(2s))`) can indeed proceed with the receive loop still running
— that is what the brief asked — but the `await receiving` three lines later still waits for it. By
the time `RunAsync` returns, the receive loop has finished, and only then does
`LiveStreams.RunSocketAsync` return and the endpoint's `using var socket` dispose the WebSocket.

Nor can the receive loop hang there. Every wait inside it is bounded: `ReceiveAsync` takes the
session token and cancelling it aborts the socket; `CloseAsync` gives up after two seconds; a send
gives up after `SendTimeout`.

The one real gap was that `await receiving` only caught `OperationCanceledException` and
`WebSocketException`. Anything else — `ObjectDisposedException` from a socket aborted under a
receive is the plausible one — would have thrown **out of the `finally`**, which loses the
"Live connection … closed" line entirely and replaces the reason the connection ended with nothing.
Fixed on this branch in both sessions, and it is a diagnosability fix, not a crash fix: the
exception would still have been logged by ASP.NET, and the crash leaves no exception at all.

§2.2 A second connection opening while the first is still closing

**Harmless.** The two sessions share nothing: each has its own `WebSocket`, its own linked
cancellation source, its own locks and queues. The only shared thing is `EventConnections`
(`src/Modbot.Api/Features/Events/EventTickets.cs`), a count per caller behind a lock, and the count
is given back in `RunSocketAsync`'s `finally`, which always runs. A second connection arriving
during the first's teardown legitimately counts two of the caller's five places for a moment, and
five in twenty seconds never reaches the cap.

Tested on this branch — see §4.

§2.3 Anything subscribed or registered that outlives a session

**Nothing.** There are three candidates and all three are clean:

- `FactSignal` (`src/Modbot.Analytics/Facts/FactSignal.cs`) hands back a `Task` from a
  `TaskCompletionSource` that is swapped out on each pulse. A waiter registers nothing; it holds a
  task. A waiter that goes away leaves no trace.
- `EventConnections` is released in a `finally`, as above.
- `EventTickets` removes a ticket when it is redeemed and sweeps expired ones on each issue.

§2.4 Unobserved tasks

The receive loop is the only task started and not immediately awaited, and it is awaited in the
`finally`. `CrashGuard.Watch()` is installed in `src/Modbot.Server/Program.cs` before anything else
that could throw, and would have written an `Error` line for an unobserved task and a `Fatal` line
for an unhandled one. Neither appears. `grep` for `async void` across every project in the server
image finds none.

§2.5 The events socket

`EventSocketSession` is the same code with a different payload, including the same `finally`, and
so has the same §2.1 gap and the same fix. Neither socket is implicated in the crash.

§3. What is left, and what would settle it

§3.1 The shape of what remains

The process is killed, without a managed exception, within about 60–95 ms of roughly forty-five
connections arriving at once, twice, on a request path that touches one database row. `CrashGuard`
runs on unhandled exceptions and produced nothing, so nothing managed threw. The runtime prints
`Fatal error.` for a fatal runtime error and did print one at 06:11 — so that channel reaches
Railway's log — and printed nothing at 19:44.

That leaves, in rough order of how well it fits:

1. **A fault below managed code, in the socket or accept path, under concurrent connections.** The
   06:11 stack is exactly that: `SocketAsyncEventArgs.TransferCompletionCallbackCore` →
   `SocketExceptionFactory.CreateSocketException` → `SocketErrorPal..cctor`. A type initialiser
   means it was the **first socket error the process had ever seen**, which is what a burst that
   overruns some limit would produce, and it faulted building the exception that describes it.
   The image is `aspnet:10.0-alpine`, so this is .NET against musl.
2. **The database connections behind those requests.** Forty-five simultaneous requests are
   forty-five simultaneous scoped `ModbotContext`s, each reading the settings row, plus whatever
   authentication reads. If Postgres or the Npgsql pool refuses at the socket level, that is a
   socket error — and (1) says the crash is in the code that turns one into an exception.
   **Not checked here**: the pool size in use, and `max_connections` on the Postgres service.
3. **A kill from the platform with no signal.** Possible, and nothing in the record supports or
   refutes it. §1.4 rules out the usual reason for one.

**None of these is proven.** (1) and (2) are one hypothesis wearing two hats and they can be tested
together.

§3.2 What would capture the next one

The crash still leaves nothing, and the reason is on the startup line of every boot:

```
[19:44:31 INF] Crash dumps are off. Set DOTNET_DbgEnableMiniDump=1 and DOTNET_DbgMiniDumpName to collect one.
```

The previous note asked for this in its §6 and it was never done. It is still the single thing that
would end the guessing, and it is two variables and a volume on the Railway service — `DISK_USAGE`
reads 0 GB, so there is no volume and a dump would go with the container even if one were written.

Beyond that, this branch adds one thing Modbot can do for itself: **`CrashGuard` now writes a line
when the host asks it to stop** (SIGTERM, SIGINT, SIGQUIT), without changing what happens next. It
does not catch anything. What it does is make the next silent death answerable at a glance — a log
that ends with "Modbot was told to stop by the host (SIGTERM)" is a platform stopping the
container, and a log that ends mid-sentence with nothing is a kill or a fault. Today those two look
identical in the record, and the first question after a crash has no answer.

§3.3 The experiment that would test the burst hypothesis directly

Against the deployed service, from one browser: open a Members page and let it draw a full page of
faces. It dies in under a tenth of a second when it dies. That is the reproducer, and it is far
cheaper than waiting for the maintainer to hit it.

Then, in order:

1. Turn crash dumps on with a volume (§3.2) and take one. `dotnet-dump analyze`, `clrstack -all`,
   `!threads`. What matters is whether any Modbot frame is on the faulting thread.
2. Check the Postgres service's `max_connections` and what Npgsql's pool is set to, and see whether
   forty-five concurrent requests exceed either. If they do, that is testable locally without
   waiting for another crash.
3. Only then consider the Debian base image. The previous note's §5 sets out what that costs and
   why it should not be done as a guess; the case for it is now a little stronger, because the
   crash is in the socket layer under concurrency and the request path above it has been shown to
   do nothing that could cause it — but it would still be swapping a base image on a hunch, and it
   would hide the fault rather than explain it.

§4. Changed on this branch

| File | Change |
|---|---|
| `src/Modbot.Api/Features/Live/Stream/LiveSocketSession.cs` | The receive loop's failure cannot escape the `finally` and take the "closed" line with it |
| `src/Modbot.Api/Features/Events/EventSocketSession.cs` | The same, for the same reason |
| `src/Modbot.Server/Startup/CrashGuard.cs` | Says when the host asked Modbot to stop, so a stop and a kill are not the same silence |

Tests written (**not run** — see CLAUDE.md, tests and CI come at the end). Both need Docker and
Postgres:

- `tests/Modbot.Api.Tests/Features/Live/LiveSocketTeardownTests.cs` — a connection dropped without
  a close frame while the server is inside a receive gives its place back and the next connection
  is served; a second connection opened before the first has finished closing is served, and both
  places come back; eight quick reconnects do not use up the caller's five places.
- `tests/Modbot.Api.Tests/Features/Events/EventSocketTeardownTests.cs` — the first two, on the
  event socket.

`CrashGuard`'s signal line has no test. Sending SIGTERM to the test host to watch it write a log
line would be a test that kills the runner.

`dotnet build -c Release` over the whole solution: clean, no warnings.

§5. Not done, and why

- **No fix for the crash.** §3.1 — nothing is proven, and the one thing Modbot's code does on the
  path where it dies is read a settings row.
- **No limit on the fan-out.** Files design §4.3 forbids giving these calls a bucket, a lane or a
  queue, because a member list of forty faces must not queue behind itself. That rule was written
  about VRChat's rate limit rather than about crashing a server, and if the burst turns out to be
  the cause it will have to be revisited — but not on a hypothesis.
- **The web switch is not chased down.** §1.6 needs the deployment, not the repository.
- **The base image is not changed.** §3.3, and the previous note's §5.
