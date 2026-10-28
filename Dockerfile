# syntax=docker/dockerfile:1.7

# The app is plain IL, so one build serves every target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Downloader/Downloader.csproj src/Downloader/
RUN dotnet restore src/Downloader/Downloader.csproj
COPY src/Downloader/ src/Downloader/
RUN dotnet publish src/Downloader/Downloader.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false \
    && mkdir /downloads

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./

# Docker seeds an empty named volume from the image directory it is mounted
# over, ownership included. 65532 is the user the yt-dlp image runs as; app
# and job containers have to agree on it to read each other's files.
COPY --from=build --chown=65532:65532 /downloads /downloads

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

USER 65532:65532
ENTRYPOINT ["dotnet", "Downloader.dll"]
