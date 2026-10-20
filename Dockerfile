# ==============================================================================
# Multi-Stage Dockerfile for Checkers Chinook & KingsRow REST Web API (.NET 8)
# Environment: Linux x86_64 container with Wine64 support for KingsRow C++ DLL
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build Application and Worker
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src


RUN apt-get update && \
    apt-get install -y --no-install-recommends gcc-mingw-w64-x86-64 && \
    rm -rf /var/lib/apt/lists/*


COPY CheckersApp/Checkers.Api/Checkers.Api.csproj CheckersApp/Checkers.Api/
COPY CheckersApp/CheckersApp.slnx CheckersApp/


RUN dotnet restore CheckersApp/Checkers.Api/Checkers.Api.csproj


COPY CheckersApp/ CheckersApp/


RUN mkdir -p /app/Workers && \
    x86_64-w64-mingw32-gcc -O3 -s CheckersApp/ChinookWorker/chinook.c -o /app/Workers/chinook.exe


COPY CheckersApp/Engines/ /app/Workers/


WORKDIR /src/CheckersApp/Checkers.Api
RUN dotnet publish -c Release -p:SkipWorkerBuild=true -o /app/publish


RUN rm -rf /app/publish/Workers && cp -r /app/Workers /app/publish/Workers

# ------------------------------------------------------------------------------
# Stage 2: Production Runtime with Wine64
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app

ENV DEBIAN_FRONTEND=noninteractive


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


ENV WINEARCH=win64 \
    WINEDEBUG=-all \
    DISPLAY="" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0


RUN wineboot --init || true


COPY --from=build /app/publish .


EXPOSE 10000
EXPOSE 8080


ENTRYPOINT ["sh", "-c", "exec dotnet Checkers.Api.dll --urls http://0.0.0.0:${PORT:-10000}"]