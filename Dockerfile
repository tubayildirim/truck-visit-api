# syntax=docker/dockerfile:1

# =============================================================================================
# Build stage
#
# Project files are copied and restored before the source. Docker caches that layer, so editing
# a .cs file does not re-download the package graph on every build — which is most of the time a
# CI build spends.
# =============================================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props TruckVisit.sln ./
COPY tests/Directory.Build.props tests/
COPY src/TruckVisit.Domain/TruckVisit.Domain.csproj src/TruckVisit.Domain/
COPY src/TruckVisit.Application/TruckVisit.Application.csproj src/TruckVisit.Application/
COPY src/TruckVisit.Infrastructure/TruckVisit.Infrastructure.csproj src/TruckVisit.Infrastructure/
COPY src/TruckVisit.Api/TruckVisit.Api.csproj src/TruckVisit.Api/
COPY tests/TruckVisit.Domain.Tests/TruckVisit.Domain.Tests.csproj tests/TruckVisit.Domain.Tests/
COPY tests/TruckVisit.Application.Tests/TruckVisit.Application.Tests.csproj tests/TruckVisit.Application.Tests/
COPY tests/TruckVisit.Api.IntegrationTests/TruckVisit.Api.IntegrationTests.csproj tests/TruckVisit.Api.IntegrationTests/

RUN dotnet restore TruckVisit.sln

COPY . .

# Unit tests run inside the image build. A container that cannot pass its own tests never gets
# built, so a broken image cannot be pushed by a pipeline step that forgot to run them.
RUN dotnet test tests/TruckVisit.Domain.Tests --no-restore --verbosity quiet

RUN dotnet publish src/TruckVisit.Api/TruckVisit.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app/publish

# =============================================================================================
# Runtime stage
#
# "chiseled" is a distroless image: no shell, no package manager, no utilities. There is nothing
# for an attacker who achieves code execution to pivot with, and the CVE surface is a fraction of
# a full distribution. It also runs as a non-root user (UID 64198) without being told to.
#
# The "extra" variant is required, not optional: it carries ICU. This application deliberately
# does not set InvariantGlobalization, so the runtime needs real culture data — see
# Directory.Build.props for why that choice was made.
# =============================================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Kestrel listens on a high port so the container never needs NET_BIND_SERVICE.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1

EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "TruckVisit.Api.dll"]
