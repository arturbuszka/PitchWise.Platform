using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Models;

namespace PitchWise.Api.Tests;

[Collection(nameof(ApiCollection))]
public class ApiTests(ApiFixture api)
{
    private async Task<int> CreateAnalysis()
    {
        var response = await api.Client.PostAsJsonAsync("/api/analyses", new { name = "Match", sport = "football" });
        response.EnsureSuccessStatusCode();
        return (await Json(response)).GetProperty("id").GetInt32();
    }

    private async Task<int> CreateEvent(int analysisId)
    {
        var response = await api.Client.PostAsJsonAsync($"/api/analyses/{analysisId}/events",
            new { timestamp_seconds = 12.5, type = "goal", label = "Goal" });
        response.EnsureSuccessStatusCode();
        return (await Json(response)).GetProperty("id").GetInt32();
    }

    private async Task<int> CreateHighlight(int analysisId, int eventId)
    {
        var response = await api.Client.PostAsJsonAsync($"/api/analyses/{analysisId}/highlights",
            new { name = "Reel", event_ids = new[] { eventId } });
        response.EnsureSuccessStatusCode();
        return (await Json(response)).GetProperty("id").GetInt32();
    }

    private Task FinishRender(int highlightId, bool hlsReady = true) => api.WithDb(async db =>
    {
        var highlight = await db.Highlights.SingleAsync(h => h.Id == highlightId);
        highlight.Status = HighlightStatus.Done;
        highlight.Filename = "missing.mp4";
        highlight.HlsReady = hlsReady;
    });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Health_ReportsOk()
    {
        var response = await api.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Analysis_RoundTripsThroughTheMigratedSchema()
    {
        var id = await CreateAnalysis();

        var body = await Json(await api.Client.GetAsync($"/api/analyses/{id}"));

        Assert.Equal("Match", body.GetProperty("name").GetString());
        Assert.Equal("draft", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnalysisList_FiltersByNameAndCountsVideos()
    {
        var name = $"Derby {Guid.NewGuid():N}";
        var created = await api.Client.PostAsJsonAsync("/api/analyses", new { name, sport = "football" });
        var id = (await Json(created)).GetProperty("id").GetInt32();
        await api.WithDb(db =>
        {
            db.Videos.Add(new Video { AnalysisId = id, Name = "First half", Filename = "a.mp4" });
            db.Videos.Add(new Video { AnalysisId = id, Name = "Second half", Filename = "b.mp4" });
            return Task.CompletedTask;
        });

        var list = await Json(await api.Client.GetAsync($"/api/analyses?search={name.ToUpperInvariant()}"));

        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetInt32());
        Assert.Equal(2, row.GetProperty("video_count").GetInt32());
    }

    [Fact]
    public async Task Analysis_MissingReturns404()
    {
        var response = await api.Client.GetAsync("/api/analyses/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Event_CannotBeDeletedThroughAnotherAnalysis()
    {
        var owner = await CreateAnalysis();
        var other = await CreateAnalysis();
        var eventId = await CreateEvent(owner);

        var response = await api.Client.DeleteAsync($"/api/analyses/{other}/events/{eventId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var events = await Json(await api.Client.GetAsync($"/api/analyses/{owner}/events"));
        Assert.Equal(1, events.GetArrayLength());
    }

    [Fact]
    public async Task Highlight_EnqueuesARenderJobForTheWorker()
    {
        var analysisId = await CreateAnalysis();
        var eventId = await CreateEvent(analysisId);
        await api.Redis.KeyDeleteAsync(ApiFixture.HighlightQueue);

        var highlightId = await CreateHighlight(analysisId, eventId);

        var job = JsonDocument.Parse((string)(await api.Redis.ListRightPopAsync(ApiFixture.HighlightQueue))!);
        Assert.Equal(highlightId, job.RootElement.EnumerateObject().Single().Value.GetInt32());
    }

    [Fact]
    public async Task Highlight_RejectsEventsFromAnotherAnalysis()
    {
        var analysisId = await CreateAnalysis();
        var foreignEvent = await CreateEvent(await CreateAnalysis());

        var response = await api.Client.PostAsJsonAsync($"/api/analyses/{analysisId}/highlights",
            new { name = "Reel", event_ids = new[] { foreignEvent } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hls_ConflictsUntilTheRenderIsReady()
    {
        var analysisId = await CreateAnalysis();
        var highlightId = await CreateHighlight(analysisId, await CreateEvent(analysisId));

        var response = await api.Client.GetAsync($"/api/analyses/{analysisId}/highlights/{highlightId}/hls");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Hls_SignsTheDirectorySoNginxAcceptsEverySegment()
    {
        var analysisId = await CreateAnalysis();
        var highlightId = await CreateHighlight(analysisId, await CreateEvent(analysisId));
        await FinishRender(highlightId);

        var body = await Json(await api.Client.GetAsync($"/api/analyses/{analysisId}/highlights/{highlightId}/hls"));

        var url = new Uri(body.GetProperty("url").GetString()!);
        Assert.Equal($"{ApiFixture.HlsBaseUrl}/hls/{highlightId}/index.m3u8", url.GetLeftPart(UriPartial.Path));
        var query = url.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        // the same formula as secure_link_md5 in nginx/nginx.conf.template
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{query["expires"]} /hls/{highlightId}/ {ApiFixture.HlsSecret}"));
        var expected = Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(expected, query["md5"]);
    }

    [Fact]
    public async Task Share_IsRefusedUntilTheRenderIsDone()
    {
        var analysisId = await CreateAnalysis();
        var highlightId = await CreateHighlight(analysisId, await CreateEvent(analysisId));

        var response = await api.Client.PostAsync($"/api/analyses/{analysisId}/highlights/{highlightId}/share", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Share_WorksUntilTheLinkExpires()
    {
        var analysisId = await CreateAnalysis();
        var highlightId = await CreateHighlight(analysisId, await CreateEvent(analysisId));
        await FinishRender(highlightId);
        var share = await Json(await api.Client.PostAsync($"/api/analyses/{analysisId}/highlights/{highlightId}/share", null));
        var token = share.GetProperty("token").GetString();

        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync($"/api/share/{token}")).StatusCode);

        await api.WithDb(async db =>
            (await db.Highlights.SingleAsync(h => h.Id == highlightId)).ShareExpiresAt = DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(HttpStatusCode.Gone, (await api.Client.GetAsync($"/api/share/{token}")).StatusCode);
    }

    [Fact]
    public async Task Share_UnknownTokenReturns404()
    {
        var response = await api.Client.GetAsync("/api/share/not-a-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
