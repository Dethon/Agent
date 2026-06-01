# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerPrinter/McpServerPrinter.csproj", "McpServerPrinter/"]
RUN dotnet restore "McpServerPrinter/McpServerPrinter.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerPrinter/", "McpServerPrinter/"]
WORKDIR "/src/McpServerPrinter"
RUN dotnet publish "./McpServerPrinter.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerPrinter.dll"]