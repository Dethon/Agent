FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS dependencies
WORKDIR /src
COPY ["McpServer.CommandRunner/McpServer.CommandRunner.csproj", "McpServer.CommandRunner/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServer.CommandRunner/McpServer.CommandRunner.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/McpServer.CommandRunner"
RUN dotnet build "./McpServer.CommandRunner.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./McpServer.CommandRunner.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServer.CommandRunner.dll"]
