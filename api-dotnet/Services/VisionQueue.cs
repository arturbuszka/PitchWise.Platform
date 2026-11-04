using System.Text.Json;
using PitchWise.Data.Config;
using StackExchange.Redis;

namespace PitchWise.Api.Services;

// Pushes job_id as JSON onto the Redis list; the worker pops it with BRPOP.
// That is the whole contract between the two.
public class VisionQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly AppSettings _settings;

    public VisionQueue(IConnectionMultiplexer redis, AppSettings settings)
    {
        _redis = redis;
        _settings = settings;
    }

    public async Task EnqueueAsync(int jobId)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(new { job_id = jobId });
        // LPUSH + (worker) BRPOP => FIFO queue.
        await db.ListLeftPushAsync(_settings.VisionQueue, payload);
    }
}
