using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Config;
using PitchWise.Data;
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
settings.LlmProvider = Environment.GetEnvironmentVariable("LLM_PROVIDER") ?? settings.LlmProvider;
settings.LlmBaseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? settings.LlmBaseUrl;
settings.LlmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") ?? settings.LlmApiKey;
settings.LlmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? settings.LlmModel;
settings.WebOrigin = Environment.GetEnvironmentVariable("WEB_ORIGIN") ?? settings.WebOrigin;
settings.WebOriginAlt = Environment.GetEnvironmentVariable("WEB_ORIGIN_ALT") ?? settings.WebOriginAlt;
settings.VisionQueue = Environment.GetEnvironmentVariable("VISION_QUEUE") ?? settings.VisionQueue;
settings.HighlightQueue = Environment.GetEnvironmentVariable("HIGHLIGHT_QUEUE") ?? settings.HighlightQueue;
settings.HlsBaseUrl = Environment.GetEnvironmentVariable("HLS_BASE_URL") ?? settings.HlsBaseUrl;
settings.HlsSigningSecret = Environment.GetEnvironmentVariable("HLS_SIGNING_SECRET") ?? settings.HlsSigningSecret;
if (int.TryParse(Environment.GetEnvironmentVariable("HLS_LINK_TTL_SECONDS"), out var hlsTtl))
    settings.HlsLinkTtlSeconds = hlsTtl;
settings.LiveWorkerUrl = Environment.GetEnvironmentVariable("LIVE_WORKER_URL") ?? settings.LiveWorkerUrl;
settings.EnsureDirs();
builder.Services.AddSingleton(settings);

// --- DB ---
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(settings.DatabaseConnection));

// --- Redis ---
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(RedisConfig(settings.RedisUrl)));
builder.Services.AddSingleton<VisionQueue>();
builder.Services.AddSingleton<HighlightQueue>();
builder.Services.AddSingleton<HlsSigner>();

// --- LLM ---
builder.Services.AddHttpClient<LlmClient>(c => c.Timeout = TimeSpan.FromSeconds(120));

// --- Full match recordings are large, so uploads have no size limit ---
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = long.MaxValue;
    o.ValueLengthLimit = int.MaxValue;
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);

// --- JSON: snake_case, enums as lower-case strings ---
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.JsonSerializerOptions.Converters.Add(new EventTypeJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new EventSourceJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new SessionStatusJsonConverter());
    o.JsonSerializerOptions.Converters.Add(new VisionJobStatusJsonConverter());
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
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();

app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
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
