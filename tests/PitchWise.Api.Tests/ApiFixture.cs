using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PitchWise.Api.Services;
using PitchWise.Data;
using PitchWise.Data.Outputs;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace PitchWise.Api.Tests;

/// <summary>Runs the real API, migrations included, against real Postgres and Redis containers.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string HlsSecret = "test-secret";
    public const string HlsBaseUrl = "http://edge.test";
    public const string WebOrigin = "http://web.test";
    public const string HighlightQueue = "highlight_jobs_test";
    public const string RecordingQueue = "recording_jobs_test";
    public const string ApiKey = "test-api-key";
    public const string AdminToken = "test-admin-token";
    public const string MediaMtxSecret = "test-mediamtx-secret";
    public const string ReadSecret = "test-read-secret";
    public static readonly string EncryptionKey = Convert.ToBase64String(new byte[32]);

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("docker.io/library/postgres:16").Build();
    private readonly RedisContainer _redis =
        new RedisBuilder("docker.io/library/redis:7").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"pitchwise-tests-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>Authenticated as the bootstrap tenant.</summary>
    public HttpClient Client { get; private set; } = null!;
    public FakeMediaMtx MediaMtx { get; } = new();
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
        Environment.SetEnvironmentVariable("RECORDING_QUEUE", RecordingQueue);
        Environment.SetEnvironmentVariable("BOOTSTRAP_API_KEY", ApiKey);
        Environment.SetEnvironmentVariable("ADMIN_TOKEN", AdminToken);
        Environment.SetEnvironmentVariable("MEDIAMTX_AUTH_SECRET", MediaMtxSecret);
        Environment.SetEnvironmentVariable("WEB_ORIGIN", WebOrigin);
        Environment.SetEnvironmentVariable("MEDIAMTX_READ_SECRET", ReadSecret);
        Environment.SetEnvironmentVariable("OUTPUT_ENCRYPTION_KEY", EncryptionKey);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMediaMtxClient>();
            services.AddSingleton<IMediaMtxClient>(MediaMtx);
        }));
        Client = CreateClient(ApiKey);
        Redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
    }

    public HttpClient CreateClient(string? apiKey = null)
    {
        var client = _factory.CreateClient();
        if (apiKey is not null) client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    /// <summary>
    /// The API as it runs when OUTPUT_ENCRYPTION_KEY isn't set. It is a second host, with its own signal monitor
    /// writing to the same database, so it must not outlive the test or it races the tests after it.
    /// </summary>
    public async Task WithoutEncryptionKey(Func<HttpClient, Task> test)
    {
        await using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<OutputKeyCipher>();
            services.AddSingleton(new OutputKeyCipher(null));
        }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        await test(client);
    }

    /// <summary>Onboards another tenant through the admin API and returns a client for it.</summary>
    public async Task<HttpClient> CreateTenantClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Token", AdminToken);
        var response = await client.PostAsJsonAsync("/api/admin/tenants", new { name = "Other" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return CreateClient(body.GetProperty("api_key").GetString());
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

/// <summary>Stands in for MediaMTX. The real one is exercised end to end with Compose.</summary>
public sealed class FakeMediaMtx : IMediaMtxClient
{
    private readonly Dictionary<string, Publisher> _publishers = [];
    public List<string> Kicked { get; } = [];

    public Task<IReadOnlyDictionary<string, Publisher>> GetPublishersAsync(CancellationToken ct)
    {
        lock (_publishers)
            return Task.FromResult<IReadOnlyDictionary<string, Publisher>>(new Dictionary<string, Publisher>(_publishers));
    }

    public void SetPublishing(string inputId, bool publishing, DateTime? since = null, string protocol = "rtmp")
    {
        lock (_publishers)
        {
            var path = $"live/{inputId}";
            if (publishing)
                _publishers[path] = new Publisher("conn-1", protocol, since ?? DateTime.UtcNow, 1000, ["H264"]);
            else
                _publishers.Remove(path);
        }
    }

    public Task KickPublisherAsync(string path, CancellationToken ct)
    {
        lock (Kicked) Kicked.Add(path);
        return Task.CompletedTask;
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public class ApiCollection : ICollectionFixture<ApiFixture>;
