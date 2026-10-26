FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Commonage.Web/Commonage.Web.csproj src/Commonage.Web/
RUN dotnet restore src/Commonage.Web/Commonage.Web.csproj
COPY src/ src/
RUN dotnet publish src/Commonage.Web/Commonage.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Commonage.Web.dll"]
