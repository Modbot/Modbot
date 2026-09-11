# Modbot M0 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A deployable, self-hosting Modbot that an operator can onboard through a wizard, which authenticates to VRChat through a governed gate and records immutable facts — with no sync jobs yet.

**Architecture:** A .NET 10 modular monolith. Separate class libraries (`Core`, `VRChat`, `Analytics`, `Api`) composed by one deployable (`Host`), with a React/Vite SPA built into `wwwroot`. Every VRChat call passes through `IVRChatGate`; every timestamp comes from `IModbotClock`; every observed change becomes an append-only fact.

**Tech Stack:** .NET 10 (SDK 10.0.400), ASP.NET Core, EF Core + Npgsql, PostgreSQL 16, `VRChat.API` 2.20.9, xUnit v3, Testcontainers, React 19 + Vite + TypeScript, Docker, Railway.

**Spec:** `.agent/specs/2026-09-04-modbot-foundation-design.md` — read it before Task 1. This plan argues from it and does not restate its rationale.

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the spec.

- **Target framework:** `net10.0`. SDK pinned via `global.json` to `10.0.400`, `rollForward: latestFeature`.
- **`<Nullable>enable</Nullable>`** in every project. `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- **External services: PostgreSQL only.** No Redis, no Meilisearch, no message bus, no hosted auth, no hosted email.
- **Environment variables are exactly `PORT` and `DATABASE_URL`.** Nothing else. All other configuration lives in the `Settings` singleton row and is set through the onboarding wizard (spec §2.6, §8.1).
- **Nothing may construct a VRChat client outside `IVRChatGate`** (spec §4.1). No `new VRChatClientBuilder()` anywhere but `VRChatGate`.
- **Nothing may read the system clock.** No `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, or `DateTimeOffset.UtcNow` outside `SystemModbotClock`. Use `IModbotClock` (spec §4.4). This is enforced by an analyzer rule in Task 1.
- **No hardcoded VRChat capacity constants** — no `80`, no member caps, no instance ceilings (spec §3.1). Capacity is data read from the API.
- **Rate limits are provisional** (spec §4.3.4): 0.3–1 req/s per endpoint class. **Ask before building against an endpoint Modbot has not used before.** Never infer a limit from a neighbouring endpoint.
- **Never retry a 429.** Cold stop only (spec §4.3.1).
- **License is AGPL-3.0.** Modbot *is* open source and may say so. It is **not** "source available" and **not** non-commercial.
- **Dependency licence check:** every new package must be AGPL-compatible. MIT, BSD, Apache-2.0 are fine.

---

## File Structure

| Path | Responsibility |
|---|---|
| `Directory.Build.props` | Shared MSBuild properties: TFM, nullable, warnings-as-errors |
| `Directory.Packages.props` | Central Package Management — all versions pinned in one place |
| `global.json` | SDK pin |
| `src/Modbot.Core/Time/` | `IModbotClock`, `SystemModbotClock` |
| `src/Modbot.Core/Data/` | `ModbotContext`, entities, migrations |
| `src/Modbot.Core/Security/` | `ISecretProtector` and its DB-keyed implementation |
| `src/Modbot.VRChat/RateLimiting/` | Hierarchical token buckets, persisted limiter state |
| `src/Modbot.VRChat/` | `IVRChatGate`, `VRChatGate`, `VRChatResult<T>` |
| `src/Modbot.Analytics/` | `IFactWriter`, rollup engine, retention job |
| `src/Modbot.Api/` | Auth, onboarding endpoints |
| `src/Modbot.Web/` | React + Vite SPA |
| `src/Modbot.Host/` | Composition root, `Program.cs`, Dockerfile |
| `tests/*.Tests/` | One test project per source project |

Files that change together live together: the fact log's entity, writer and dedup logic are one unit; the gate's session, limiter and result type are another.

---

## Task 1: Repository skeleton, licensing, CI

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitattributes`
- Create: `Modbot.sln`, `src/Modbot.Host/Modbot.Host.csproj`, `src/Modbot.Host/Program.cs`
- Create: `tests/Modbot.Host.Tests/Modbot.Host.Tests.csproj`, `tests/Modbot.Host.Tests/HealthTests.cs`
- Create: `LICENSE`, `CLA.md`, `TRADEMARK.md`, `README.md`
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: nothing (first task)
- Produces: a solution that builds; `Modbot.Host` exposing `GET /health/live` returning `200 OK`; `WebApplicationFactory<Program>` usable by tests (requires `public partial class Program`)

- [ ] **Step 1: Create the solution skeleton and SDK pin**

```bash
cd /c/Users/sarma/source/repos/binn/Modbot
cat > global.json <<'EOF'
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestFeature"
  }
}
EOF
dotnet new sln --name Modbot
dotnet new web    --output src/Modbot.Host        --name Modbot.Host    --framework net10.0
dotnet new xunit3 --output tests/Modbot.Host.Tests --name Modbot.Host.Tests --framework net10.0
dotnet sln add src/Modbot.Host/Modbot.Host.csproj tests/Modbot.Host.Tests/Modbot.Host.Tests.csproj
dotnet add tests/Modbot.Host.Tests reference src/Modbot.Host
```

If `dotnet new xunit3` is unavailable, run `dotnet new install xunit.v3.templates` first.

- [ ] **Step 2: Add shared MSBuild properties**

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props` — versions are pinned here and nowhere else:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="VRChat.API" Version="2.20.9" />
  </ItemGroup>
