# Same base and same distro packages the broadcast service runs on, so that versions measured here
# (ffmpeg 6.1.1, GStreamer 1.24) are the ones production would get, and both candidates use libx264.
FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
      ffmpeg python3 python3-numpy python3-gi gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0 \
      gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good \
      gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-libav \
    && rm -rf /var/lib/apt/lists/*
ENTRYPOINT []
