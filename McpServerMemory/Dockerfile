FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["McpServerMemory/McpServerMemory.csproj", "McpServerMemory/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpServerMemory/McpServerMemory.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/McpServerMemory"
RUN dotnet build "./McpServerMemory.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./McpServerMemory.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerMemory.dll"]