</Project>
```

Later tasks add `<PackageVersion>` entries here and reference them with a bare
`<PackageReference Include="X" />` (no `Version` attribute).

- [ ] **Step 3: Ban the system clock and system time via analyzer config**

`.editorconfig` — the enforcement mechanism for the global no-system-clock constraint:

```ini
root = true

[*.cs]
indent_style = space
indent_size = 4
end_of_line = lf
charset = utf-8
dotnet_diagnostic.CA1305.severity = error
dotnet_diagnostic.RS0030.severity = error

[*.{json,yml,yaml,ts,tsx,js,jsx,css}]
indent_style = space
indent_size = 2
end_of_line = lf
```

`.gitattributes` — stops the LF/CRLF churn already visible in this repo:

```
* text=auto eol=lf
*.sln text eol=crlf
*.png binary
*.ico binary
*.jpg binary
```

- [ ] **Step 4: Write the failing health-check test**

`tests/Modbot.Host.Tests/HealthTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Modbot.Host.Tests;

public class HealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task LivenessProbe_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

Add the test-host package:

```bash
dotnet add tests/Modbot.Host.Tests package Microsoft.AspNetCore.Mvc.Testing
```

Then move the resolved version into `Directory.Packages.props` as a `<PackageVersion>` and strip the
`Version` attribute from the `.csproj`.

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test tests/Modbot.Host.Tests`
Expected: FAIL — `/health/live` returns 404, so the assertion reports `NotFound` instead of `OK`.

- [ ] **Step 6: Implement the minimal host**

`src/Modbot.Host/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live");

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the host in tests.
public partial class Program;
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/Modbot.Host.Tests`
Expected: PASS — 1 test passed.

- [ ] **Step 8: Add the licence, CLA and trademark files**

```bash
curl -fsSL https://www.gnu.org/licenses/agpl-3.0.txt -o LICENSE
```

Verify it starts with `GNU AFFERO GENERAL PUBLIC LICENSE` and `Version 3, 19 November 2007`, and that
the file is ~34KB. If the fetch fails, copy the text from <https://www.gnu.org/licenses/agpl-3.0.txt>
manually — **do not** substitute a different licence or write a summary.

`TRADEMARK.md`:

```markdown
# Trademark Policy

"Modbot" and the Modbot logo are trademarks of the Modbot project's copyright holder.

The AGPL-3.0 licence in `LICENSE` grants rights to the *software*. It does not grant any right
to use the project's name or logo.

You may:

- State accurately that your project is built on, derived from, or compatible with Modbot.
- Use the name in unmodified redistributions of official releases.

You may not, without written permission:

- Name a fork, service, or product "Modbot", or any name likely to be confused with it.
- Use the Modbot logo as the identity of a fork or service.
- Imply endorsement by, or affiliation with, the Modbot project.

If you fork Modbot, please pick your own name. This keeps things clear for users trying to work
out who is responsible for the software they are running.
```

`CLA.md`:

```markdown
# Contributor License Agreement

By submitting a contribution to Modbot, you agree to the following.

1. **You grant a licence.** You grant the project's copyright holder a perpetual, worldwide,
   non-exclusive, royalty-free, irrevocable licence to use, reproduce, modify, sublicense and
   distribute your contribution, and to relicense it under any licence.

2. **You retain your copyright.** You keep ownership of your contribution and may use it
   elsewhere however you wish. This agreement is a licence grant, not a transfer.

3. **You have the right to contribute it.** The contribution is your original work, or you have
   the necessary rights to submit it. If your employer has rights to work you produce, you have
   permission to contribute it or your employer has waived those rights.

4. **No warranty.** Contributions are provided as-is, without warranty of any kind.

## Why this exists

Modbot is AGPL-3.0. Clause 1 exists so the project can keep its licensing coherent and change it
if it ever needs to, without having to contact every past contributor. Clause 2 exists because
your work stays yours.

Please note that nothing in this agreement requires you to contribute changes upstream. A licence
cannot compel that. We would rather you did, and this document is what lets us accept it cleanly
when you do.
```

- [ ] **Step 9: Write the README**

`README.md`:

```markdown
# Modbot

