$root = $PSScriptRoot

# Shared storage directory - the .NET API writes uploads, the worker reads them. MUST be the same.
$storage = "$root\storage"

# Infrastructure: Postgres (shared database) + Redis (job queue).
Write-Host "Starting infrastructure (Postgres + Redis)..."
docker compose -f "$root\docker-compose.infra.yml" up -d | Out-Null

# .NET API (:8000) - creates the DB schema, listens on port 8000.
Start-Process powershell -ArgumentList "-NoExit", "-Command", "
    Set-Location '$root\api-dotnet';
    `$env:STORAGE_DIR='$storage';
    dotnet run
" -WindowStyle Normal

# .NET worker (vision) - pops jobs from Redis (BRPOP), writes to the shared Postgres,
# and serves the live WS/HLS server on :8001. Two processes (PitchWise.Worker batch +
# PitchWise.Live). YOLO11 runs via ONNX Runtime; ffmpeg does HLS.
Start-Process powershell -ArgumentList "-NoExit", "-Command", "
    Set-Location '$root\worker-dotnet';
    # Shared with the .NET API, which owns the schema.
    `$env:DATABASE_CONNECTION='Host=localhost;Port=5432;Database=pitchwise;Username=pitchwise;Password=pitchwise';
    `$env:REDIS_URL='redis://localhost:6379';
    `$env:STORAGE_DIR='$storage';
    `$env:WEB_ORIGIN='http://localhost:3000';
    # Football-specific model (player/ball/referee/goalkeeper) exported from
    # https://github.com/Darkmyter/Football-Players-Tracking weights. Falls back to
    # yolo11n.onnx (COCO, person-only) if you haven't run the export yet.
    `$env:YOLO_MODEL_PATH='$root\worker-dotnet\vision-onnx\football.onnx';
    # OSNet person-Re-ID model (export via vision-onnx/export_reid_onnx.py). Enables stable
    # per-player identity across track-id switches + a time-on-pitch report. If the file
    # doesn't exist yet, leave it — the worker just skips Re-ID and runs as before.
    `$env:REID_MODEL_PATH='$root\worker-dotnet\vision-onnx\osnet_x0_25.onnx';
    # Live pipeline: 'passthrough' (raw frames) or 'detect' (YOLO overlay). Default safe.
    `$env:LIVE_PIPELINE_MODE='detect';
    # ONNX execution provider: 'dml' (DirectML GPU, any DX12 card, no CUDA needed) or 'cpu'.
    # Auto-falls back to CPU if DirectML can't initialise. Applies to live (this scope).
    `$env:ONNX_EP='dml';
    # DirectML adapter index. On this dual-GPU laptop: 0 = AMD Vega iGPU, 1 = NVIDIA GTX 1660 Ti.
    # Verified via nvidia-smi (device 1 loads the NVIDIA card; device 0 leaves it idle).
    `$env:ONNX_DEVICE_ID='1';
    # Use the winget-installed ffmpeg 8.x (has -hls_flags). Without this, an old
    # ffmpeg earlier on PATH (e.g. Panda3D's 2013 build) breaks live HLS encoding.
    `$ff = Get-ChildItem `"`$env:LOCALAPPDATA\Microsoft\WinGet\Packages`" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1;
    if (`$ff) { `$env:FFMPEG_PATH = `$ff.FullName };
    # Batch worker in the background; live server in the foreground (keeps window alive).
    Start-Job -ScriptBlock {
        param(`$dir, `$db, `$redis, `$storage, `$model, `$ffmpeg, `$reid)
        Set-Location `$dir;
        `$env:DATABASE_CONNECTION=`$db; `$env:REDIS_URL=`$redis; `$env:STORAGE_DIR=`$storage;
        `$env:YOLO_MODEL_PATH=`$model; if (`$ffmpeg) { `$env:FFMPEG_PATH=`$ffmpeg };
        if (`$reid) { `$env:REID_MODEL_PATH=`$reid };
        # DirectML GPU for the batch worker (falls back to CPU automatically).
        `$env:ONNX_EP='dml';
        # DirectML adapter: 1 = NVIDIA GTX 1660 Ti (0 = AMD Vega iGPU). Same choice as live scope.
        `$env:ONNX_DEVICE_ID='1';
        # Frames per GPU inference (analysis path). 1 = old one-at-a-time behaviour.
        `$env:ONNX_BATCH='8';
        dotnet run --project PitchWise.Worker -c Release
    } -ArgumentList (Get-Location).Path, `$env:DATABASE_CONNECTION, `$env:REDIS_URL, `$env:STORAGE_DIR, `$env:YOLO_MODEL_PATH, `$env:FFMPEG_PATH, `$env:REID_MODEL_PATH | Out-Null;
    dotnet run --project PitchWise.Live -c Release
" -WindowStyle Normal

# Frontend (:3000) - NEXT_PUBLIC_API_URL points to http://localhost:8000.
Start-Process powershell -ArgumentList "-NoExit", "-Command", "
    Set-Location '$root\web';
    npm run dev
" -WindowStyle Normal

Write-Host "Started:"
Write-Host "  API (.NET)      -> http://localhost:8000"
Write-Host "  Worker (.NET)   -> Redis 'vision_jobs' queue + live WebSocket :8001"
Write-Host "  Web             -> http://localhost:3000"
Write-Host "  Postgres        -> localhost:5432   Redis -> localhost:6379"
