# The SDK always runs on the build machine's own platform and compiles for the target (x86 or ARM), which is much
# faster than emulating ARM. Base images are pinned by digest; Dependabot updates them.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Commonage.Web/Commonage.Web.csproj src/Commonage.Web/packages.lock.json src/Commonage.Web/
RUN dotnet restore src/Commonage.Web/Commonage.Web.csproj -a $TARGETARCH --locked-mode
COPY src/ src/
RUN dotnet publish src/Commonage.Web/Commonage.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
WORKDIR /app
COPY --from=build /app .
# Uploaded files live here; Compose mounts a volume so they survive container updates.
RUN mkdir -p /app/App_Data/media && chown -R $APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Commonage.Web.dll"]
