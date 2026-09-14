# syntax=docker/dockerfile:1
#
# my.modbot.co: the instance selector, the register and pair pages, the instance registry and
# Modbot Hub.
#
# Build from the repository root, like the main image, because package versions are pinned once in
# Directory.Packages.props:
#
#   docker build -f src/Modbot.My/Dockerfile -t modbot-my .
#
# On Railway, leave the service's Root Directory empty and set the variable
# RAILWAY_DOCKERFILE_PATH=src/Modbot.My/Dockerfile. The service needs DATABASE_URL and ROOT_API_KEY.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Modbot.My/Modbot.My.csproj src/Modbot.My/
RUN dotnet restore src/Modbot.My/Modbot.My.csproj

COPY src/Modbot.My/ src/Modbot.My/

RUN dotnet publish src/Modbot.My/Modbot.My.csproj \
        --configuration Release \
        --no-restore \
        --output /app

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

# Content root is the working directory, which is where publish put wwwroot/ and termlists/.
ENTRYPOINT ["dotnet", "/app/Modbot.My.dll"]
