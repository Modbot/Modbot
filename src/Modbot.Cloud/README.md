# Modbot.Cloud

Modbot Cloud (`cloud.modbot.co`):

- receives the companions' event backup and the log each Modbot deployment sends, and keeps both
- watches each of those deployments from outside and emails somebody when one goes quiet
- takes each Modbot server's report of the instances its group has open to everyone
- holds **accounts** — an email address and a password, confirmed by mail
- holds the **server registry**: Modbot servers register themselves, report every six hours, and can
  be claimed by the account that owns them
- holds the **showcase**: the sponsors and early adopters every Modbot shows on its Credits page,
  beside the contributors it reads from GitHub
- serves the **term lists** at `/termlists/index.json`, `/termlists/_schema.json` and
  `/termlists/{id}.json`, moved here from my.modbot.co on 2026-09-16 with their shapes unchanged
- answers my.modbot.co and the landing page under `/api/v1/site`, behind `PROXY_API_KEY` — including
  what a my.modbot.co register visit learned about a Modbot address by asking it
- holds the **mailing list**: the addresses people tick a box for while registering an account on a
  Modbot, with a link at `/unsubscribe` that takes them off it without an account
- has an `/admin` area behind `ROOT_API_KEY`

The web app lives in `src/Modbot.Cloud/Web` and is built into this project's `wwwroot`. The image is
built by `src/Modbot.Cloud/Dockerfile` from the repository root.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `DATABASE_URL` | Yes | — | Cloud's main PostgreSQL database: installs, admin sessions and settings. A `postgres://user:password@host:5432/database` URL or a keyword connection string. |
| `DATABASE_ENGINE_URL` | Yes | — | A second, separate PostgreSQL database for the events and the deployments' log lines. Same formats. |
| `ROOT_API_KEY` | No | none | The key that signs in to `/admin`. Unset means admin refuses everyone. |
| `INSTANCES_API_KEY` | No | none | The read-only key for `GET /api/v1/public-instances`, which is the key the landing page holds. `ROOT_API_KEY` opens that feed too; with neither set it refuses everyone. Its old name, `ROOMS_API_KEY`, is still read. |
| `PROXY_API_KEY` | No | none | The key my.modbot.co and the landing page send as `Authorization: Bearer`. It opens the endpoints under `/api/v1/site` and nothing else. Set it to the same value as my.modbot.co's `MODBOT_CLOUD_API_KEY`. Unset means those endpoints refuse everyone. |
| `RESEND_API_KEY` | No | none | The [Resend](https://resend.com) key Cloud sends its mail with: account mail, and the alerts about a Modbot that has gone quiet. **Unset means Cloud sends no mail**, so registering an account, confirming an address and resetting a password are all refused, and the instance checks run and record what they found without emailing anybody. |
| `MAIL_FROM` | With `RESEND_API_KEY` | — | The From address, such as `Modbot <noreply@modbot.co>`. Cloud refuses to start with a Resend key and no From address. |
| `CLOUD_PUBLIC_URL` | No | `https://cloud.modbot.co` | Where Cloud is reachable, for the links in its mail. |
| `GITHUB_TOKEN` | No | none | Reads the repository's contributors for the showcase. Not needed for a public repository; without it while it is private, the contributor list is empty. |
| `GITHUB_REPOSITORY` | No | `binn/Modbot` | The repository those contributors come from, as `owner/name`. |
| `PORT` | No | `8080` | Port to listen on. A missing or invalid value falls back to 8080. |
| `SEQ_URL` | No | none | A [Seq](https://datalust.co/seq) server to send logs to. Unset means no Seq. |
| `CONSOLE_LOG_MODE` | No | `serilog` | The shape of the console output: `serilog` (readable lines), `json` (Serilog's compact JSON) or `railway_json` (the JSON Railway parses). Case, spaces, hyphens and underscores are ignored; anything else means `serilog`. |
| `LOG_LEVEL` | No | `Information` | The lowest level written: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. An unknown value means `Information`. |

Cloud refuses to start when either database variable is missing, and names each missing one.
`/health/ready` checks both databases.

The image clears `ASPNETCORE_HTTP_PORTS`, so `PORT` is the only port setting.

On Railway:
- leave Root Directory empty
- set `RAILWAY_DOCKERFILE_PATH=src/Modbot.Cloud/Dockerfile`
- set the health check to `/health/ready`
- add two Postgres services, and point each database variable at one of them
