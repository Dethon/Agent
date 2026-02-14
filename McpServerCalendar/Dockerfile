# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerCalendar/McpServerCalendar.csproj", "McpServerCalendar/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "McpServerCalendar/McpServerCalendar.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerCalendar/", "McpServerCalendar/"]
WORKDIR "McpServerCalendar"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./McpServerCalendar.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerCalendar.dll"]
