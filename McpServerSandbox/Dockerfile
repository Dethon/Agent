# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Install bash, python3, pip, common CLI tooling
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        bash \
        python3 \
        python3-pip \
        python3-venv \
        curl \
        ca-certificates \
        git \
        jq \
        unzip \
 && rm -rf /var/lib/apt/lists/*

# Create non-root sandbox_user with home at /home/sandbox_user
RUN useradd --create-home --home-dir /home/sandbox_user --shell /bin/bash sandbox_user \
 && mkdir -p /home/sandbox_user/.local/bin \
 && chown -R sandbox_user:sandbox_user /home/sandbox_user \
 && mkdir -p /.trash \
 && chown sandbox_user:sandbox_user /.trash

WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerSandbox/McpServerSandbox.csproj", "McpServerSandbox/"]
RUN dotnet restore "McpServerSandbox/McpServerSandbox.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerSandbox/", "McpServerSandbox/"]
WORKDIR "McpServerSandbox"
RUN dotnet publish "./McpServerSandbox.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Switch to non-root sandbox_user; bash exec inherits this user
USER sandbox_user
ENV HOME=/home/sandbox_user
ENV PATH=/home/sandbox_user/.local/bin:$PATH

ENTRYPOINT ["dotnet", "McpServerSandbox.dll"]
