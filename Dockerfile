# ==============================================================================
# Multi-Stage Dockerfile for Checkers Chinook & KingsRow REST Web API (.NET 8)
# Environment: Linux x86_64 container with Wine64 support for KingsRow C++ DLL
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build Application and Worker
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Install MinGW toolchain to compile native Windows x64 worker
RUN apt-get update && \
    apt-get install -y --no-install-recommends gcc-mingw-w64-x86-64 && \
    rm -rf /var/lib/apt/lists/*

# Copy project definition files for layer caching
COPY CheckersApp/Checkers.Api/Checkers.Api.csproj CheckersApp/Checkers.Api/
COPY CheckersApp/CheckersApp.slnx CheckersApp/

# Restore dependencies
RUN dotnet restore CheckersApp/Checkers.Api/Checkers.Api.csproj

# Copy all source files
COPY CheckersApp/ CheckersApp/

# 1. Compile native Win32 PE64 ChinookWorker (pure C, zero CoreCLR overhead, 100% Wine compatible)
RUN mkdir -p /app/Workers && \
    x86_64-w64-mingw32-gcc -O3 -s CheckersApp/ChinookWorker/chinook.c -o /app/Workers/chinook.exe

# 2. Deploy native KingsRow64.dll, egdb64.dll, tablebases (db/), and opening books
COPY CheckersApp/Engines/ /app/Workers/

# 3. Build Checkers.Api Web API for Linux
WORKDIR /src/CheckersApp/Checkers.Api
RUN dotnet publish -c Release -p:SkipWorkerBuild=true -o /app/publish

# Copy Workers folder into final publish payload
RUN rm -rf /app/publish/Workers && cp -r /app/Workers /app/publish/Workers

# ------------------------------------------------------------------------------
# Stage 2: Production Runtime with Wine64
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app

ENV DEBIAN_FRONTEND=noninteractive

# Install 64-bit Wine and diagnostics utilities
RUN dpkg --add-architecture amd64 && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        wine64 \
        wine \
        procps \
        curl \
    && ln -sf /usr/lib/wine/wine64 /usr/local/bin/wine64 \
    && ln -sf /usr/lib/wine/wine64 /usr/local/bin/wine \
    && rm -rf /var/lib/apt/lists/*

# Configure headless Wine environment
ENV WINEARCH=win64 \
    WINEDEBUG=-all \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_URLS=http://0.0.0.0:5114

# Pre-initialize Wine prefix at build time
RUN wineboot --init || true

# Copy published application and worker bundle
COPY --from=build /app/publish .

# Expose HTTP port for API, Swagger, and Web Test Bench
EXPOSE 5114

# Container health monitoring
HEALTHCHECK --interval=20s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:5114/healthz || exit 1

# Launch ASP.NET Core API server
ENTRYPOINT ["dotnet", "Checkers.Api.dll"]
