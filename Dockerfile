# Stage 1: Build (.NET 10 SDK)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY app/ .
RUN dotnet publish "KopiaMonitorApp.csproj" -c Release -o /app/publish

# Stage 2: Runtime Non-Root (.NET 10 ASP.NET)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Copia i file compilati ed imposta i permessi per l'utente 'app' preesistente
COPY --from=build /app/publish .
RUN chown -R app:app /app

USER app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "KopiaMonitorApp.dll"]
