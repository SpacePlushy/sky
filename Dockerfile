# syntax=docker/dockerfile:1

# The dashboard's static files.
FROM node:24-bookworm-slim AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# The API. .dockerignore keeps appsettings.Local.json and every other private file out of this
# stage, and the project never publishes it either.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
RUN dotnet publish src/Sky.Api/Sky.Api.csproj --configuration Release --output /app

# Runtime. Ubuntu noble includes tzdata, which the API needs to resolve the observer's IANA zone.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
COPY deploy/demo-cache/ ./demo-cache/

# The container never contacts CelesTrak. It reads a cache folder mounted read-only: the recorded
# demo data by default, or the host CLI's cache, which only the CLI refreshes. That keeps one
# request history per machine and needs no lock across the container boundary.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    SKY_CelesTrak__Offline=true \
    SKY_CelesTrak__CacheDirectory=/app/demo-cache \
    SKY_Clock__StartUtc=2026-09-24T04:00:00Z

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sky.Api.dll"]
