# Сервер ЕМИР. Сборка: docker compose build api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Emir.Api/Emir.Api.csproj Emir.Api/
RUN dotnet restore Emir.Api/Emir.Api.csproj
COPY Emir.Api/ Emir.Api/
RUN dotnet publish Emir.Api/Emir.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "Emir.Api.dll"]
