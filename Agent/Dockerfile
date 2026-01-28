# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["Agent/Agent.csproj", "Agent/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "Agent/Agent.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
COPY ["Agent/", "Agent/"]
WORKDIR "/src/Agent"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./Agent.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Agent.dll"]