Self-hosted moderation, analytics and automation for a single VRChat group.

Modbot caches what VRChat's API exposes but its own UI does not make usable, records the history
VRChat throws away, and gives your staff tools VRChat does not provide.

**Modbot is not affiliated with or endorsed by VRChat Inc.**

## Status

In development. See `.agent/specs/` for the design and `.agent/plans/` for implementation plans.

## Deploying

Modbot runs as one service plus one PostgreSQL database. It needs exactly two environment
variables — `PORT` and `DATABASE_URL` — and everything else is configured in the app through a
setup wizard the first time you open it.

## Documentation

`docs/` — setup, usage, self-hosting and API reference.

## Licence

[AGPL-3.0](LICENSE). Modbot is open source: you may use, modify, self-host and fork it. If you run
a modified Modbot as a network service, you must offer your users its source.

Contributions are covered by [CLA.md](CLA.md). The name and logo are covered by
[TRADEMARK.md](TRADEMARK.md) and are not granted by the licence.
```

- [ ] **Step 10: Add CI**

`.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Test
        run: dotnet test --no-build --configuration Release
```

- [ ] **Step 11: Verify a clean build from scratch and commit**

```bash
dotnet build --configuration Release
dotnet test
```

Expected: build succeeds with zero warnings (warnings are errors), 1 test passes.

```bash
git add -A
git commit -m "feat: solution skeleton, AGPL licensing, CI

- .NET 10 solution with central package management and SDK pin
- Warnings as errors, nullable enabled solution-wide
- .gitattributes normalises line endings (repo was churning CRLF)
- AGPL-3.0 LICENSE, CLA.md, TRADEMARK.md
- Host with /health/live and a passing integration test
- GitHub Actions build + test"
```

---

## Task 2: `IModbotClock`

Placed second, before any entity exists, because the global constraint forbids the system clock and
every later task needs a clock to inject. Introducing it after the entities would mean retrofitting
every one of them.

**Files:**
- Create: `src/Modbot.Core/Modbot.Core.csproj`, `src/Modbot.Core/Time/IModbotClock.cs`, `src/Modbot.Core/Time/SystemModbotClock.cs`
- Create: `tests/Modbot.Core.Tests/Modbot.Core.Tests.csproj`, `tests/Modbot.Core.Tests/Time/FakeClock.cs`, `tests/Modbot.Core.Tests/Time/SystemModbotClockTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Modbot.Core.Time.IModbotClock` with `DateTimeOffset UtcNow { get; }`
  - `Modbot.Core.Time.SystemModbotClock : IModbotClock`
  - `Modbot.Core.Tests.Time.FakeClock : IModbotClock` with `DateTimeOffset UtcNow { get; set; }` and `void Advance(TimeSpan by)` — **every later test uses this**

- [ ] **Step 1: Create the projects**

```bash
cd /c/Users/sarma/source/repos/binn/Modbot
dotnet new classlib --output src/Modbot.Core        --name Modbot.Core        --framework net10.0
dotnet new xunit3   --output tests/Modbot.Core.Tests --name Modbot.Core.Tests --framework net10.0
dotnet sln add src/Modbot.Core/Modbot.Core.csproj tests/Modbot.Core.Tests/Modbot.Core.Tests.csproj
dotnet add tests/Modbot.Core.Tests reference src/Modbot.Core
rm src/Modbot.Core/Class1.cs
```

- [ ] **Step 2: Write the failing tests**

`tests/Modbot.Core.Tests/Time/SystemModbotClockTests.cs`:

```csharp
using Modbot.Core.Time;

namespace Modbot.Core.Tests.Time;

public class SystemModbotClockTests
{
    [Fact]
    public void UtcNow_IsUtc()
    {
        var clock = new SystemModbotClock();

        Assert.Equal(TimeSpan.Zero, clock.UtcNow.Offset);
    }

    [Fact]
    public void UtcNow_AdvancesBetweenReads()
    {
        var clock = new SystemModbotClock();

        var first = clock.UtcNow;
        Thread.Sleep(2);
        var second = clock.UtcNow;

        Assert.True(second >= first, $"clock went backwards: {second} < {first}");
    }
}
```

`tests/Modbot.Core.Tests/Time/FakeClock.cs` — the shared test double:

```csharp
using Modbot.Core.Time;

namespace Modbot.Core.Tests.Time;

