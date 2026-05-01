# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpChannelServiceBus/McpChannelServiceBus.csproj", "McpChannelServiceBus/"]
RUN dotnet restore "McpChannelServiceBus/McpChannelServiceBus.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpChannelServiceBus/", "McpChannelServiceBus/"]
WORKDIR "/src/McpChannelServiceBus"
RUN dotnet publish "./McpChannelServiceBus.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelServiceBus.dll"]
