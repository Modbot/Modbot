# Modbot.Landing

`modbot.co`, the landing page. The web app lives in `src/Modbot.Landing/Web` and is prerendered into
this project's `wwwroot`. The image is built by `src/Modbot.Landing/Dockerfile` from the repository
root. It has no database.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. |
| `LOG_LEVEL` | No | `Information` | The lowest level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. |

`PORT` is the only variable it needs. The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only
port setting.

On Railway: leave Root Directory empty, set `RAILWAY_DOCKERFILE_PATH=src/Modbot.Landing/Dockerfile`,
and set the health check to `/health/ready`.
