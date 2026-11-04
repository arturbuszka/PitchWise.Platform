using System.Net.WebSockets;
using PitchWise.Live;
using PitchWise.Vision;

var builder = WebApplication.CreateBuilder(args);

LiveSettings settings = LiveSettings.FromEnvironment(builder.Configuration);
Directory.CreateDirectory(settings.HlsBaseDir);
FfmpegTools.FfmpegPath = settings.FfmpegPath;

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<SharedDetector>();
builder.Services.AddHttpClient<TacticalTip>(c => c.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(settings.WebOrigin, settings.WebOriginAlt)
    .AllowAnyMethod()
    .AllowAnyHeader()));

// Bind to port 8001 unless overridden.
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("LIVE_URLS") ?? "http://0.0.0.0:8001");

WebApplication app = builder.Build();

app.UseCors();
app.UseWebSockets();

// Pre-load the YOLO model at startup so the first live session doesn't stall.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try { app.Services.GetRequiredService<SharedDetector>().Preload(); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Detector preload failed"); }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- HLS playlist/segment serving (correct MIME + no-cache). Port of live_hls handler. ---
app.MapGet("/live_hls/{sessionId}/{filename}", (string sessionId, string filename, LiveSettings s) =>
{
    if (sessionId.Contains('/') || filename.Contains('/') ||
        sessionId.Contains("..") || filename.Contains(".."))
        return Results.BadRequest(new { detail = "invalid path" });

    string path = Path.Combine(s.HlsBaseDir, sessionId, filename);
    if (!File.Exists(path)) return Results.NotFound(new { detail = "not found" });

    string mime = filename.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl"
        : filename.EndsWith(".ts") ? "video/mp2t"
        : "application/octet-stream";

    byte[] bytes = File.ReadAllBytes(path);
    return Results.Bytes(bytes, mime).WithNoCache();
});

// --- Live external analysis WebSocket. Port of /ws/live/external/{id}. ---
app.Map("/ws/live/external/{sessionId}", async (
    HttpContext ctx, string sessionId,
    LiveSettings s, SharedDetector detector, TacticalTip tip, ILoggerFactory lf) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var session = new ExternalPreviewSession(sessionId, ws, s, detector, tip);
    ILogger log = lf.CreateLogger("ExternalPreviewSession");
    try
    {
        await session.RunAsync();
    }
    catch (Exception ex)
    {
        log.LogError(ex, "ExternalPreviewSession {id} crashed", sessionId);
    }
    finally
    {
        session.Cleanup();
        if (ws.State == WebSocketState.Open)
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }
});

app.Run();

// Small helper: attach no-cache headers to an IResult.
internal static class ResultExtensions
{
    public static IResult WithNoCache(this IResult inner) => new NoCacheResult(inner);

    private sealed class NoCacheResult(IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            await inner.ExecuteAsync(httpContext);
        }
    }
}
