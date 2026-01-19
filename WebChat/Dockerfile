FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dependencies
WORKDIR /src
COPY ["WebChat/WebChat.csproj", "WebChat/"]
COPY ["WebChat.Client/WebChat.Client.csproj", "WebChat.Client/"]
COPY ["Domain/Domain.csproj", "Domain/"]
RUN dotnet restore "WebChat/WebChat.csproj"

FROM dependencies AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/WebChat"
RUN dotnet publish "./WebChat.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WebChat.dll"]
