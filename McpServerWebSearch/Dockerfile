# syntax=docker/dockerfile:1

# Stage 1: Runtime base with Playwright browsers
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS playwright-base

# Install Chromium dependencies and Playwright browsers (Node.js removed after install)
# NOTE: playwright version must match Microsoft.Playwright in Infrastructure.csproj
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN apt-get update && apt-get install -y --no-install-recommends \
    nodejs npm \
    libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 \
    libdrm2 libdbus-1-3 libxkbcommon0 libatspi2.0-0t64 libxcomposite1 \
    libxdamage1 libxfixes3 libxrandr2 libgbm1 libasound2t64 \
    libpango-1.0-0 libcairo2 libxshmfence1 \
    && npx playwright@1.58.0 install chromium \
    && chmod -R 755 /ms-playwright \
    && apt-get purge -y nodejs npm \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/* /root/.npm /tmp/*

# Stage 2: Restore dependencies
FROM base-sdk:latest AS dependencies
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

# Stage 3: Publish
FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerWebSearch/", "McpServerWebSearch/"]
WORKDIR "/src/McpServerWebSearch"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Stage 4: Final runtime image (inherits Playwright + deps from playwright-base)
FROM playwright-base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN chmod -R +x /app/.playwright/node/
USER $APP_UID
ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
