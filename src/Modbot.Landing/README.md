# Modbot.Landing

`modbot.co`, the landing page. The web app lives in `src/Modbot.Landing/Web` and is prerendered into
this project's `wwwroot`. The image is built by `src/Modbot.Landing/Dockerfile` from the repository
root. It has no database.

`/rooms` lists every group whose Modbot reports its open public rooms. The list is read from Modbot
Cloud here, on the server, and held for a minute; the browser asks this site at `/api/rooms`, so the
Cloud key never reaches a page. With the two Cloud variables unset the page says no groups are
listed and Cloud is never asked.

`/privacy` is `PRIVACY_POLICY.md` from the repository root, turned into HTML at build time. While
that file does not exist the route is a 404 and the footer leaves the link out.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |
| `MODBOT_CLOUD_PROXY_URL` | No | none | The Modbot Cloud `/rooms` reads from, as a full `http` or `https` address. Anything else is ignored. |
| `MODBOT_CLOUD_API_KEY` | No | none | The key sent to that Cloud — its `ROOMS_API_KEY`. Read on the server only; never written into a page. |
| `MODBOT_MY_URL` | No | `https://my.modbot.co` | Where the instance selector is. Every link on the page that points at it is rewritten to this address as the page is served. A value that is not a full `http` or `https` address means the default. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. |
| `LOG_LEVEL` | No | `Information` | The lowest level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. |

`PORT` is the only variable it needs to start. The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only
port setting.

On Railway: leave Root Directory empty, set `RAILWAY_DOCKERFILE_PATH=src/Modbot.Landing/Dockerfile`,
and set the health check to `/health/ready`.
