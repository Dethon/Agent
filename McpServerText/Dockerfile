# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerText/McpServerText.csproj", "McpServerText/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerText/McpServerText.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerText/", "McpServerText/"]
WORKDIR "McpServerText"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerText.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerText.dll"]
