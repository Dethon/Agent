# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerLibrary/McpServerLibrary.csproj", "McpServerLibrary/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerLibrary/McpServerLibrary.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerLibrary/", "McpServerLibrary/"]
WORKDIR "McpServerLibrary"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerLibrary.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerLibrary.dll"]
