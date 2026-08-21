# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish VysorServer.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render encaminha o tráfego HTTP/WebSocket para a porta definida em PORT.
EXPOSE 10000
ENV ASPNETCORE_ENVIRONMENT=Production
# Render limits inotify instances; use polling for .NET file change detection.
ENV DOTNET_USE_POLLING_FILE_WATCHER=1

ENTRYPOINT ["dotnet", "VysorServer.dll"]
