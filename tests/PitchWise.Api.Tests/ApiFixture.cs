using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PitchWise.Data;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace PitchWise.Api.Tests;

/// <summary>Runs the real API, migrations included, against real Postgres and Redis containers.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string HlsSecret = "test-secret";
    public const string HlsBaseUrl = "http://edge.test";
    public const string HighlightQueue = "highlight_jobs_test";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("docker.io/library/postgres:16").Build();
    private readonly RedisContainer _redis =
        new RedisBuilder("docker.io/library/redis:7").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"pitchwise-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;
    public IDatabase Redis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Program.cs reads its settings from the environment, as it does in production.
        Environment.SetEnvironmentVariable("DATABASE_CONNECTION", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("REDIS_URL", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("STORAGE_DIR", _storage);
        Environment.SetEnvironmentVariable("HLS_SIGNING_SECRET", HlsSecret);
        Environment.SetEnvironmentVariable("HLS_BASE_URL", HlsBaseUrl);
        Environment.SetEnvironmentVariable("HIGHLIGHT_QUEUE", HighlightQueue);

        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient();
        Redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
    }

    /// <summary>Changes data directly, for states only the worker can produce, such as a finished render.</summary>
    public async Task WithDb(Func<AppDbContext, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public class ApiCollection : ICollectionFixture<ApiFixture>;
