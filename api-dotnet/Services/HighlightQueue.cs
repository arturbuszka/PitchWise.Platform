using System.Text.Json;
using PitchWise.Data.Config;
using StackExchange.Redis;

namespace PitchWise.Api.Services;

// Same contract as VisionQueue, but for highlight rendering jobs. The API only
// pushes highlight_id as JSON onto the Redis list; the worker's highlight
// loop pops it via BRPOP and runs ffmpeg concat.
public class HighlightQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly AppSettings _settings;

    public HighlightQueue(IConnectionMultiplexer redis, AppSettings settings)
    {
        _redis = redis;
        _settings = settings;
    }

    public async Task EnqueueAsync(int highlightId)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(new { highlight_id = highlightId });
        // LPUSH + (worker) BRPOP => FIFO queue.
        await db.ListLeftPushAsync(_settings.HighlightQueue, payload);
    }
}
