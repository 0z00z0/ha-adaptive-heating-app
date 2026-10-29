# The image the add-on runs. This folder is the build context: the Supervisor runs `docker buildx build .` inside it
# and mounts nothing else, so every COPY source has to sit in this folder. Off the box, from the repository root:
#   docker buildx build --platform linux/arm64,linux/amd64 -t <image>:<version> adaptive_heating
#
# Since Supervisor 2026.04.0 no BUILD_FROM argument is supplied and build.yaml is not read, so both stages
# name their base image themselves.

# The publish is architecture-neutral: no runtime identifier, so the compiled output is the same bytes for
# every board and the build stage always runs on the builder's own processor. Cross-compiling never happens,
# which is what keeps a multi-architecture build to one compile.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY addon/Directory.Build.props addon/
COPY addon/src/AdaptiveHeating.Planner/AdaptiveHeating.Planner.csproj addon/src/AdaptiveHeating.Planner/
COPY addon/src/AdaptiveHeating.Core/AdaptiveHeating.Core.csproj addon/src/AdaptiveHeating.Core/
COPY addon/src/AdaptiveHeating.Page/AdaptiveHeating.Page.csproj addon/src/AdaptiveHeating.Page/
COPY addon/src/AdaptiveHeating.AddOn/AdaptiveHeating.AddOn.csproj addon/src/AdaptiveHeating.AddOn/
RUN dotnet restore addon/src/AdaptiveHeating.AddOn/AdaptiveHeating.AddOn.csproj

COPY addon/ addon/
RUN dotnet publish addon/src/AdaptiveHeating.AddOn/AdaptiveHeating.AddOn.csproj \
	-c Release --no-restore -o /app

# The thin layer: a runtime base pulled for the board's own processor, and the neutral publish copied onto
# it. Framework-dependent, because compiling ahead of time is unavailable to a Blazor page.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

LABEL io.hass.type="addon" \
	io.hass.name="Adaptive Heating" \
	org.opencontainers.image.source="https://github.com/0z00z0/ha-adaptive-heating-app"

# Culture data is needed: a temperature is shown in the reader's own culture, and an invariant build would
# render every one of them the same way whatever the browser asked for.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
	ASPNETCORE_ENVIRONMENT=Production

# tzdata is the zone database. The Supervisor sets TZ in every add-on container to the household's IANA zone id,
# and a Microsoft .NET Alpine base carries no database to resolve it against, so without this package every zone
# name fails and the process runs on UTC. Home Assistant's own base images carry it; this one is not one of them.
# 1.5 MiB installed.
RUN apk add --no-cache icu-libs tzdata

WORKDIR /app
COPY --from=build /app .

# /data is the add-on's own persistent directory. The key ring lives there, so an update replaces the image
# without logging every open page out.
VOLUME /data

ENTRYPOINT ["dotnet", "AdaptiveHeating.AddOn.dll"]
