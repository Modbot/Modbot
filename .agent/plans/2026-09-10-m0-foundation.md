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
| `src/Modbot.Analytics/` | `IFactWriter`, daily totals engine, retention job |
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

## Task 3: `ModbotContext`, the `Settings` singleton, and the Postgres test harness

**Files:**
- Create: `src/Modbot.Core/Data/ModbotContext.cs`, `src/Modbot.Core/Data/Entities/Settings.cs`
- Create: `src/Modbot.Core/Data/ModbotContextFactory.cs` (design-time, for `dotnet ef`)
- Create: `tests/Modbot.Core.Tests/Data/PostgresFixture.cs`, `tests/Modbot.Core.Tests/Data/SettingsTests.cs`
- Modify: `Directory.Packages.props`

**Interfaces:**
- Consumes: `IModbotClock` (Task 2)
- Produces:
  - `Modbot.Core.Data.ModbotContext : DbContext` with `DbSet<Settings> Settings`
  - `Modbot.Core.Data.Entities.Settings` — singleton, `Id` always `1`, accessed via `ModbotContext.GetSettingsAsync(CancellationToken)`
  - `Modbot.Core.Tests.Data.PostgresFixture` — an `IAsyncLifetime` collection fixture exposing `string ConnectionString` and `ModbotContext NewContext()`. **Every later database test uses this.**

- [ ] **Step 1: Add packages**

Add to `Directory.Packages.props` inside the existing `<ItemGroup>`:

```xml
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.0" />
<PackageVersion Include="Testcontainers.PostgreSql" Version="4.0.0" />
```

If those exact versions do not resolve, run `dotnet add package <name>` to get the current one and
update `Directory.Packages.props` to match. Then:

```bash
cd /c/Users/sarma/source/repos/binn/Modbot
dotnet add src/Modbot.Core package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/Modbot.Core package Microsoft.EntityFrameworkCore.Design
dotnet add tests/Modbot.Core.Tests package Testcontainers.PostgreSql
dotnet tool install --global dotnet-ef --prerelease 2>/dev/null || dotnet tool update --global dotnet-ef
```

Strip every `Version=` attribute from the resulting `<PackageReference>` elements — central package
management owns versions.

- [ ] **Step 2: Write the failing test**

`tests/Modbot.Core.Tests/Data/PostgresFixture.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Testcontainers.PostgreSql;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// One real PostgreSQL container shared by every database test in the assembly.
/// Real Postgres, not SQLite or InMemory: Modbot depends on table partitioning, jsonb, GIN
/// indexes and advisory locks, none of which a substitute provider implements faithfully.
/// A test that passes against a fake database proves nothing about the code that ships.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ModbotContext(options);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

`tests/Modbot.Core.Tests/Data/SettingsTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.Core.Tests.Data;

[Collection(nameof(PostgresCollection))]
public class SettingsTests
{
    private readonly PostgresFixture _db;

