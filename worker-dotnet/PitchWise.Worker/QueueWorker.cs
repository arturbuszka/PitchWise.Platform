using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PitchWise.Data.Config;
using StackExchange.Redis;

namespace PitchWise.Worker;

/// <summary>
/// Consumer loop over the highlight Redis list. The API only LPUSHes {highlight_id}; this worker
/// pops and dispatches.
/// </summary>
public sealed class QueueWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly AppSettings _appSettings;
    private readonly WorkerSettings _worker;
    private readonly HighlightRunner _highlight;
    private readonly ILogger<QueueWorker> _log;

    public QueueWorker(
        IConnectionMultiplexer redis,
        AppSettings appSettings,
        WorkerSettings worker,
        HighlightRunner highlight,
        ILogger<QueueWorker> log)
    {
        _redis = redis;
        _appSettings = appSettings;
        _worker = worker;
        _highlight = highlight;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Worker listening on {highlight}", _appSettings.HighlightQueue);

        await Consume(
            _appSettings.HighlightQueue, "highlight_id",
            (id, ct) => _highlight.RunAsync(id, ct), stoppingToken);
    }

    private async Task Consume(
        string queue, string idField, Func<int, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        while (!ct.IsCancellationRequested)
        {
            RedisValue item;
            try
            {
                // StackExchange.Redis has no blocking BRPOP; poll RPOP with a short
                // idle sleep. Same delivery semantics, at the cost of up to timeout
                // latency on an empty queue.
                item = await db.ListRightPopAsync(queue);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Redis error polling {queue}", queue);
                await Task.Delay(TimeSpan.FromSeconds(_worker.BrpopTimeoutSeconds), ct);
                continue;
            }

            if (item.IsNullOrEmpty)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                continue;
            }

            int? id = ParseId(item!, idField);
            if (id is null)
            {
                _log.LogWarning("Skipped invalid queue item on {queue}: {raw}", queue, (string?)item);
                continue;
            }

            _log.LogInformation("Starting {field}={id}", idField, id);
            try
            {
                await handler(id.Value, ct);
                _log.LogInformation("Finished {field}={id}", idField, id);
            }
            catch (Exception ex)
            {
                // A single job must not bring the worker down.
                _log.LogError(ex, "{field}={id} failed", idField, id);
            }
        }
    }

    private static int? ParseId(string raw, string field)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty(field, out JsonElement el))
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)) return n;
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int s)) return s;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
