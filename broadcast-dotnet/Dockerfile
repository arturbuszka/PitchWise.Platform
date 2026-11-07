# Sends matches on to YouTube and Twitch, and cuts between their cameras. Build context must be the repo root:
#   docker build -f broadcast-dotnet/Dockerfile -t pitchwise-broadcast .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY broadcast-dotnet/ ./broadcast-dotnet/
COPY shared/ ./shared/
RUN dotnet publish broadcast-dotnet/PitchWise.Broadcast/PitchWise.Broadcast.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
# ffmpeg sends outputs, and a Python program around GStreamer is the switcher. prototypes/switcher says why.
RUN apt-get update && apt-get install -y --no-install-recommends \
      ffmpeg python3 python3-gi gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0 \
      gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-libav \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
COPY broadcast-dotnet/switcher/ ./switcher/
# Fails the build if a package left out something a match would need.
RUN HOME=/tmp python3 switcher/switcher.py --check
# It only needs the network, so it doesn't need to be root.
USER $APP_UID
ENTRYPOINT ["dotnet", "PitchWise.Broadcast.dll"]
