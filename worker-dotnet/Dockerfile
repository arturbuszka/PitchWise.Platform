# Runs the batch queue worker (PitchWise.Worker) and the live WS/HLS server (PitchWise.Live)
# in one container. Models are mounted at /app/models by docker-compose.yml.
#
# Build context must be the repo root:
#   docker build -f worker-dotnet/Dockerfile -t pitchwise-worker .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the projects the worker/live builds depend on.
COPY worker-dotnet/ ./worker-dotnet/
COPY shared/ ./shared/

# Publish both entrypoints. -r linux-x64 makes the csproj pull the LINUX OpenCvSharp
# native lib (libOpenCvSharpExtern.so); without an explicit RID it defaults to Windows.
RUN dotnet publish worker-dotnet/PitchWise.Worker/PitchWise.Worker.csproj -c Release -r linux-x64 --no-self-contained -o /out/worker \
 && dotnet publish worker-dotnet/PitchWise.Live/PitchWise.Live.csproj    -c Release -r linux-x64 --no-self-contained -o /out/live

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# System deps: ffmpeg (HLS encode/probe), yt-dlp (stream resolver, single binary),
# and the native libs OpenCvSharp needs at runtime (OpenGL/threading/etc.).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg yt-dlp \
        libgl1 libglib2.0-0 libsm6 libxext6 libxrender1 libgtk-3-0t64 libatomic1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out/worker ./worker
COPY --from=build /out/live   ./live
ENV ONNX_EP=cpu \
    FFMPEG_PATH=ffmpeg \
    YT_DLP_PATH=yt-dlp \
    LIVE_HLS_DIR=/tmp/live_hls \
    LIVE_PIPELINE_MODE=detect

EXPOSE 8001

# Run the native apphost (not `dotnet foo.dll`) — on Windows dev boxes, invoking the
# bare .dll via the dotnet.exe muxer can resolve the OS-bundled onnxruntime.dll in
# System32 ahead of ours (crashes with "version [14] not supported"); the apphost
# doesn't go through that resolution path. Harmless on Linux either way.
RUN chmod +x /app/worker/PitchWise.Worker /app/live/PitchWise.Live
# bash, because dash has no `wait -n`: if either process dies, the container exits and Compose reports it.
CMD ["bash", "-c", "/app/worker/PitchWise.Worker & /app/live/PitchWise.Live & wait -n"]
