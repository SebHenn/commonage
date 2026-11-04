# The SDK always runs on the build machine's own platform and compiles for the target (x86 or ARM), which is much
# faster than emulating ARM. Base images are pinned by digest; Dependabot updates them.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Commonage.Web/Commonage.Web.csproj src/Commonage.Web/packages.lock.json src/Commonage.Web/
RUN dotnet restore src/Commonage.Web/Commonage.Web.csproj -a $TARGETARCH --locked-mode
COPY src/ src/
RUN dotnet publish src/Commonage.Web/Commonage.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY --from=build /app .
# Uploaded files live here; Compose mounts a volume so they survive container updates.
RUN mkdir -p /app/App_Data/media && chown -R $APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Commonage.Web.dll"]
