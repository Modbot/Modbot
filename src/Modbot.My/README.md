# Modbot.My

`my.modbot.co`: the page where a moderator's Modbot servers are remembered, and `/go` to open one of
them. The web app lives in `src/Modbot.My.Web` and is built into this project's `wwwroot`. The image
is built by `src/Modbot.My/Dockerfile` from the repository root.

**It has no database.** Everything it shows comes from Modbot Cloud, which it reads with a key that
stays on the server. `/admin` and the instance registry moved to `cloud.modbot.co` on 2026-09-16, and
`DATABASE_URL` and `ROOT_API_KEY` are no longer read.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `MODBOT_CLOUD_API_KEY` | Yes | — | The key sent to Modbot Cloud as `Authorization: Bearer`. It refuses to start without it. Server-side only: it is never returned by an endpoint, never logged and never reaches a browser. Set it to the same value as Cloud's `PROXY_API_KEY`. |
| `MODBOT_CLOUD_PROXY_URL` | No | `https://cloud.modbot.co` | Which Modbot Cloud to read from. Anything that is not a full `http` or `https` address means the default. |
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. |
| `LOG_LEVEL` | No | `Information` | The lowest level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. |

## Routes

| Route | What it does |
|---|---|
| `/`, `/register`, `/go` | The app. A `url` on any of them is noted through Cloud. |
| `POST /api/local-register` | The app's own save once it has rendered. Passed to Cloud. `204` once Cloud has it; `503` when Cloud did not take it, and the app keeps the address to send again later. |
| `GET /api/my-instances` | The servers Cloud has seen from this address. `503` when Cloud could not be asked, so the app keeps the list it last had instead of showing nothing. |
| `/termlists/…` | Permanent redirects to Cloud, where the term lists now live. |
| `/health/live`, `/health/ready` | Both answer while the process is up. |

`/api/local-register` and `/api/my-instances` are limited per IP address — 30 saves and 60 reads an
hour — so that one address cannot make this service hammer Cloud. Serving a page is never refused for
being over the limit; the visit simply is not counted.

A page is served without waiting for Cloud. The visit it carries in `url` is noted in the background,
and the app notes it again once it has rendered — that second note is the one the browser keeps in
`localStorage` and sends again, with a growing wait between tries, until Cloud takes it.

The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only port setting.

On Railway: leave Root Directory empty and set `RAILWAY_DOCKERFILE_PATH=src/Modbot.My/Dockerfile`.
