# Renders highlight reels. Build context must be the repo root:
#   docker build -f worker-dotnet/Dockerfile -t pitchwise-worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY worker-dotnet/ ./worker-dotnet/
COPY shared/ ./shared/
RUN dotnet publish worker-dotnet/PitchWise.Worker/PitchWise.Worker.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENTRYPOINT ["dotnet", "PitchWise.Worker.dll"]
