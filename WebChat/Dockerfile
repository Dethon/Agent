# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["WebChat/WebChat.csproj", "WebChat/"]
COPY ["WebChat.Client/WebChat.Client.csproj", "WebChat.Client/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "WebChat/WebChat.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["Domain/", "Domain/"]
COPY ["Infrastructure/", "Infrastructure/"]
COPY ["WebChat/", "WebChat/"]
COPY ["WebChat.Client/", "WebChat.Client/"]
WORKDIR "/src/WebChat"
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "./WebChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebChat.dll"]
