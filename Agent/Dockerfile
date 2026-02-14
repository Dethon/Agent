# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["Agent/Agent.csproj", "Agent/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Agent/Agent.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Agent/", "Agent/"]
WORKDIR "Agent"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./Agent.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Agent.dll"]
