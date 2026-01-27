# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerText/McpServerText.csproj", "McpServerText/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "McpServerText/McpServerText.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
COPY ["McpServerText/", "McpServerText/"]
WORKDIR "/src/McpServerText"
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "./McpServerText.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "./McpServerText.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerText.dll"]
