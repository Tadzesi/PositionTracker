# PositionTracker: web app build + API image. Build context: the repository root.
# Base images are pinned by digest (tag kept for reading): a rebuild uses exactly the reviewed images, and an update is a
# deliberate change (docker buildx imagetools inspect <image:tag> prints the current digest).
FROM node:24-alpine@sha256:8a32c8cf649a49915a66e81e7a609223c708d0adabf288ee3f4077d765542f85 AS web
WORKDIR /repo/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
ARG VITE_BASE=/
ENV VITE_BASE=$VITE_BASE
# vite.config.ts writes to ../src/PositionTracker.Api/wwwroot
RUN mkdir -p /repo/src/PositionTracker.Api && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /repo
COPY Directory.Build.props PositionTracker.sln ./
COPY src/ src/
COPY --from=web /repo/src/PositionTracker.Api/wwwroot src/PositionTracker.Api/wwwroot
RUN dotnet publish src/PositionTracker.Api/PositionTracker.Api.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS run
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "PositionTracker.Api.dll"]
