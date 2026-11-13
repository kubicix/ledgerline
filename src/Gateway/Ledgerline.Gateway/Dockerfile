FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/Gateway/Ledgerline.Gateway/Ledgerline.Gateway.csproj -c Release -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ledgerline.Gateway.dll"]
