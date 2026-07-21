# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpChannelVoice/McpChannelVoice.csproj", "McpChannelVoice/"]
RUN dotnet restore "McpChannelVoice/McpChannelVoice.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpChannelVoice/", "McpChannelVoice/"]
WORKDIR "/src/McpChannelVoice"
RUN dotnet publish "./McpChannelVoice.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
ADD --checksum=sha256:c46fad10b5f81e1aa4a60c162714208577093655076c5450f8c469e522ec54ef --chown=$APP_UID:$APP_UID \
    https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_CAM++.onnx \
    /app/models/speaker-embedding.onnx
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelVoice.dll"]
