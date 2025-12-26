FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerWebSearch/McpServerWebSearch.csproj", "McpServerWebSearch/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServerWebSearch/McpServerWebSearch.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/McpServerWebSearch"
RUN dotnet build "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./McpServerWebSearch.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerWebSearch.dll"]
