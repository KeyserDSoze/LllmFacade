# syntax=docker/dockerfile:1

# Admin SPA, served as static files by the API (same origin: no CORS, cookie session).
FROM node:22-bookworm-slim AS admin
WORKDIR /src/admin
COPY src/admin/package.json ./
RUN npm install --no-fund --no-audit
COPY src/admin/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src
COPY src/backend/ ./src/backend/
RUN dotnet publish src/backend/applications/LllmFacade.Api/LllmFacade.Api.csproj \
    -c Release -a $TARGETARCH --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DataProtection__KeysPath=/data/keys
WORKDIR /app
COPY --from=build /app ./
COPY --from=admin /src/admin/dist ./wwwroot
# Chromium matching Microsoft.Playwright plus its system libraries (installed as root, used by the app user).
# libatomic1 is required by the Node.js driver bundled with Microsoft.Playwright.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libatomic1 \
    && dotnet LllmFacade.Api.dll playwright install --with-deps chromium \
    && rm -rf /var/lib/apt/lists/* \
    && chmod -R a+rX /app/.playwright /ms-playwright \
    && mkdir -p /data/keys \
    && chown -R $APP_UID /data
USER $APP_UID
EXPOSE 8080
VOLUME ["/data/keys"]
ENTRYPOINT ["dotnet", "LllmFacade.Api.dll"]
