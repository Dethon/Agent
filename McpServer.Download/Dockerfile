FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServer.Download/McpServer.Download.csproj", "McpServer.Download/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServer.Download/McpServer.Download.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/McpServer.Download"
RUN dotnet build "./McpServer.Download.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./McpServer.Download.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServer.Download.dll"]
