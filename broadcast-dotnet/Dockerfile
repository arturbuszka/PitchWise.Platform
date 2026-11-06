# Sends matches on to YouTube and Twitch. Build context must be the repo root:
#   docker build -f broadcast-dotnet/Dockerfile -t pitchwise-broadcast .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY broadcast-dotnet/ ./broadcast-dotnet/
COPY shared/ ./shared/
RUN dotnet publish broadcast-dotnet/PitchWise.Broadcast/PitchWise.Broadcast.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
# It only needs the network, so it doesn't need to be root.
USER $APP_UID
ENTRYPOINT ["dotnet", "PitchWise.Broadcast.dll"]
