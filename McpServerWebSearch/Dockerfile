# Stage to install Playwright browsers using SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base

# Install Playwright browsers to a shared location accessible by all users
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
WORKDIR /tmp/pw
RUN dotnet new console -n pwinstall \
    && cd pwinstall \
    && dotnet add package Microsoft.Playwright \
    && dotnet build \
    && pwsh ./bin/Debug/net10.0/playwright.ps1 install chromium \
    && chmod -R 755 /ms-playwright

RUN apt-get update && apt-get install -y --no-install-recommends \
    libnss3 \
    libnspr4 \
    libatk1.0-0t64 \
    libatk-bridge2.0-0t64 \
    libcups2t64 \
    libdrm2 \
    libdbus-1-3 \
    libxkbcommon0 \
    libatspi2.0-0t64 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2t64 \
    libpango-1.0-0 \
    libcairo2 \
    libxshmfence1 \
    && rm -rf /var/lib/apt/lists/*

USER $APP_UID

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final-base
RUN apt-get update && apt-get install -y --no-install-recommends \
    libnss3 \
    libnspr4 \
    libatk1.0-0t64 \
    libatk-bridge2.0-0t64 \
    libcups2t64 \
    libdrm2 \
    libdbus-1-3 \
    libxkbcommon0 \
    libatspi2.0-0t64 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2t64 \
    libpango-1.0-0 \
    libcairo2 \
    libxshmfence1 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/McpServerWebSearch"
RUN dotnet build "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM final-base AS final
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
WORKDIR /app
COPY --from=base /ms-playwright /ms-playwright
COPY --from=publish /app/publish .
RUN chmod -R 755 .playwright

USER $APP_UID

ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
