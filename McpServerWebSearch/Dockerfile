# syntax=docker/dockerfile:1

# Stage 1: Restore dependencies
FROM base-sdk:latest AS dependencies
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

# Stage 2: Publish
FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerWebSearch/", "McpServerWebSearch/"]
WORKDIR "/src/McpServerWebSearch"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# Stage 3: Final runtime image (browser runs in Camoufox sidecar)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN chmod +x .playwright/node/linux-x64/node
USER $APP_UID
ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
