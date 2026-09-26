# Modbot.Server

The Modbot server: the API, the web app, and the VRChat and Discord sync, all in one process. It is
built by the `Dockerfile` at the repository root.

Almost everything is configured in the app (the setup wizard and Settings) and stored in the
database. The environment only holds what is needed before the database can be reached, plus a few
deployment switches.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `DATABASE_URL` | Yes | — | PostgreSQL connection, as a `postgres://user:password@host:5432/database` URL or a keyword connection string. Modbot refuses to start without it. |
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value (not 1–65535) falls back to 8080. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send structured logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. Log files and Seq are unaffected. |
| `LOG_LEVEL` | No | `Information` | The lowest level written anywhere, the Logs page in the app included: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. `Debug` adds a line per request answered and a line per database query. |
| `MODBOT_DEBUG_LOGGING` | No | off | `1`, `true`, `yes` or `on` turns on the Debug log streams while the console stays at Information. `LOG_LEVEL` wins over it. |
| `MODBOT_CLOUD_ENDPOINT` | No | `https://cloud.modbot.co` | The Modbot Cloud this server talks to for its own use: the term lists, a report every six hours, the instances its group has open, and its own log. A value that is not a full `http` or `https` address means the default. Companions are not told it. |
| `MODBOT_CLOUD_DISABLED` | No | off | `1`, `true`, `yes` or `on`: this server does not talk to Modbot Cloud at all — no report, no term list downloads, no registration, no instances listed and no log sent. This is how usage reporting is turned off; there is no toggle in the setup wizard or in Settings. It does not change what companions send. |
| `MODBOT_MY_URL` | No | `https://my.modbot.co` | Where the server selector is, for the **Add to my.modbot.co** links. A value that is not a full `http` or `https` address means the default. Modbot never calls it. |
| `MODBOT_DEMO` | No | off | `1`, `true`, `yes` or `on` starts this server as a public demo: made-up data, no sign-in, every visitor an administrator. Ignored, with nothing seeded or removed, once a staff account exists or the setup wizard has been finished. |
| `MODBOT_DEMO_RESET_HOURS` | No | `24` | Hours between automatic resets of the demo's data. `0` never resets. Anything but a whole number from 0 to 8760 means 24. Only read while `MODBOT_DEMO` is on. |

## Read once, to fill in the setup wizard

Railway sets the first five when a bucket is added to the service. Modbot reads them only to
pre-fill the evidence storage step the first time it is set up. You still confirm and save, and
later changes to them are ignored. `BUCKET`, `ENDPOINT`, `ACCESS_KEY_ID` and `SECRET_ACCESS_KEY` must
all be present for the pre-fill to appear.

| Variable | Pre-fills |
|---|---|
| `BUCKET` | Bucket name |
| `ENDPOINT` | S3 endpoint |
| `ACCESS_KEY_ID` | Access key id |
| `SECRET_ACCESS_KEY` | Secret access key |
| `REGION` | Region (optional) |
| `RAILWAY_PUBLIC_DOMAIN` | The suggested public address in Settings |

## Read to describe the deployment

The hosting platform sets these, not you. Modbot reads them to show the host, version commit and
release branch on Settings → Data → Deployment.

| Variable | Used for |
|---|---|
| `RAILWAY_GIT_COMMIT_SHA`, `GITHUB_SHA` | Version commit, when the build didn't record one |
| `RAILWAY_GIT_BRANCH`, `GITHUB_HEAD_REF`, `GITHUB_REF_NAME` (with `GITHUB_REF_TYPE=branch`) | Release branch, when the build didn't record one |
| `RAILWAY_ENVIRONMENT`, `RAILWAY_PROJECT_ID`, `RAILWAY_SERVICE_ID` | Host: Railway |
| `FLY_APP_NAME`, `FLY_ALLOC_ID`, `FLY_MACHINE_ID` | Host: Fly.io |
| `RENDER`, `RENDER_SERVICE_ID`, `RENDER_INSTANCE_ID` | Host: Render |
| `DYNO`, `HEROKU_APP_ID` | Host: Heroku |
| `VERCEL`, `VERCEL_ENV` | Host: Vercel |
| `KOYEB_APP_NAME`, `KOYEB_SERVICE_ID` | Host: Koyeb |
| `NF_INSTANCE_ID`, `NF_PROJECT_ID` | Host: Northflank |
| `CONTAINER_APP_NAME`, `CONTAINER_APP_REVISION` | Host: Azure Container Apps |
| `AWS_APP_RUNNER_SERVICE_ID` | Host: AWS App Runner |
| `ECS_CONTAINER_METADATA_URI_V4`, `ECS_CONTAINER_METADATA_URI` | Host: AWS ECS / Fargate |
| `KUBERNETES_SERVICE_HOST` | Host: Kubernetes |

## Build settings

| Setting | Where | What it does |
|---|---|---|
| `RAILWAY_GIT_COMMIT_SHA`, `RAILWAY_GIT_BRANCH` | Docker build args (`--build-arg`) | Recorded as the version commit and release branch. Railway passes them automatically. |
| `ModbotCommit`, `ModbotBranch` | MSBuild (`-p:ModbotCommit=...`) | The same, and they win over everything else. Without them, a local build asks git. |
| `ModbotRelease` | MSBuild (`-p:ModbotRelease=2026.9.0`) | Stamps the release version on every assembly. |
| `ModbotOpenApiDocument` | MSBuild (`-p:ModbotOpenApiDocument=false`) | Stops the build rewriting `docs/openapi/modbot.json`, the OpenAPI document the docs site is built from. Docker builds never write it. |

The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only port setting.

## Docker Compose

`docker-compose.yml` in the repository root runs this project with PostgreSQL 16 and Seq. Its
settings are listed in `.env.example`:

```bash
cp .env.example .env   # set POSTGRES_PASSWORD and SEQ_ADMIN_PASSWORD
docker compose up -d --build
```

| Variable in `.env` | Required | Default | What it does |
|---|---|---|---|
| `POSTGRES_PASSWORD` | Yes | — | The database password, used by PostgreSQL and in `DATABASE_URL`. It must not contain `;`. |
| `SEQ_ADMIN_PASSWORD` | Yes | — | Seq's `admin` password, set the first time Seq starts. |
| `POSTGRES_DB`, `POSTGRES_USER` | No | `modbot` | The database name and user. |
| `MODBOT_PORT` | No | `8080` | The port Modbot is published on. |
| `SEQ_PORT` | No | `5380` | The port the Seq UI is published on, on 127.0.0.1 only. |
| `MODBOT_DEBUG_LOGGING`, `MODBOT_CLOUD_ENDPOINT`, `MODBOT_CLOUD_DISABLED`, `MODBOT_MY_URL` | No | — | Passed to Modbot, as described above. |
| `MODBOT_COMMIT`, `MODBOT_BRANCH` | No | — | Passed as the build args that show the version commit and release branch. |

Volumes: `modbot-logs` (`/app/logs`), `modbot-data` (`/app/data`, for evidence on disk),
`postgres-data` and `seq-data`.
