using Microsoft.EntityFrameworkCore;
using PitchWise.Broadcast;
using PitchWise.Data;
using PitchWise.Data.Outputs;

var builder = Host.CreateApplicationBuilder(args);

// It polls every second, and those would bury anything that matters.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

var settings = BroadcastSettings.FromEnvironment();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new OutputKeyCipher(settings.OutputEncryptionKey));
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(settings.DatabaseConnection));
builder.Services.AddHttpClient<IOutputSources, MediaMtxSources>(c =>
{
    c.BaseAddress = new Uri(settings.MediaMtxApiUrl);
    c.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton<IOutputProcessFactory, FfmpegOutputFactory>();
builder.Services.AddSingleton<ITargetGuard, TargetGuard>();
builder.Services.AddSingleton<SingleInstanceLock>();
builder.Services.AddSingleton<OutputSupervisor>();
builder.Services.AddHostedService<BroadcastService>();
// Time for ffmpeg to close its connections, which a stop request allows two seconds each.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(8));

builder.Build().Run();
