# Modbot.Landing

`modbot.co`, the landing site. The web app lives in `src/Modbot.Landing/Web` and is prerendered into
this project's `wwwroot`. The image is built by `src/Modbot.Landing/Dockerfile` from the repository
root. It has no database.

## Pages

| Address | Built from | Notes |
|---|---|---|
| `/` | `Web/index.html` and `Web/src/pages/Home.tsx` | |
| `/features` | `Web/features.html` | |
| `/instances` | `Web/instances.html` | Every group whose Modbot reports its open public instances. The list is read from Modbot Cloud here, on the server, and held for a minute; the browser asks this site at `/api/instances`, so the Cloud key never reaches a page. With the two Cloud variables unset the page says no groups are listed and Cloud is never asked. |
| `/self-host` | `Web/self-host.html` | |
| `/about` | `Web/about.html` | The people come from `Web/src/data/about.json`, and the Contributors section also asks GitHub at build time. |
| `/license` | `Web/license.html` | The `LICENSE` file at the repository root. |
| `/privacy` | `Web/privacy.html` | `PRIVACY_POLICY.md` from the repository root, turned into HTML at build time. While that file does not exist the address is a 404 and the footer leaves the link out. |
| `/discord` | `Web/discord.html` | Sent on to `MODBOT_DISCORD_URL`. Without one, the page saying there is no invite yet. |
| `/github` | nothing | Sent on to `MODBOT_GITHUB_URL`. |
| `/rooms` | nothing | The instances page's address until 2026-09-17. A permanent redirect to `/instances`. |

Anything else is the built not-found page.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |
| `MODBOT_CLOUD_PROXY_URL` | No | none | The Modbot Cloud `/instances` reads from, as a full `http` or `https` address. Anything else is ignored. |
| `MODBOT_CLOUD_API_KEY` | No | none | The key sent to that Cloud, its `INSTANCES_API_KEY`. Read on the server only; never written into a page. |
| `MODBOT_MY_URL` | No | `https://my.modbot.co` | Where the server selector is. Every link on the page that points at it is rewritten to this address as the page is served. |
| `MODBOT_DISCORD_URL` | No | none | Where `/discord` sends people. Without it, `/discord` says there is no invite yet and points at GitHub. |
| `MODBOT_GITHUB_URL` | No | `https://github.com/Modbot/Modbot` | Where `/github` sends people. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. |
| `LOG_LEVEL` | No | `Information` | The lowest level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. |

Every address above must be a whole `http` or `https` address; anything else means the default. They
are read when the server starts, so the pages are built once and the addresses can change without
building them again.

`PORT` is the only variable it needs to start. The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT`
is the only port setting.

On Railway: leave Root Directory empty, set `RAILWAY_DOCKERFILE_PATH=src/Modbot.Landing/Dockerfile`,
and set the health check to `/health/ready`.