    public SettingsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task GetSettingsAsync_CreatesTheSingletonOnFirstCall()
    {
        await using var context = _db.NewContext();

        var settings = await context.GetSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, settings.Id);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsTheSameRowEveryTime()
    {
        await using (var write = _db.NewContext())
        {
            var settings = await write.GetSettingsAsync(TestContext.Current.CancellationToken);
            settings.ManagedGroupId = "grp_test_0001";
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = _db.NewContext();
        var reloaded = await read.GetSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("grp_test_0001", reloaded.ManagedGroupId);
        Assert.Equal(1, await read.Settings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecondSettingsRow_IsRejectedByTheDatabase()
    {
        await using var context = _db.NewContext();
        await context.GetSettingsAsync(TestContext.Current.CancellationToken);

        // Bypass EF's change tracker -- the guarantee must live in the database, not in C#.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Database.ExecuteSqlRawAsync(
                "INSERT INTO settings (id, onboarding_complete) VALUES (2, false)",
                TestContext.Current.CancellationToken));

        Assert.Contains("ck_settings_singleton", ex.ToString());
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Modbot.Core.Tests --filter "FullyQualifiedName~SettingsTests"`
Expected: FAIL to compile — `ModbotContext` does not exist.

- [ ] **Step 4: Implement the entity and context**

`src/Modbot.Core/Data/Entities/Settings.cs`:

```csharp
namespace Modbot.Core.Data.Entities;

/// <summary>
/// Modbot's entire configuration, as a single row.
/// </summary>
/// <remarks>
/// Spec section 2.6: the only environment variables are PORT and DATABASE_URL. Everything else is
/// entered through the onboarding wizard and lives here, so deploying Modbot is "click the
/// template, open the URL, follow the wizard" rather than a page of environment variables.
/// Secret-bearing columns are encrypted by ISecretProtector (Task 4).
/// </remarks>
public class Settings
{
    /// <summary>Always 1. Enforced by a database check constraint, not by convention.</summary>
    public int Id { get; set; } = 1;

    public bool OnboardingComplete { get; set; }

    // --- VRChat account (spec 2.3) ---
    public string? VRChatUsername { get; set; }
    public string? VRChatPasswordEncrypted { get; set; }
    public string? VRChatTotpSecretEncrypted { get; set; }
    public string? VRChatAuthCookieEncrypted { get; set; }

    // --- Managed group (spec 2.4) ---
    public string? ManagedGroupId { get; set; }
    public string? ManagedGroupName { get; set; }

    // --- Optional egress proxy (spec 2.3.1) ---
    public string? ProxyUrl { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPasswordEncrypted { get; set; }

    // --- Retention, tiered per fact class (spec 5.5) ---
    public int ModerationFactRetentionDays { get; set; } = 0;   // 0 = keep forever
    public int PresenceFactRetentionDays { get; set; }            // 0 = keep forever

    /// <summary>
    /// Deduplication half-window for client-reported facts (spec 5.7.1). Bounded above by the
    /// 15-second genuine leave-and-rejoin, below by residual clock skew after IModbotClock sync.
    /// </summary>
    public int DedupWindowSeconds { get; set; } = 5;

    /// <summary>Spec 5.8.1 -- optional by default; groups may opt into requiring it.</summary>
    public bool RequireModerationClassification { get; set; }
}
```

`src/Modbot.Core/Data/ModbotContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

public class ModbotContext : DbContext
{
    public ModbotContext(DbContextOptions<ModbotContext> options) : base(options) { }

    public DbSet<Settings> Settings => Set<Settings>();

    /// <summary>
    /// Reads the singleton, creating it on first call. Every caller uses this rather than
    /// querying <see cref="Settings"/> directly, so "the row might not exist yet" is handled once.
    /// </summary>
    public async Task<Settings> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await Settings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is not null)
            return settings;

        settings = new Settings { Id = 1 };
        Settings.Add(settings);
        await SaveChangesAsync(ct);

        return settings;
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Settings>(entity =>
        {
            entity.ToTable("settings", t =>
                t.HasCheckConstraint("ck_settings_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        base.OnModelCreating(builder);
    }
}
```

`src/Modbot.Core/Data/ModbotContextFactory.cs` — required by `dotnet ef`, which cannot boot the Host:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modbot.Core.Data;

/// <summary>
/// Design-time factory for `dotnet ef migrations add`. The connection string is never used to
/// connect -- EF only needs a provider to generate SQL.
/// </summary>
public sealed class ModbotContextFactory : IDesignTimeDbContextFactory<ModbotContext>
{
    public ModbotContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql("Host=localhost;Database=modbot_design_time")
            .Options;

        return new ModbotContext(options);
    }
}
```

- [ ] **Step 5: Configure snake_case naming and generate the migration**

Add to `ModbotContext.OnModelCreating`, before `base.OnModelCreating(builder)`:

```csharp
        // Postgres convention. Without this, EF emits "OnboardingComplete", which needs
        // double-quoting in every hand-written SQL statement -- and Tasks 6 and 8 write a lot
        // of hand-written SQL for partitioning.
        builder.UseSnakeCaseNamingConvention();
```

This requires the naming-convention package:

```bash
dotnet add src/Modbot.Core package EFCore.NamingConventions
dotnet ef migrations add InitialCreate --project src/Modbot.Core --output-dir Data/Migrations
```

Add `<PackageVersion Include="EFCore.NamingConventions" Version="10.0.0" />` to
`Directory.Packages.props` and strip the inline version.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Modbot.Core.Tests`
Expected: PASS — 6 tests. Docker must be running; Testcontainers pulls `postgres:16-alpine` on
first run, so the first execution takes noticeably longer.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: ModbotContext, Settings singleton, Postgres test harness

Settings is one row holding all configuration -- spec 2.6 limits
environment to PORT and DATABASE_URL, so everything else lives here and
is set by the wizard.

The singleton guarantee is a database check constraint, not a
convention, and is tested by trying to violate it through raw SQL.

Tests run against real PostgreSQL via Testcontainers. Modbot depends on
partitioning, jsonb, GIN and advisory locks; a substitute provider
implements none of them faithfully."
```

---

## Task 4: `ISecretProtector`

**Files:**
- Create: `src/Modbot.Core/Security/ISecretProtector.cs`, `src/Modbot.Core/Security/AesGcmSecretProtector.cs`, `src/Modbot.Core/Data/Entities/ProtectorKey.cs`
- Create: `tests/Modbot.Core.Tests/Security/SecretProtectorTests.cs`
- Modify: `src/Modbot.Core/Data/ModbotContext.cs`

**Interfaces:**
- Consumes: `ModbotContext` (Task 3)
- Produces: `Modbot.Core.Security.ISecretProtector` with `string Protect(string plaintext)` and `string? Unprotect(string? ciphertext)`

- [ ] **Step 1: Write the failing test**

`tests/Modbot.Core.Tests/Security/SecretProtectorTests.cs`:

```csharp
using Modbot.Core.Security;
using Modbot.Core.Tests.Data;

namespace Modbot.Core.Tests.Security;

[Collection(nameof(PostgresCollection))]
public class SecretProtectorTests
{
    private readonly PostgresFixture _db;

    public SecretProtectorTests(PostgresFixture db) => _db = db;

    private async Task<ISecretProtector> CreateAsync()
    {
        var context = _db.NewContext();
        return await AesGcmSecretProtector.CreateAsync(context, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RoundTrips()
    {
        var protector = await CreateAsync();

        var ciphertext = protector.Protect("hunter2");

        Assert.NotEqual("hunter2", ciphertext);
        Assert.Equal("hunter2", protector.Unprotect(ciphertext));
    }

    [Fact]
    public async Task ProducesDifferentCiphertextForTheSamePlaintext()
    {
        var protector = await CreateAsync();

        // A fresh nonce per call. Identical ciphertexts would let anyone reading the table see
        // that two accounts share a password.
        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public async Task RejectsTamperedCiphertext()
    {
        var protector = await CreateAsync();
        var bytes = Convert.FromBase64String(protector.Protect("hunter2"));
        bytes[^1] ^= 0xFF;

        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public async Task ReusesTheStoredKeyAcrossInstances()
    {
        var first = await CreateAsync();
        var ciphertext = first.Protect("persisted");

        var second = await CreateAsync();

        Assert.Equal("persisted", second.Unprotect(ciphertext));
    }

    [Fact]
    public void UnprotectNull_ReturnsNull()
    {
        // Settings columns are nullable before onboarding fills them in; callers should not have
        // to null-check at every site.
        Assert.Null(AesGcmSecretProtector.ForTesting().Unprotect(null));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Modbot.Core.Tests --filter "FullyQualifiedName~SecretProtectorTests"`
Expected: FAIL to compile — `Modbot.Core.Security` does not exist.

- [ ] **Step 3: Implement**

`src/Modbot.Core/Security/ISecretProtector.cs`:

```csharp
namespace Modbot.Core.Security;

/// <summary>
/// Encrypts the secret-bearing columns of <see cref="Data.Entities.Settings"/>.
/// </summary>
/// <remarks>
/// Spec section 8.3. The key is generated on first boot and stored in the database, so there is
/// no environment variable to lose and no way to permanently brick an install by redeploying.
///
/// This protects against casual reading of a database dump and NOTHING MORE -- an attacker with
/// full database access has the key too. That is the correct trade for community groups on
/// managed hosting, where Railway already stores environment variables in plaintext and shows
/// them in its dashboard. Operators who need real encryption at rest should encrypt the database.
/// This is documented plainly in docs/security.md; do not overstate it elsewhere.
/// </remarks>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Returns null for null input, so callers need not null-check every column.</summary>
    string? Unprotect(string? ciphertext);
}
```

`src/Modbot.Core/Data/Entities/ProtectorKey.cs`:

```csharp
namespace Modbot.Core.Data.Entities;

/// <summary>The auto-generated encryption key. Singleton, like <see cref="Settings"/>.</summary>
public class ProtectorKey
{
    public int Id { get; set; } = 1;
    public byte[] Key { get; set; } = [];
}
```

`src/Modbot.Core/Security/AesGcmSecretProtector.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Security;

/// <summary>AES-256-GCM. Layout: [12-byte nonce][16-byte tag][ciphertext], base64-encoded.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;

    private AesGcmSecretProtector(byte[] key) => _key = key;

    public static async Task<ISecretProtector> CreateAsync(ModbotContext context, CancellationToken ct = default)
    {
        var stored = await context.ProtectorKeys.FirstOrDefaultAsync(k => k.Id == 1, ct);
        if (stored is null)
        {
            stored = new ProtectorKey { Id = 1, Key = RandomNumberGenerator.GetBytes(KeySize) };
            context.ProtectorKeys.Add(stored);
            await context.SaveChangesAsync(ct);
        }

        return new AesGcmSecretProtector(stored.Key);
    }

    /// <summary>An ephemeral protector for tests that do not need persistence.</summary>
    public static ISecretProtector ForTesting()
        => new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(KeySize));

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(
            nonce,
            plain,
            output.AsSpan(NonceSize + TagSize),
            output.AsSpan(NonceSize, TagSize));

        return Convert.ToBase64String(output);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (ciphertext is null)
            return null;

        var input = Convert.FromBase64String(ciphertext);
        if (input.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext is too short to be valid.");

        var plain = new byte[input.Length - NonceSize - TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(
            input.AsSpan(0, NonceSize),
            input.AsSpan(NonceSize + TagSize),
            input.AsSpan(NonceSize, TagSize),
            plain);

        return Encoding.UTF8.GetString(plain);
    }
}
```

- [ ] **Step 4: Register the entity and migrate**

In `ModbotContext`, add the set and mapping:

```csharp
    public DbSet<ProtectorKey> ProtectorKeys => Set<ProtectorKey>();
```

and inside `OnModelCreating`:

```csharp
        builder.Entity<ProtectorKey>(entity =>
        {
            entity.ToTable("protector_key", t =>
                t.HasCheckConstraint("ck_protector_key_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });
```

```bash
dotnet ef migrations add AddProtectorKey --project src/Modbot.Core --output-dir Data/Migrations
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Modbot.Core.Tests`
Expected: PASS — 11 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: ISecretProtector, AES-256-GCM with a database-stored key

Spec 8.3. Key generated on first boot and stored in the database: no
environment variable to lose, no way to brick an install by redeploying.

Protects against casual reading of a dump and nothing more, which is the
correct trade for community groups on managed hosting. The interface
docs say so explicitly so nobody later describes it as encryption at
rest.

Fresh nonce per call, so identical passwords do not produce identical
ciphertext. Tamper detection is tested by flipping a byte."

---

## Self-Review

Run after all tasks are written, per the writing-plans skill:

1. **Spec coverage** — walk spec §§2–12 and name the task implementing each.
2. **Placeholder scan** — no "TBD", no "add appropriate error handling", no "similar to Task N".
3. **Type consistency** — `IModbotClock.UtcNow`, `FakeClock.Advance`, `IFactWriter`, `IVRChatGate.ExecuteAsync`, `VRChatResult<T>` must be spelled identically everywhere they appear.

---

## Remaining M0 tasks

Written in batches so each stays reviewable. Tasks 1–4 are complete above.

| # | Task | Key deliverable | Notes for the author |
|---|---|---|---|
| 5 | `ModbotUser`, password hashing, cookie sessions, `RequiresFlagAttribute` | Login works; permission flags enforced | `PasswordHasher<ModbotUser>` registered standalone from `Microsoft.Extensions.Identity.Core` — **no ASP.NET Core Identity** (spec §7.2). Permissions are a `[Flags]` enum. |
| 6 | Fact log: `modbot_event`, monthly partitions, `IFactWriter`, ±5 s windowed dedup | Facts append; bucket-boundary and 15 s rejoin cases covered | The hardest task in M0. See design notes below. |
| 7 | Daily totals: `modbot_rollup_daily`, `DailyTotalsJob`, recompute | Property test: recomputed == incremental | Generic `(day, metric, dimension)` shape so new metrics need no migration (spec §5.4). Include the §5.2.1 counted-only path. |
| 8 | Retention: tiered pruning | Moderation kept, presence pruned at 90 d | Prune by `DROP TABLE` on whole partitions, never mass `DELETE` (spec §5.5). |
| 9 | Hierarchical token buckets, `IRateLimitLease`, persisted limiter state | Cold stop survives restart; one probe per window | Global → endpoint class → resource (spec §4.3.1). State in the DB (§4.3.2). Fake must *model the punitive limiter* or the tests prove nothing. |
| 10 | `IVRChatGate` | Non-throwing SDK verified; WAF classified; AIMD | Must include a smoke test asserting `...WithHttpInfoAsync` returns a non-success `ApiResponse` rather than throwing — pins the upstream behaviour this design depends on. |
| 11 | Onboarding API: admin, VRChat login, connection test + proxy, group select | Wizard completable end to end | Connection test must distinguish a WAF block from DNS/timeout/bad-credentials; a proxy fixes only the first (spec §7.1.1). |
| 12 | React/Vite SPA + setup wizard UI | Served from `wwwroot` | npm, not pnpm (not installed). Vite `build.outDir` → `../../Modbot.Host/wwwroot`. |
| 13 | Host wiring, health checks, Dockerfile, `.railway/railway.ts` | One-click deployable | Resolve `PORT` in the entrypoint shell, not an `ENV` line — it is unset at build time. |

### Design notes for Task 6 (fact log)

Recorded now so they are not rediscovered during implementation.

**Partitioning forces the primary key.** PostgreSQL requires the partition key to be part of the
primary key, so `modbot_event` is keyed on `(id, occurred_at)`, not `id` alone. EF Core cannot
express declarative partitioning, so the `CREATE TABLE … PARTITION BY RANGE (occurred_at)` and the
per-month partitions are hand-written `migrationBuilder.Sql(...)`, with the entity mapped onto the
resulting table.

**A partition-creation job is required, not optional.** An insert with no matching partition
*fails*. Something must create next month's partition ahead of time, and the failure mode if it
does not is total ingest loss — so it needs its own test that advances `FakeClock` across a month
boundary and asserts the insert still succeeds.

**Dedup cannot be a unique index.** §5.7.1 specifies a *windowed* range check (±5 s), and ranges
are not expressible as a unique constraint. Two clients reporting the same join concurrently would
both pass a naive check-then-insert. The fix is a Postgres transaction-scoped advisory lock keyed
on the logical event, which serialises only same-key inserts:

```sql
SELECT pg_advisory_xact_lock(hashtext(@subject_platform || ':' || @subject_id
                                      || ':' || @type || ':' || @instance_id));
-- then range-check within ±window, then insert
```

**Schema additions from spec §5.3** (added after the accountability and Discord work — do not omit):
`subject_platform` and `actor_platform` (`VRChat | Discord`), and a second index on
`(actor_platform, actor_id, occurred_at DESC)` for §5.8.5's actor-side queries. `source` gains a
`Discord` member.
