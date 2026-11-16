# syntax=docker/dockerfile:1

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore first with only the project/props files so the NuGet layer is cached
# and invalidated only when dependencies actually change.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HowToSoftware.Hosting/HowToSoftware.Hosting.csproj src/HowToSoftware.Hosting/
RUN dotnet restore src/HowToSoftware.Hosting/HowToSoftware.Hosting.csproj

COPY src/ src/
RUN dotnet publish src/HowToSoftware.Hosting/HowToSoftware.Hosting.csproj \
    -c $BUILD_CONFIGURATION \
    --no-restore \
    -o /app/publish

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1

EXPOSE 8080

# APP_UID is the non-root user baked into the Microsoft base images. The data-protection key
# directory is created with that owner so a named volume mounted over it inherits it.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R $APP_UID:$APP_UID /home/app/.aspnet
USER $APP_UID

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "HowToSoftware.Hosting.dll"]
