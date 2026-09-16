# syntax=docker/dockerfile:1
#
# my.modbot.co: the web app (/, /register and /go). It has no database and reads everything from
# Modbot Cloud.
#
# Build from the repository root, like the main image, because package versions are pinned once in
# Directory.Packages.props and the web app is a sibling project under src/:
#
#   docker build -f src/Modbot.My/Dockerfile -t modbot-my .
#
# On Railway, leave the service's Root Directory empty and set the variable
# RAILWAY_DOCKERFILE_PATH=src/Modbot.My/Dockerfile. The service needs MODBOT_CLOUD_API_KEY.

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — the web app. Vite writes it straight into Modbot.My/wwwroot (see
# src/Modbot.My.Web/vite.config.ts), which the publish stage then copies in.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:24-alpine AS web
WORKDIR /src

# Lockfile first: dependencies change far less often than source, and this layer is the slow one.
COPY src/Modbot.My.Web/package.json src/Modbot.My.Web/package-lock.json src/Modbot.My.Web/
RUN cd src/Modbot.My.Web && npm ci

COPY src/Modbot.My.Web/ src/Modbot.My.Web/
RUN cd src/Modbot.My.Web && npm run build

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — publish.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
# Modbot.Shared carries the logging every Modbot program shares (CONSOLE_LOG_MODE, LOG_LEVEL).
COPY src/Modbot.Shared/Modbot.Shared.csproj src/Modbot.Shared/
COPY src/Modbot.My/Modbot.My.csproj src/Modbot.My/
RUN dotnet restore src/Modbot.My/Modbot.My.csproj

COPY src/Modbot.Shared/ src/Modbot.Shared/
COPY src/Modbot.My/ src/Modbot.My/

# .dockerignore keeps any local wwwroot out of the context, so the only web app in the image is the
# one stage 1 just built.
COPY --from=web /src/src/Modbot.My/wwwroot/ src/Modbot.My/wwwroot/

RUN dotnet publish src/Modbot.My/Modbot.My.csproj \
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
ENTRYPOINT ["dotnet", "/app/Modbot.My.dll"]
