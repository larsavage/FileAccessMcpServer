# syntax=docker/dockerfile:1

# =============================================================================
# FileAccessMcpServer — Dockerfile
# Мультистейдж-сборка: SDK для публикации, aspnet-образ для запуска.
#
# Конфигурация контейнера:
#   - смонтируйте config.json в /app/config.json (volume), ЛИБО
#   - задайте параметры через переменные окружения FILEACCESS__KEY
#     (двойное подчёркивание == разделитель ":", регистр не важен), например:
#       FILEACCESS__SANDBOXDIR=/data/sandbox
#       FILEACCESS__APIKEY=<secret>
#       FILEACCESS__RATELIMIT__ENABLED=true
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# restore с кэшированием слоёв
COPY FileAccessMcpServer.csproj .
RUN dotnet restore "FileAccessMcpServer.csproj"

# Остальной исходный код (config.json исключён через .dockerignore)
COPY . .
RUN dotnet publish "FileAccessMcpServer.csproj" -c "$BUILD_CONFIGURATION" -o /app/publish

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Приложение self-contained single-file; копируем публикацию и даём app права на каталог.
COPY --from=build /app/publish .
RUN chown -R app:app /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# liveness/readiness для k8s и прокси; /healthz не требует API-ключа.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl --silent --fail http://localhost:8080/healthz || exit 1

# Запуск от непривилегированного пользователя.
USER app

ENTRYPOINT ["./FileAccessMcpServer"]