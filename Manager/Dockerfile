FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .

# RUN dotnet publish -c Release -o /app
RUN dotnet publish Manager/Manager.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

COPY --from=build /app .
COPY --from=build /src/Manager/wwwroot ./wwwroot

ENTRYPOINT ["dotnet", "Manager.dll"]