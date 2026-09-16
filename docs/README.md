# Modbot docs

`docs.modbot.co`: how to host, set up and use Modbot, and its API reference. A Next.js site built
with [Fumadocs](https://fumadocs.dev), exported as static files.

## Where things are

| Path | Contents |
|---|---|
| `content/docs/` | The pages, as MDX. Each folder's `meta.json` sets the order in the sidebar. |
| `openapi/modbot.json` | The OpenAPI document the API reference is made from. Written by building `src/Modbot.Server`; do not edit it by hand. |
| `content/docs/api/reference/` | The reference's introduction and section order. Its endpoint pages are made from the OpenAPI document at build time. |
| `app/`, `components/`, `lib/` | The site itself. |
| `scripts/serve.mjs` | The server the Docker image runs. |
| `scripts/check-links.mjs` | Checks every link in the built site. |

## Running it

Node 24.

```bash
cd docs
npm ci
npm run dev        # http://localhost:3000, reloads as you edit pages
```

## Building it

```bash
npm run build        # writes the whole site to out/
npm run check-links  # every internal link and #heading in out/ must exist
npm start            # serves out/ on PORT (default 8080)
npm run typecheck
npm run lint
```

The build is a static export. The API reference pages are rendered at build time from
`openapi/modbot.json`, and search runs in the browser from an index written at build time, so the
output needs nothing but a file server.

## Updating the API reference

Build the server and commit the document with the endpoint change:

```bash
dotnet build src/Modbot.Server
git add docs/openapi/modbot.json
```

CI fails when the committed document differs from what the build writes.

## Docker

Built from the repository root:

```bash
docker build -f docs/Dockerfile -t modbot-docs .
docker run --rm -p 8080:8080 modbot-docs
```

On Railway, leave Root Directory empty, set `RAILWAY_DOCKERFILE_PATH=docs/Dockerfile`, and set the
health check to `/health`.

## Environment variables

| Variable | Required | Default | What it does |
|---|---|---|---|
| `PORT` | No | `8080` | Port `scripts/serve.mjs` listens on. |

The build reads none. The site's public address, `https://docs.modbot.co`, is set in `lib/shared.ts`.
