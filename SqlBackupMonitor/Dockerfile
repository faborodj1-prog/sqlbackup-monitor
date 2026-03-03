FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY SqlBackupMonitor/SqlBackupMonitor/SqlBackupMonitor.csproj ./
RUN dotnet restore
COPY SqlBackupMonitor/SqlBackupMonitor/ ./
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "SqlBackupMonitor.dll"]
