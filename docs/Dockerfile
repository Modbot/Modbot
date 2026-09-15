# syntax=docker/dockerfile:1
#
# docs.modbot.co: the documentation site. A static site with a small Node server in front of it; no
# database and no settings beyond PORT.
#
# Build from the repository root, like the other images:
#
#   docker build -f docs/Dockerfile -t modbot-docs .
#   docker run --rm -p 8080:8080 modbot-docs
#
# On Railway, leave the service's Root Directory empty and set the variable
# RAILWAY_DOCKERFILE_PATH=docs/Dockerfile. Point the health check at /health.

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — the site. `next build` writes every page, the API reference and the search index to
# docs/out. The API reference is read from docs/openapi/modbot.json, which is committed, so this
# stage needs no .NET.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:24-alpine AS build
WORKDIR /src

# Lockfile first: dependencies change far less often than pages, and this layer is the slow one.
COPY docs/package.json docs/package-lock.json docs/
RUN cd docs && npm ci

COPY docs/ docs/
RUN cd docs && npm run build

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — runtime. Only the built files and the server script; no node_modules.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:24-alpine AS runtime
WORKDIR /app

COPY --from=build /src/docs/out/ out/
COPY --from=build /src/docs/scripts/serve.mjs scripts/serve.mjs

ENV NODE_ENV=production
USER node

# Documentation only -- the real port comes from PORT at runtime (8080 when it is unset).
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD wget --quiet --spider "http://127.0.0.1:${PORT:-8080}/health" || exit 1

CMD ["node", "scripts/serve.mjs"]
