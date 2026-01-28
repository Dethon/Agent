# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerLibrary/McpServerLibrary.csproj", "McpServerLibrary/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "McpServerLibrary/McpServerLibrary.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
COPY ["McpServerLibrary/", "McpServerLibrary/"]
WORKDIR "/src/McpServerLibrary"
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "./McpServerLibrary.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerLibrary.dll"]
