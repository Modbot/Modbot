# Modbot

Self-hosted moderation, analytics and automation for a single VRChat group.

Modbot caches what VRChat's API exposes but its own UI does not make usable, records the history
VRChat throws away, and gives your staff tools VRChat does not provide.

> **Modbot is not affiliated with or endorsed by VRChat Inc.**

## Status

**In development.** Nothing is deployable yet.

The design is complete and reviewable — see [`.agent/specs/`](.agent/specs/). Implementation starts
with the foundation milestone in [`.agent/plans/`](.agent/plans/).

## What it does

| | |
|---|---|
| **Search and sort** members, bans and invites | VRChat's lists are neither searchable nor sortable |
| **Rich audit log** | Every event parsed, with its data, and synced to Discord |
| **Moderation history** | Who was actioned, by whom, why — including per-moderator attribution VRChat itself discards |
| **Analytics** | Member growth, join and leave rates, moderator activity, time spent in your worlds |
| **In-instance monitoring** | A Windows client and SteamVR overlay reporting presence from your instances |
| **Discord integration** | Account linking, role sync, ban sync, and Discord-side analytics |
| **Segments and giveaways** | Query your members — *"regulars with 10+ hours this month, no bans"* — and draw verifiably |

## Deploying

Modbot runs as **one service plus one PostgreSQL database**. It needs exactly two environment
variables — `PORT` and `DATABASE_URL` — and everything else is configured in the app through a setup
wizard the first time you open it.

No Redis, no search server, no message broker, no hosted auth, no third-party account.

## Repository layout

| Path | Contents |
|---|---|
| [`.agent/specs/`](.agent/specs/) | Design documents, with the reasoning behind every decision |
| [`.agent/plans/`](.agent/plans/) | Implementation plans, task by task |
| [`.agent/research/`](.agent/research/) | Empirical findings about VRChat's API and logs |
| `docs/` | End-user documentation — setup, usage, self-hosting, API reference |
| `explore/` | A scratch tool for probing the live VRChat API. Not part of the product. |

## Contributing

Read [`CLAUDE.md`](CLAUDE.md) for working conventions, then the foundation spec in `.agent/specs/`.

Contributions are covered by [`CLA.md`](CLA.md) — you keep your copyright; the project gets a licence
broad enough to keep its licensing coherent over time.

## Licence

[AGPL-3.0](LICENSE). Modbot is open source: you may use, modify, self-host and fork it.

If you run a **modified** Modbot as a network service, you must offer its source to your users. That
is the clause that keeps hosted forks open, and it is the reason for choosing AGPL over a permissive
licence.

The **name and logo** are not granted by the licence — see [`TRADEMARK.md`](TRADEMARK.md). If you
fork, please pick your own name.
