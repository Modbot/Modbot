# syntax=docker/dockerfile:1
#
# cloud.modbot.co: the event backup API desktop clients send to, and /admin.
#
# Build from the repository root, like the other images, because package versions are pinned once in
# Directory.Packages.props:
#
#   docker build -f src/Modbot.Cloud/Dockerfile -t modbot-cloud .
#
# On Railway it is its own service, not part of .railway/railway.ts. Leave the Root Directory empty,
# set RAILWAY_DOCKERFILE_PATH=src/Modbot.Cloud/Dockerfile and point the health check at /health/ready.
# It needs two Postgres databases: DATABASE_URL (installs, sessions, settings) and
# DATABASE_ENGINE_URL (backed-up events). ROOT_API_KEY unlocks /admin.

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — the web app. Vite writes it straight into Modbot.Cloud/wwwroot (see
# src/Modbot.Cloud/Web/vite.config.ts), which the publish stage then copies in.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:24-alpine AS web
WORKDIR /src

# Lockfile first: dependencies change far less often than source, and this layer is the slow one.
COPY src/Modbot.Cloud/Web/package.json src/Modbot.Cloud/Web/package-lock.json src/Modbot.Cloud/Web/
RUN cd src/Modbot.Cloud/Web && npm ci

COPY src/Modbot.Cloud/Web/ src/Modbot.Cloud/Web/
RUN cd src/Modbot.Cloud/Web && npm run build

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — publish.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Modbot.Cloud/Modbot.Cloud.csproj src/Modbot.Cloud/
RUN dotnet restore src/Modbot.Cloud/Modbot.Cloud.csproj

COPY src/Modbot.Cloud/ src/Modbot.Cloud/

# .dockerignore keeps any local wwwroot out of the context, so the only web app in the image is the
# one stage 1 just built.
COPY --from=web /src/src/Modbot.Cloud/wwwroot/ src/Modbot.Cloud/wwwroot/

RUN dotnet publish src/Modbot.Cloud/Modbot.Cloud.csproj \
        --configuration Release \
        --no-restore \
        --output /app

# ─────────────────────────────────────────────────────────────────────────────
# Stage 3 — runtime.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Npgsql probes for GSSAPI at startup; without krb5-libs the alpine loader prints an error on every
# boot.
RUN apk add --no-cache krb5-libs

# The base image sets ASPNETCORE_HTTP_PORTS=8080; Program.cs binds from PORT instead, and leaving
# both set makes Kestrel warn about the override on every start.
ENV ASPNETCORE_HTTP_PORTS=

COPY --from=build /app/ ./

USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD wget --quiet --spider "http://127.0.0.1:${PORT:-8080}/health/ready" || exit 1

# Content root is the working directory, which is where publish put wwwroot/.
ENTRYPOINT ["dotnet", "/app/Modbot.Cloud.dll"]
