using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PitchWise.Api.Tests;

[Collection(nameof(ApiCollection))]
public class StreamsTests(ApiFixture api)
{
    private string AuthUrl => $"/internal/mediamtx/auth/{ApiFixture.MediaMtxSecret}";

    private async Task<(string Id, string Key, string IngestUrl)> CreateStream()
    {
        var response = await api.Client.PostAsJsonAsync("/api/streams", new { name = "Final" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("stream").GetProperty("id").GetString()!,
            body.GetProperty("stream_key").GetString()!,
            body.GetProperty("ingest_url").GetString()!);
    }

    private async Task<string> WaitForStatus(string id, string expected)
    {
        var status = "";
        for (var i = 0; i < 40 && status != expected; i++)
        {
            if (i > 0) await Task.Delay(250);
            var body = await api.Client.GetFromJsonAsync<JsonElement>($"/api/streams/{id}");
            status = body.GetProperty("status").GetString()!;
        }
        return status;
    }

    private Task<HttpResponseMessage> AskPublish(string path, string? query) =>
        api.CreateClient().PostAsJsonAsync(AuthUrl, new { action = "publish", path, query, protocol = "rtmp" });

    [Fact]
    public async Task Create_ReturnsWhereToPublish_AndStartsIdle()
    {
        var (id, key, ingestUrl) = await CreateStream();

        Assert.EndsWith($"/live/{id}?key={key}", ingestUrl);
        Assert.Equal("idle", await WaitForStatus(id, "idle"));
    }

    [Fact]
    public async Task Create_RequiresAName()
    {
        var response = await api.Client.PostAsJsonAsync("/api/streams", new { name = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_CannotSeeOrEndAnotherTenantsStream()
    {
        var other = await api.CreateTenantClient();
        var (id, _, _) = await CreateStream();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/streams/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/streams/{id}/playback")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/streams/{id}/end", null)).StatusCode);
        Assert.Equal("idle", await WaitForStatus(id, "idle"));
    }

    [Fact]
    public async Task Status_FollowsThePublisher_AndSurvivesAReconnect()
    {
        var (id, _, _) = await CreateStream();

        api.MediaMtx.SetReady($"live/{id}", true);
        Assert.Equal("live", await WaitForStatus(id, "live"));

        api.MediaMtx.SetReady($"live/{id}", false);
        Assert.Equal("idle", await WaitForStatus(id, "idle"));

        api.MediaMtx.SetReady($"live/{id}", true);
        Assert.Equal("live", await WaitForStatus(id, "live"));
        api.MediaMtx.SetReady($"live/{id}", false);
    }

    [Fact]
    public async Task Playback_ConflictsUntilLive()
    {
        var (id, _, _) = await CreateStream();

        var response = await api.Client.GetAsync($"/api/streams/{id}/playback");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Playback_SignsThePathTheEdgeChecks()
    {
        var (id, _, _) = await CreateStream();
        api.MediaMtx.SetReady($"live/{id}", true);
        await WaitForStatus(id, "live");

        var body = await api.Client.GetFromJsonAsync<JsonElement>($"/api/streams/{id}/playback");

        var parts = new Uri(body.GetProperty("url").GetString()!).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["live", parts[1], parts[2], id, "index.m3u8"], parts);
        // the same formula as secure_link_md5 in nginx/nginx.conf.template
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{parts[2]} /live/{id}/ {ApiFixture.HlsSecret}"));
        Assert.Equal(Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('='), parts[1]);
        api.MediaMtx.SetReady($"live/{id}", false);
    }

    [Fact]
    public async Task End_DisconnectsTheEncoder_AndStaysEnded()
    {
        var (id, key, _) = await CreateStream();
        api.MediaMtx.SetReady($"live/{id}", true);
        await WaitForStatus(id, "live");

        (await api.Client.PostAsync($"/api/streams/{id}/end", null)).EnsureSuccessStatusCode();

        lock (api.MediaMtx.Kicked) Assert.Contains($"live/{id}", api.MediaMtx.Kicked);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{id}", $"key={key}")).StatusCode);
        // the monitor must not revive it while MediaMTX still reports the path
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal("ended", await WaitForStatus(id, "ended"));
        api.MediaMtx.SetReady($"live/{id}", false);
    }

    [Fact]
    public async Task Publish_NeedsTheRightKeyForThatStream()
    {
        var (id, key, _) = await CreateStream();
        var (otherId, _, _) = await CreateStream();

        Assert.Equal(HttpStatusCode.OK, (await AskPublish($"live/{id}", $"key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{otherId}", $"key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{id}", "key=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish("live/not-a-guid", $"key={key}")).StatusCode);
    }

    [Fact]
    public async Task AuthCallback_RejectsAWrongSecretAndUnknownActions()
    {
        var (id, key, _) = await CreateStream();
        var client = api.CreateClient();

        var wrongSecret = await client.PostAsJsonAsync("/internal/mediamtx/auth/wrong",
            new { action = "publish", path = $"live/{id}", query = $"key={key}" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        var unknown = await client.PostAsJsonAsync(AuthUrl, new { action = "api" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var read = await client.PostAsJsonAsync(AuthUrl, new { action = "read", path = $"live/{id}" });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
