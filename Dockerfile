# The SDK always runs on the build machine's own platform and compiles for the target (x86 or ARM), which is much
# faster than emulating ARM. Base images are pinned by digest; Dependabot updates them.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Commonage.Web/Commonage.Web.csproj src/Commonage.Web/packages.lock.json src/Commonage.Web/
RUN dotnet restore src/Commonage.Web/Commonage.Web.csproj -a $TARGETARCH --locked-mode
COPY src/ src/
RUN dotnet publish src/Commonage.Web/Commonage.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
COPY --from=build /app .
# Uploaded files live here; Compose mounts a volume so they survive container updates.
RUN mkdir -p /app/App_Data/media && chown -R $APP_UID /app/App_Data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Commonage.Web.dll"]
