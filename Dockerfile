# syntax=docker/dockerfile:1
# Сборка: docker buildx build --platform linux/amd64,linux/arm64 -t vds-service-updater .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY src/VdsServiceUpdater/VdsServiceUpdater.csproj src/VdsServiceUpdater/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-musl-arm64 || echo linux-musl-x64) \
 && dotnet restore src/VdsServiceUpdater -r $RID
COPY src/ src/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-musl-arm64 || echo linux-musl-x64) \
 && dotnet publish src/VdsServiceUpdater -c Release -r $RID --self-contained false --no-restore -p:Version=$VERSION -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# docker CLI + compose-плагин: именно они обрабатывают compose/stack-файлы
RUN apk add --no-cache docker-cli docker-cli-compose
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Запускается от root: доступ к docker.sock. Для non-root используйте user + group_add (GID группы docker).
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "VdsServiceUpdater.dll"]
