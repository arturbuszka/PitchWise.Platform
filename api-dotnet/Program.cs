using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Auth;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Data.Outputs;
using PitchWise.Api.Dtos;
using PitchWise.Api.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration: appsettings.json + environment ---
var settings = new AppSettings();
builder.Configuration.GetSection("App").Bind(settings);
// The environment overrides appsettings.json.
settings.StorageDir = Environment.GetEnvironmentVariable("STORAGE_DIR") ?? settings.StorageDir;
settings.DatabaseConnection = Environment.GetEnvironmentVariable("DATABASE_CONNECTION") ?? settings.DatabaseConnection;
settings.RedisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? settings.RedisUrl;
settings.WebOrigin = Environment.GetEnvironmentVariable("WEB_ORIGIN") ?? settings.WebOrigin;
settings.WebOriginAlt = Environment.GetEnvironmentVariable("WEB_ORIGIN_ALT") ?? settings.WebOriginAlt;
settings.HighlightQueue = Environment.GetEnvironmentVariable("HIGHLIGHT_QUEUE") ?? settings.HighlightQueue;
settings.RecordingQueue = Environment.GetEnvironmentVariable("RECORDING_QUEUE") ?? settings.RecordingQueue;
settings.HlsBaseUrl = Environment.GetEnvironmentVariable("HLS_BASE_URL") ?? settings.HlsBaseUrl;
settings.HlsSigningSecret = Environment.GetEnvironmentVariable("HLS_SIGNING_SECRET") ?? settings.HlsSigningSecret;
if (int.TryParse(Environment.GetEnvironmentVariable("HLS_LINK_TTL_SECONDS"), out var hlsTtl))
    settings.HlsLinkTtlSeconds = hlsTtl;
if (int.TryParse(Environment.GetEnvironmentVariable("HLS_LIVE_LINK_TTL_SECONDS"), out var hlsLiveTtl))
    settings.HlsLiveLinkTtlSeconds = hlsLiveTtl;
settings.AdminToken = Environment.GetEnvironmentVariable("ADMIN_TOKEN") ?? settings.AdminToken;
settings.BootstrapApiKey = Environment.GetEnvironmentVariable("BOOTSTRAP_API_KEY") ?? settings.BootstrapApiKey;
settings.RtmpIngestUrl = Environment.GetEnvironmentVariable("RTMP_INGEST_URL") ?? settings.RtmpIngestUrl;
settings.SrtIngestUrl = Environment.GetEnvironmentVariable("SRT_INGEST_URL") ?? settings.SrtIngestUrl;
settings.MediaMtxApiUrl = Environment.GetEnvironmentVariable("MEDIAMTX_API_URL") ?? settings.MediaMtxApiUrl;
settings.MediaMtxAuthSecret = Environment.GetEnvironmentVariable("MEDIAMTX_AUTH_SECRET") ?? settings.MediaMtxAuthSecret;
settings.MediaMtxReadSecret = Environment.GetEnvironmentVariable("MEDIAMTX_READ_SECRET") ?? settings.MediaMtxReadSecret;
settings.OutputEncryptionKey = Environment.GetEnvironmentVariable("OUTPUT_ENCRYPTION_KEY") ?? settings.OutputEncryptionKey;
settings.EnsureDirs();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new OutputKeyCipher(settings.OutputEncryptionKey));

// --- DB ---
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(settings.DatabaseConnection));

// --- Redis ---
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(RedisConfig(settings.RedisUrl)));
builder.Services.AddSingleton<HighlightQueue>();
builder.Services.AddSingleton<RecordingQueue>();
builder.Services.AddSingleton<HlsSigner>();

// --- Auth: every endpoint needs an API key unless it opts out with [AllowAnonymous] ---
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(o => o.FallbackPolicy =
    new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// --- Live ingest ---
builder.Services.AddHttpClient<IMediaMtxClient, MediaMtxClient>(c =>
{
    c.BaseAddress = new Uri(settings.MediaMtxApiUrl);
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<SignalTracker>();
builder.Services.AddScoped<MatchSignalSync>();
builder.Services.AddScoped<MatchMapper>();
builder.Services.AddHostedService<MatchSignalMonitor>();

// --- Full match recordings are large, so uploads have no size limit ---
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
    o.ValueLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);

// --- JSON: snake_case, enums as lower-case strings ---
builder.Services.AddControllers(o => o.Filters.Add<AnalysisAccessFilter>()).AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.Converters.Add(new EventTypeJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new EventSourceJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new SessionStatusJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new MatchStatusJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new RecordingStatusJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new OutputStatusJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new SignalStateJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new HighlightStatusJsonConverter());
});

// --- CORS ---
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(settings.WebOrigin, settings.WebOriginAlt)
    .AllowCredentials()
    .AllowAnyMethod()
    .AllowAnyHeader()));

var app = builder.Build();

// The API owns the schema; the worker only reads and writes it.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await TenantBootstrap.EnsureAsync(db, settings.BootstrapApiKey);
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllers();
app.Run();


static ConfigurationOptions RedisConfig(string url)
{
    // Accept both "host:port" and "redis://host:port".
    var trimmed = url.Replace("redis://", "").Replace("rediss://", "");
    return ConfigurationOptions.Parse(trimmed);
}

// Exposes the entry point to WebApplicationFactory in the integration tests.
public partial class Program;
