# Modbot.Cloud

Modbot Cloud (`cloud.modbot.co`): receives the desktop clients' backup and keeps it, with an
`/admin` area. The web app lives in `src/Modbot.Cloud/Web` and is built into this project's
`wwwroot`. The image is built by `src/Modbot.Cloud/Dockerfile` from the repository root.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `DATABASE_URL` | Yes | — | Cloud's main PostgreSQL database: installs, admin sessions and settings. A `postgres://user:password@host:5432/database` URL or a keyword connection string. |
| `DATABASE_ENGINE_URL` | Yes | — | A second, separate PostgreSQL database for event storage. Same formats. |
| `ROOT_API_KEY` | No | none | The key that signs in to `/admin`. Unset means admin refuses everyone. |
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |

Cloud refuses to start when either database variable is missing, and names each missing one.
`/health/ready` checks both databases.

The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only port setting.

On Railway:
- leave Root Directory empty
- set `RAILWAY_DOCKERFILE_PATH=src/Modbot.Cloud/Dockerfile`
- set the health check to `/health/ready`
- add two Postgres services, and point each database variable at one of them
