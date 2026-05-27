# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project files first for layer-cached restore
COPY TradingCsvProcessor.Domain/TradingCsvProcessor.Domain.csproj               TradingCsvProcessor.Domain/
COPY TradingCsvProcessor.Application/TradingCsvProcessor.Application.csproj     TradingCsvProcessor.Application/
COPY TradingCsvProcessor.Infrastructure/TradingCsvProcessor.Infrastructure.csproj TradingCsvProcessor.Infrastructure/
COPY TradingCsvProcessor.API/TradingCsvProcessor.API.csproj                      TradingCsvProcessor.API/

RUN dotnet restore TradingCsvProcessor.API/TradingCsvProcessor.API.csproj

# Copy everything else and publish
COPY . .
RUN dotnet publish TradingCsvProcessor.API/TradingCsvProcessor.API.csproj \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    /p:UseAppHost=false

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Non-root user for least-privilege execution
RUN groupadd -r appgroup && useradd -r -g appgroup appuser

COPY --from=build /app/publish .

# Ensure writable directories exist and are owned by the app user
RUN mkdir -p /app/uploads /app/logs && chown -R appuser:appgroup /app

USER appuser

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "TradingCsvProcessor.API.dll"]
