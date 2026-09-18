# syntax=docker/dockerfile:1
#
# One container: the SPA, the API, the Discord bot and the sync scheduler (spec 2.5). The build
# context is the repository root, because the image needs both the .NET solution and the Vite
# project and they are siblings under src/.

# ─────────────────────────────────────────────────────────────────────────────
# Stage 1 — the SPA. Vite writes it straight into Modbot.Server/wwwroot (see
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
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Modbot.Shared/Modbot.Shared.csproj src/Modbot.Shared/
COPY src/Modbot.Core/Modbot.Core.csproj src/Modbot.Core/
COPY src/Modbot.VRChat/Modbot.VRChat.csproj src/Modbot.VRChat/
COPY src/Modbot.Analytics/Modbot.Analytics.csproj src/Modbot.Analytics/
COPY src/Modbot.Evidence/Modbot.Evidence.csproj src/Modbot.Evidence/
COPY src/Modbot.Discord/Modbot.Discord.csproj src/Modbot.Discord/
COPY src/Modbot.Moderation/Modbot.Moderation.csproj src/Modbot.Moderation/
COPY src/Modbot.AI/Modbot.AI.csproj src/Modbot.AI/
COPY src/Modbot.Demo/Modbot.Demo.csproj src/Modbot.Demo/
COPY src/Modbot.Api/Modbot.Api.csproj src/Modbot.Api/
COPY src/Modbot.Server/Modbot.Server.csproj src/Modbot.Server/
RUN dotnet restore src/Modbot.Server/Modbot.Server.csproj

COPY src/ src/
COPY --from=web /src/src/Modbot.Server/wwwroot/ src/Modbot.Server/wwwroot/

# The commit and branch shown on the Deployment card. .git is not in the build context, so the build
# cannot ask git; Railway supplies these to a Dockerfile that declares them, and anyone else can pass
# them with --build-arg. They reach MSBuild as environment variables (see Modbot.Server.csproj).
# Declared here, after the restore and the source copy, so a new commit does not throw away those
# layers. Unset is fine: the running container falls back to the same variables at runtime.
ARG RAILWAY_GIT_COMMIT_SHA
ARG RAILWAY_GIT_BRANCH

# The release version, YYYY.M.PATCH, for a build that is one. The host image workflow passes it
# from a host-v tag; anything else leaves it unset and the build carries the development version.
# Passed as a property rather than read from the environment, because Directory.Build.props keys
# the version stamping on the ModbotRelease property and an empty one must mean "not a release".
ARG MODBOT_RELEASE

RUN dotnet publish src/Modbot.Server/Modbot.Server.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        ${MODBOT_RELEASE:+-p:ModbotRelease=$MODBOT_RELEASE}

# ─────────────────────────────────────────────────────────────────────────────
# Stage 3 — runtime.
# ─────────────────────────────────────────────────────────────────────────────
# Debian rather than Alpine, since 2026-09-18. The deployed server segfaulted repeatedly under a
# page that opens tens of connections at once -- signal 11, no managed exception, nothing for the
# crash guard to catch, and one captured stack inside the socket engine's own type initialiser.
# The same image survived far heavier load locally, memory was never above a sixteenth of the
# limit, and no managed code in this image calls into anything native. What was left was the C
# library underneath, and musl is where .NET is least exercised. Debian is the supported ground.
# If this is ever revisited, the evidence is in .agent/research/2026-09-18-file-proxy-crash.md and
# 2026-09-18-live-socket-crash.md.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Npgsql probes for GSSAPI at startup; without the library the loader prints "Cannot load library
# libgssapi_krb5.so.2" to stderr before Serilog is even involved -- harmless, unsuppressable, and
# the first thing an operator sees in the dashboard on every boot. curl drives the HEALTHCHECK
# below, which busybox wget did on Alpine; Debian's image carries neither.
RUN apt-get update     && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl     && rm -rf /var/lib/apt/lists/*

# The base image sets ASPNETCORE_HTTP_PORTS=8080, which Modbot then overrides from PORT -- and
# Kestrel warns about the override on every start. Cleared so the warning does not appear; PORT
# remains the only thing that decides the port.
ENV ASPNETCORE_HTTP_PORTS=

COPY --from=build /app/ ./

# Serilog writes six streams into ./logs relative to the content root, and evidence stored on disk
# goes under ./data.
RUN mkdir -p /app/logs /app/data/evidence /app/data/dumps

# Runs as root, deliberately, since 2026-09-18. A mounted volume arrives owned by root whatever the
# image did at build time -- the mount replaces the directory and its ownership with it -- so an
# unprivileged user could not write into one. On Railway that showed up as crash dumps being
# switched on and the folder for them failing to be made, and it would have hit evidence on disk
# the same way. Dropping privileges is worth having, but it has to be done after the volume is
# mounted rather than before, which needs an entry point that starts as root and steps down. Until
# that exists, a container whose volume works beats a container that cannot write to it.
USER root

# Documentation only -- the real port comes from PORT at runtime. It is deliberately not an ENV
# line: the value belongs to the host that starts the container, is unset while the image is being
# built, and baking a default here would make an image that ignores the platform's assignment.
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --output /dev/null "http://127.0.0.1:${PORT:-8080}/health/live" || exit 1

# Shell form so ${PORT} is resolved when the container starts. The default matches
# ModbotEnvironment's, and exec keeps dotnet as PID 1 so SIGTERM reaches it and shutdown is clean.
ENTRYPOINT ["/bin/sh", "-c", "export PORT=\"${PORT:-8080}\"; exec dotnet /app/Modbot.Server.dll"]
