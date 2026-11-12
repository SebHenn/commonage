# The SDK always runs on the build machine's own platform and compiles for the target (x86 or ARM), which is much
# faster than emulating ARM. Base images are pinned by digest; Dependabot updates them.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
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
