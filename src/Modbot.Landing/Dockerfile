# syntax=docker/dockerfile:1
#
# modbot.co: the landing page. No database and no settings beyond PORT.
#
# Build from the repository root, like the other images, because package versions are pinned once in
# Directory.Packages.props and the privacy policy is read from the root:
#
#   docker build -f src/Modbot.Landing/Dockerfile -t modbot-landing .
#   docker run --rm -p 8080:8080 modbot-landing
#
# On Railway, leave the service's Root Directory empty and set the variable
# RAILWAY_DOCKERFILE_PATH=src/Modbot.Landing/Dockerfile. Point the health check at /health/ready.

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — the page. Vite writes it into Modbot.Landing/wwwroot and scripts/prerender.ts renders
# the words into the HTML (see src/Modbot.Landing/Web).
# ─────────────────────────────────────────────────────────────────────────────
FROM node:24-alpine AS web
WORKDIR /src

# Lockfile first: dependencies change far less often than source, and this layer is the slow one.
COPY src/Modbot.Landing/Web/package.json src/Modbot.Landing/Web/package-lock.json src/Modbot.Landing/Web/
RUN cd src/Modbot.Landing/Web && npm ci

# PRIVACY_POLICY.md becomes /privacy when it exists. The pattern lets the copy succeed while it does
# not: a COPY with a wildcard that matches nothing is allowed as long as another source matches.
COPY global.json PRIVACY_POLICY.m[d] ./

COPY src/Modbot.Landing/Web/ src/Modbot.Landing/Web/
RUN cd src/Modbot.Landing/Web && npm run build

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — publish.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Modbot.Landing/Modbot.Landing.csproj src/Modbot.Landing/
RUN dotnet restore src/Modbot.Landing/Modbot.Landing.csproj

COPY src/Modbot.Landing/ src/Modbot.Landing/

# .dockerignore keeps any local wwwroot out of the context, so the only page in the image is the one
# stage 1 just built.
COPY --from=web /src/src/Modbot.Landing/wwwroot/ src/Modbot.Landing/wwwroot/

RUN dotnet publish src/Modbot.Landing/Modbot.Landing.csproj \
        --configuration Release \
        --no-restore \
        --output /app

# ─────────────────────────────────────────────────────────────────────────────
# Stage 3 — runtime.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# The base image sets ASPNETCORE_HTTP_PORTS=8080; Program.cs binds from PORT instead (8080 when it is
# unset), and leaving both set makes Kestrel warn about the override on every start.
ENV ASPNETCORE_HTTP_PORTS=

COPY --from=build /app/ ./

USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget --quiet --spider "http://127.0.0.1:${PORT:-8080}/health/ready" || exit 1

# Content root is the working directory, which is where publish put wwwroot/.
ENTRYPOINT ["dotnet", "/app/Modbot.Landing.dll"]
