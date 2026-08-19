# Multi-stage build for FcTelecom.Web.
#
# The image is built but not required for local development (docker-compose runs only
# the dependencies). It exists so that App Service can be swapped for Container Apps
# without a code change, per the hosting decision in docs/01-architecture.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the manifests first so a dependency-only change does not invalidate the
# restore layer on every source edit.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/FcTelecom.Domain/*.csproj             src/FcTelecom.Domain/
COPY src/FcTelecom.Application/*.csproj        src/FcTelecom.Application/
COPY src/FcTelecom.Infrastructure/*.csproj     src/FcTelecom.Infrastructure/
COPY src/FcTelecom.Contracts/*.csproj          src/FcTelecom.Contracts/
COPY src/FcTelecom.Web/*.csproj                src/FcTelecom.Web/
RUN dotnet restore src/FcTelecom.Web/FcTelecom.Web.csproj

COPY src/ src/
RUN dotnet publish src/FcTelecom.Web/FcTelecom.Web.csproj \
        -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Run as a non-root user. The base image provides 'app' (UID 1654).
USER app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "FcTelecom.Web.dll"]
