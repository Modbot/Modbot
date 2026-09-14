# syntax=docker/dockerfile:1
#
# my.modbot.co: the instance selector, the register page, the instance registry and Modbot Hub.
#
# The build context is THIS folder (src/Modbot.My), not the repository root. The project references
# no other project and no NuGet package, so it builds on its own:
#
#   docker build -t modbot-my src/Modbot.My
#
# On Railway, set the service's Root Directory to /src/Modbot.My and it picks this file up.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Modbot.My.csproj ./
RUN dotnet restore Modbot.My.csproj

COPY . ./

# InvariantGlobalization normally comes from the repository's Directory.Build.props, which is
# outside this build context. Alpine ships without ICU, so without it .NET fails at startup.
RUN dotnet publish Modbot.My.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        -p:InvariantGlobalization=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# The base image sets ASPNETCORE_HTTP_PORTS=8080; Program.cs binds from PORT instead, and leaving
# both set makes Kestrel warn about the override on every start.
ENV ASPNETCORE_HTTP_PORTS=

COPY --from=build /app/ ./

USER $APP_UID

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --quiet --spider "http://127.0.0.1:${PORT:-8080}/health/live" || exit 1

# Content root is the working directory, which is where publish put wwwroot/ and termlists/.
ENTRYPOINT ["dotnet", "/app/Modbot.My.dll"]
