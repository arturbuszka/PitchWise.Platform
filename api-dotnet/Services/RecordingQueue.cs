using System.Text.Json;
using PitchWise.Data.Config;
using StackExchange.Redis;

namespace PitchWise.Api.Services;

// Asks the worker to put a match's recordings together. The job carries only the match id.
public class RecordingQueue(IConnectionMultiplexer redis, AppSettings settings)
{
    public async Task EnqueueAsync(Guid matchId)
    {
        var payload = JsonSerializer.Serialize(new { match_id = matchId });
        await redis.GetDatabase().ListLeftPushAsync(settings.RecordingQueue, payload);
    }
}
