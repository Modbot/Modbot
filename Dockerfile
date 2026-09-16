# syntax=docker/dockerfile:1
#
# One container: the SPA, the API, the Discord bot and the sync scheduler (spec 2.5). The build
# context is the repository root, because the image needs both the .NET solution and the Vite
# project and they are siblings under src/.

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — the SPA. Vite writes it straight into Modbot.Host/wwwroot (see
# src/Modbot.Web/vite.config.ts), which the publish stage then picks up as static web assets.
# ─────────────────────────────────────────────────────────────────────────────
FROM node:24-alpine AS web
WORKDIR /src

# Lockfile first: dependencies change far less often than source, and this layer is the slow one.
COPY src/Modbot.Web/package.json src/Modbot.Web/package-lock.json src/Modbot.Web/
RUN cd src/Modbot.Web && npm ci

COPY src/Modbot.Web/ src/Modbot.Web/
RUN cd src/Modbot.Web && npm run build

# ─────────────────────────────────────────────────────────────────────────────
# Stage 2 — publish. Project files first so a source-only change does not re-resolve NuGet.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Modbot.Shared/Modbot.Shared.csproj src/Modbot.Shared/
COPY src/Modbot.Core/Modbot.Core.csproj src/Modbot.Core/
COPY src/Modbot.VRChat/Modbot.VRChat.csproj src/Modbot.VRChat/
COPY src/Modbot.Analytics/Modbot.Analytics.csproj src/Modbot.Analytics/
COPY src/Modbot.Evidence/Modbot.Evidence.csproj src/Modbot.Evidence/
COPY src/Modbot.Discord/Modbot.Discord.csproj src/Modbot.Discord/
COPY src/Modbot.AI/Modbot.AI.csproj src/Modbot.AI/
COPY src/Modbot.Demo/Modbot.Demo.csproj src/Modbot.Demo/
COPY src/Modbot.Api/Modbot.Api.csproj src/Modbot.Api/
COPY src/Modbot.Host/Modbot.Host.csproj src/Modbot.Host/
RUN dotnet restore src/Modbot.Host/Modbot.Host.csproj

COPY src/ src/
COPY --from=web /src/src/Modbot.Host/wwwroot/ src/Modbot.Host/wwwroot/

# The commit and branch shown on the Deployment card. .git is not in the build context, so the build
# cannot ask git; Railway supplies these to a Dockerfile that declares them, and anyone else can pass
# them with --build-arg. They reach MSBuild as environment variables (see Modbot.Host.csproj).
# Declared here, after the restore and the source copy, so a new commit does not throw away those
# layers. Unset is fine: the running container falls back to the same variables at runtime.
ARG RAILWAY_GIT_COMMIT_SHA
ARG RAILWAY_GIT_BRANCH

# The release version, YYYY.M.PATCH, for a build that is one. The host image workflow passes it
# from a host-v tag; anything else leaves it unset and the build carries the development version.
# Passed as a property rather than read from the environment, because Directory.Build.props keys
# the version stamping on the ModbotRelease property and an empty one must mean "not a release".
ARG MODBOT_RELEASE

RUN dotnet publish src/Modbot.Host/Modbot.Host.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        ${MODBOT_RELEASE:+-p:ModbotRelease=$MODBOT_RELEASE}

# ─────────────────────────────────────────────────────────────────────────────
# Stage 3 — runtime.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Npgsql probes for GSSAPI at startup. Without krb5-libs the alpine loader prints
# "Cannot load library libgssapi_krb5.so.2" to stderr before Serilog is even involved -- harmless,
# unsuppressable, and the first thing an operator sees in the dashboard on every boot.
RUN apk add --no-cache krb5-libs

# The base image sets ASPNETCORE_HTTP_PORTS=8080, which Modbot then overrides from PORT -- and
# Kestrel warns about the override on every start. Cleared so the warning does not appear; PORT
# remains the only thing that decides the port.
ENV ASPNETCORE_HTTP_PORTS=

# busybox wget drives the HEALTHCHECK below; nothing else is added to the runtime image.
COPY --from=build /app/ ./

# Serilog writes six streams into ./logs relative to the content root, and evidence stored on disk
# goes under ./data, so both must be writable by the unprivileged user the container runs as.
# Creating them here also means a new named volume mounted on either starts out owned by that user
# rather than by root. $APP_UID is set by the base image.
RUN mkdir -p /app/logs /app/data/evidence     && chown -R $APP_UID:0 /app/logs /app/data     && chmod -R g+rwX /app/logs /app/data
USER $APP_UID

# Documentation only -- the real port comes from PORT at runtime. It is deliberately not an ENV
# line: the value belongs to the host that starts the container, is unset while the image is being
# built, and baking a default here would make an image that ignores the platform's assignment.
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget --quiet --spider "http://127.0.0.1:${PORT:-8080}/health/live" || exit 1

# Shell form so ${PORT} is resolved when the container starts. The default matches
# ModbotEnvironment's, and exec keeps dotnet as PID 1 so SIGTERM reaches it and shutdown is clean.
ENTRYPOINT ["/bin/sh", "-c", "export PORT=\"${PORT:-8080}\"; exec dotnet /app/Modbot.Host.dll"]