/// <summary>
/// Deterministic clock for tests. Every test that needs time uses this, never the system clock.
/// </summary>
public sealed class FakeClock : IModbotClock
{
    public FakeClock(DateTimeOffset? start = null)
        => UtcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Modbot.Core.Tests`
Expected: FAIL to compile — `The type or namespace name 'Time' does not exist in the namespace 'Modbot.Core'`.

- [ ] **Step 4: Implement the clock**

`src/Modbot.Core/Time/IModbotClock.cs`:

```csharp
namespace Modbot.Core.Time;

/// <summary>
/// Modbot's single source of time.
/// </summary>
/// <remarks>
/// Nothing in Modbot may read the system clock directly. Analytics correctness depends on
/// timestamps from machines Modbot does not control (moderator PCs running the Windows client),
/// and the rate limiter must not believe a penalty expired because the host clock stepped.
/// Both problems are solved by there being exactly one clock. See spec section 4.4.
/// </remarks>
public interface IModbotClock
{
    /// <summary>The current instant, always with a zero offset.</summary>
    DateTimeOffset UtcNow { get; }
}
```

`src/Modbot.Core/Time/SystemModbotClock.cs`:

```csharp
namespace Modbot.Core.Time;

/// <summary>
/// The one place in Modbot permitted to read the machine clock.
/// </summary>
public sealed class SystemModbotClock : IModbotClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Modbot.Core.Tests`
Expected: PASS — 2 tests passed.

- [ ] **Step 6: Add a guard test that the ban is real**

This is the test that keeps the constraint alive after everyone forgets about it.
`tests/Modbot.Core.Tests/Time/NoSystemClockTests.cs`:

```csharp
using System.Reflection;
using System.Text.RegularExpressions;

namespace Modbot.Core.Tests.Time;

public class NoSystemClockTests
{
    private static readonly Regex SystemClockUse = new(
        @"\bDateTime(Offset)?\s*\.\s*(UtcNow|Now|Today)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Walks the actual source tree. A unit test cannot catch this because the violation compiles
    /// perfectly well -- it just silently produces wrong timestamps.
    /// </summary>
    [Fact]
    public void NoSourceFileReadsTheSystemClock()
    {
        var repoRoot = FindRepoRoot();
        var allowed = Path.Combine(repoRoot, "src", "Modbot.Core", "Time", "SystemModbotClock.cs");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !string.Equals(f, allowed, StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => SystemClockUse.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(repoRoot, f))
            .Order()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These files read the system clock instead of IModbotClock:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.sln.");
    }
}
```

Note the exclusion of `*.Designer.cs`: EF Core generates migration designer files that embed
timestamps, and those are generated code rather than hand-written clock reads.

- [ ] **Step 7: Run the guard test**

Run: `dotnet test tests/Modbot.Core.Tests`
Expected: PASS — 3 tests passed. It passes now because only `SystemModbotClock` reads the clock.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: IModbotClock as Modbot's single source of time

Nothing may read the system clock directly -- analytics correctness
depends on timestamps from machines Modbot does not control, and the
rate limiter must not believe a penalty expired because the host clock
stepped. Spec 4.4.

Includes FakeClock for every later test, and a source-scanning guard
test, because the violation compiles fine and fails silently."
```

---

## Remaining M0 tasks

Tasks 3–13 follow the same shape. Rather than emitting them all in one pass, they are written in
batches so each stays reviewable:

| # | Task | Key deliverable |
|---|---|---|
| 3 | `ModbotContext` + `Settings` singleton + Testcontainers harness | Migration applies; singleton round-trips |
| 4 | `ISecretProtector` — DB-keyed encryption | Secrets encrypted at rest, key auto-generated on first boot |
| 5 | `ModbotUser`, password hashing, cookie sessions, `RequiresFlagAttribute` | Login works; permission flags enforced |
| 6 | Fact log: `modbot_event`, monthly partitions, `IFactWriter`, ±5 s dedup | Facts append; boundary and 15 s rejoin cases covered |
| 7 | Rollups: `modbot_rollup_daily`, `RollupJob`, recompute invariant | Property test: recomputed == incremental |
| 8 | Retention: tiered pruning by partition drop | Moderation kept, presence pruned at 90 d |
| 9 | Hierarchical token buckets, `IRateLimitLease`, persisted limiter state | Cold stop survives restart; one probe per window |
| 10 | `IVRChatGate` | Non-throwing SDK verified; WAF classified; AIMD |
| 11 | Onboarding API: admin, VRChat login, connection test + proxy, group select | Wizard completable end to end |
| 12 | React/Vite SPA + setup wizard UI | Served from `wwwroot` |
| 13 | Host wiring, health checks, Dockerfile, `railway.json` | One-click deployable |

---

## Self-Review

Run after all tasks are written, per the writing-plans skill:

1. **Spec coverage** — walk spec §§2–12 and name the task implementing each.
2. **Placeholder scan** — no "TBD", no "add appropriate error handling", no "similar to Task N".
3. **Type consistency** — `IModbotClock.UtcNow`, `FakeClock.Advance`, `IFactWriter`, `IVRChatGate.ExecuteAsync`, `VRChatResult<T>` must be spelled identically everywhere they appear.
