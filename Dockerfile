# PositionTracker: web app build + API image. Build context: the repository root.
FROM node:24-alpine AS web
WORKDIR /repo/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --legacy-peer-deps
COPY web/ ./
# vite.config.ts writes to ../src/PositionTracker.Api/wwwroot
RUN mkdir -p /repo/src/PositionTracker.Api && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Directory.Build.props PositionTracker.sln ./
COPY src/ src/
COPY --from=web /repo/src/PositionTracker.Api/wwwroot src/PositionTracker.Api/wwwroot
RUN dotnet publish src/PositionTracker.Api/PositionTracker.Api.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "PositionTracker.Api.dll"]
