# Modbot.My

`my.modbot.co`: the page where a moderator's servers are registered, `/go` to open one of them, and
the `/admin` area. The web app lives in `src/Modbot.My.Web` and is built into this project's
`wwwroot`. The image is built by `src/Modbot.My/Dockerfile` from the repository root.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `DATABASE_URL` | Yes | — | PostgreSQL, as a `postgres://user:password@host:5432/database` URL or a keyword connection string. It refuses to start without it. |
| `ROOT_API_KEY` | No | none | The key that signs in to `/admin` and unlocks the endpoints that read registered instances. Unset means those endpoints refuse everyone. It is never stored in the browser. |
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. |
| `LOG_LEVEL` | No | `Information` | The lowest level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. |

The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only port setting.

On Railway: leave Root Directory empty and set `RAILWAY_DOCKERFILE_PATH=src/Modbot.My/Dockerfile`.
