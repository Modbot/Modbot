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

## Naming — plain words only

**Use names a sixteen-year-old with no technical background would understand.** This applies to
UI text, API field names, page titles, code identifiers, spec headings and docs alike.

Not `dossier` — **profile**. Not `posture` — **status**. Not `cadence` — **poll rate**. Not
`sentinel` — **store marker**. Not `latched` — **locked**. Not `commission` — **set up**. Not
`canary` — **test file**. Not `backfill` — **catch-up**. Not `rollup` — **daily totals**. Not
`projection` — **estimate**. Not `headroom` — **room left**. Not `evacuate` — **move out**. Not
`room` — **instance** (VRChat's own word; one word for one thing, since 2026-09-17).

The test is simple: if you would have to explain the word to a volunteer moderator, pick a
different word. Ordinary English words with an ordinary meaning are fine (`fact`, `event`,
`ban`, `sync`); words borrowed from distributed-systems vocabulary are not, however precise they
feel. Precision that nobody can read is not precision.

## UI text — controls, not explanations

**Never add explanatory text to the UI unless someone explicitly asks for it.** No card
descriptions, hints, captions, footnotes or asides that explain how a feature works, why it was
designed that way, or what could go wrong. A label names a control; a heading names a section;
an error message says what failed. That is all the text a screen needs.

The reasoning behind a design belongs in `.agent/specs/` and in code comments, not in front of a
moderator. If a screen seems to need a paragraph to be understood, fix the screen.

## Link previews — every page gets one

**A new page carries the tags a chat app reads to draw its preview.** Moderators paste Modbot links
into Discord all day. A link with no tags shows as a bare address, and a link whose preview is word
for word every other page's is worse, because it reads as broken. A preview needs four things: a
title, a description, an image and the site's name, with Twitter's `twitter:card` tags beside them.
The image address must be the whole `https://` one, because the crawler that fetches it has no page
to work a relative address out from. The share image is `og.png`, and the brand spec (§7) says where
it lives.

A crawler runs no script, so tags the app sets once it has loaded are never read. They belong in the
file the server sends: the page's own `.html` in a Vite app, `generateMetadata` in the docs site.
Where a site has real pages each one says its own name; where every path serves a single file, one
honest set for the whole site is all it can have.

A preview is public, and that decides what it may say. The moderator app names the product and never
the page: the links moderators paste carry a person, a ban or a case file in them, and a preview
would spell that into a channel the app itself keeps behind a sign-in.

## Docs — every feature gets a page

**Every feature a moderator or member can see gets its own page in `docs/content/docs/`, or an
update to the page it belongs on.** Add one line for it to its section's overview page (the
`index.mdx` of its folder). If it is one of the section's main features, name it in that section's
paragraph of the Introduction (`docs/content/docs/index.mdx`). Building something that
`not-built-yet.mdx` lists takes it off that page in the same change.

The Introduction and the overview pages are how people find a feature. A page that nothing links
to is a feature nobody knows Modbot has, and `not-built-yet.mdx` is only honest while it lists what
is still missing.

## Where things live

| Path | Contents |
|---|---|
| `.agent/specs/` | Design documents. Read before changing behaviour. |
| `.agent/plans/` | Implementation plans, task-by-task. |
| `.agent/research/` | Empirical findings about VRChat's API and logs, with fixtures. |
| `.agent/docs/` | The Markdown docs from before the docs site, kept as source material and reference. Not published; the site in `docs/` replaces them. |
| `docs/` | The documentation site (Next.js and Fumadocs), published at docs.modbot.co: end-user pages in `docs/content/docs/`, the API reference made from `docs/openapi/modbot.json`. See `docs/README.md`. |
| `explore/` | Scratch tool for probing the live VRChat API. Not part of the product. |
| `libs/`, `old/` | Gitignored reference clones. Never edit; never import from. |
| `G:\brain\projects\modbot.md` | The owner's second brain: Modbot's history, decisions and the owner's preferences (also `people/`, `projects/thy-kingdom`). Rules in `~/.claude/rules/brain.md`. |

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

### Tests and CI come at the end

**An agent building a feature does not run the test suites and does not watch CI.** Waiting on them
is most of an agent's wall-clock time, and several agents waiting in parallel is most of a session.

Before pushing, an agent still checks that the work compiles:

- `dotnet build -c Release`
- the web project's typecheck, lint and build, for web changes
- `dotnet ef migrations has-pending-model-changes`, for model changes

Tests are still **written**; they are simply not run yet, and the agent lists in its report what it
wrote but did not run. When a run of features has landed, one testing pass runs every suite and every
web check, fixes what fails, and takes CI green on master.

### Where work lands

**Push to `staging`, not `master`.** CI runs on pushes to `master` only (and on demand), because a
run costs Actions minutes and a batch of features would otherwise pay for one per commit.

`staging` is merged into `master` when a batch is ready and the testing pass has it passing. That
merge is the CI run that matters.

### Which model runs end-to-end testing

**Agents that run end-to-end testing use the latest Sonnet model (currently Sonnet 5,
`claude-sonnet-5`), not Opus.** That covers:
- running the test suites
- watching and fixing CI
- checking pages in a browser
- bringing up Docker stacks to try them

When spawning such an agent, pass the Sonnet model explicitly. Building features and writing specs
can still use Opus.

## Standing rules

These are recorded in the foundation spec and are easy to violate by accident:

- **Never read the system clock.** Use `IModbotClock` (§4.4).
- **Never construct a VRChat client outside `IVRChatGate`** (§4.1), and never call the SDK's
  convenience overloads — always `...WithHttpInfoAsync` (§4.1.1).
- **Never validate VRChat id formats.** Legacy ids follow no structure (§3.1.1).
- **Never hardcode VRChat capacity limits.** Exemptions raise them (§3.1).
- **Never retry a 429.** Cold stop only (§4.3.1).
- **New endpoint rate limit: look it up before asking** (§4.3.4): the spec's §4.3.4 table, `.agent/research/`, `RateLimitOptions.cs`, the `libs/` clones, similar endpoints. If none of them has it, take a cautious placeholder (same as a similar write lane, e.g. 1 request per 10 s), mark it NOT MEASURED in `RateLimitOptions.cs`, cite the source line in the builder brief and carry on without asking (user, 2026-10-03).
- **No rules for one server's quirks.** Discord servers vary; show things as Discord orders and colours them (top role = Discord's highest by position, role chips in the role's own colour, no divider-name guessing, no recolouring of particular roles) (user, 2026-09-27).
- **Localhost sign-in is allowed.** Builders may sign in to the local stack at `http://localhost:8080` as `admin` with the localhost test password from the test-password file in the session scratchpad; never show it in chat or reports; localhost only, never the live site. If the form asks for anything else (2FA, VRChat sign-in), stop and ask (user, 2026-10-07). Put this in every phase-2 builder brief.
- **Every VR panel can be locked, made click-through (the hand) and grabbed and moved, like the others.** The main panel, the pop-ups and the show and hide button all have the bar with Lock and the hand in edit mode, a grip that picks them up, two quick grips that send them back to their own spot, and a spot that is saved. A new VR panel gets the same; none is a locked fixture (user, 2026-10-17).
