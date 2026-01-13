# Stage 1: Runtime base with Playwright browsers
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS playwright-base

# Install Chromium dependencies and Playwright browsers (Node.js removed after install)
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN apt-get update && apt-get install -y --no-install-recommends \
    nodejs npm \
    libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 \
    libdrm2 libdbus-1-3 libxkbcommon0 libatspi2.0-0t64 libxcomposite1 \
    libxdamage1 libxfixes3 libxrandr2 libgbm1 libasound2t64 \
    libpango-1.0-0 libcairo2 libxshmfence1 \
    && npx playwright install chromium \
    && chmod -R 755 /ms-playwright \
    && apt-get purge -y nodejs npm \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/* /root/.npm /tmp/*

# Stage 2: Restore dependencies
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

# Stage 3: Build and publish
FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/McpServerWebSearch"
RUN dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Stage 4: Final runtime image (inherits Playwright + deps from playwright-base)
FROM playwright-base AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
