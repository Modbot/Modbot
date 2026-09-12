# Modbot — working conventions

Guidance for anyone (human or agent) contributing to this repository.

## Commits and pull requests

**No AI-tool attribution.** Commit messages and PR descriptions must not contain co-author trailers
for AI assistants, session links, or "generated with" watermarks. Write the substantive message and
stop at the last real paragraph.

Modbot is meant to outlive any one contributor and be picked up by others. The history should read
as the project's own.

## Writing commit messages

Explain **why**, not just what. The design specs in `.agent/specs/` carry the reasoning for decisions;
commit messages carry the reasoning for changes. If a change reverses or narrows an earlier decision,
say which one and why.

## Where things live

| Path | Contents |
|---|---|
| `.agent/specs/` | Design documents. Read before changing behaviour. |
| `.agent/plans/` | Implementation plans, task-by-task. |
| `.agent/research/` | Empirical findings about VRChat's API and logs, with fixtures. |
| `docs/` | End-user documentation only — setup, usage, self-hosting, API reference. |
| `explore/` | Scratch tool for probing the live VRChat API. Not part of the product. |
| `libs/`, `old/` | Gitignored reference clones. Never edit; never import from. |

## Running the tests

```
dotnet run --project tests/Modbot.Core.Tests -c Release
```

**Not `dotnet test`.** The suites are xUnit v3 on Microsoft.Testing.Platform v2
(`xunit.v3.mtp-v2`), which SDK 10.0.400's `dotnet test` does not drive: it reports
*"Zero tests ran"* and exits 5 while the very same assemblies pass when run directly.

That failure mode is worth knowing, because it is easy to misread as "the tests are broken"
rather than "the runner is wrong". CI runs each suite directly for the same reason.

The data tests need **Docker** — they run against real PostgreSQL via Testcontainers rather
than a substitute provider, because Modbot depends on table partitioning, `jsonb`, GIN indexes
and advisory locks, and an in-memory provider implements none of them.

## Standing rules

These are recorded in the foundation spec and are easy to violate by accident:

- **Never read the system clock.** Use `IModbotClock` (§4.4).
- **Never construct a VRChat client outside `IVRChatGate`** (§4.1), and never call the SDK's
  convenience overloads — always `...WithHttpInfoAsync` (§4.1.1).
- **Never validate VRChat id formats.** Legacy ids follow no structure (§3.1.1).
- **Never hardcode VRChat capacity limits.** Exemptions raise them (§3.1).
- **Never retry a 429.** Cold stop only (§4.3.1).
- **Ask about the rate limit before using a new endpoint** (§4.3.4).
