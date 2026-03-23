FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Domain/Domain.csproj Domain/
COPY Dashboard.Client/Dashboard.Client.csproj Dashboard.Client/
COPY Observability/Observability.csproj Observability/
RUN dotnet restore Observability/Observability.csproj
COPY Domain/ Domain/
COPY Dashboard.Client/ Dashboard.Client/
COPY Observability/ Observability/
RUN dotnet publish Observability/Observability.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Observability.dll"]
