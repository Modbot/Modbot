# Modbot.Landing

`modbot.co`, the landing page. The web app lives in `src/Modbot.Landing/Web` and is prerendered into
this project's `wwwroot`. The image is built by `src/Modbot.Landing/Dockerfile` from the repository
root. It has no database.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |

`PORT` is the only variable it reads. The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only
port setting.

On Railway: leave Root Directory empty, set `RAILWAY_DOCKERFILE_PATH=src/Modbot.Landing/Dockerfile`,
and set the health check to `/health/ready`.
