FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY StressManager.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Read-only root filesystem: no diagnostic IPC socket in /tmp, temp files go to the pod's /tmp emptyDir.
ENV DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "StressManager.dll"]
