using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PitchWise.Api.Tests;

[Collection(nameof(ApiCollection))]
public class MatchesTests(ApiFixture api)
{
    private string AuthUrl => $"/internal/mediamtx/auth/{ApiFixture.MediaMtxSecret}";

    private record CreatedMatch(string Id, string PublicId, string InputId, string Key, JsonElement Body);

    private async Task<CreatedMatch> CreateMatch()
    {
        var response = await api.Client.PostAsJsonAsync("/api/matches", new { name = "Final" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("match");
        var access = body.GetProperty("access");
        return new CreatedMatch(match.GetProperty("id").GetString()!, match.GetProperty("public_id").GetString()!,
            access.GetProperty("input_id").GetString()!, access.GetProperty("stream_key").GetString()!, body);
    }

    private Task<JsonElement> GetMatch(string id) => api.Client.GetFromJsonAsync<JsonElement>($"/api/matches/{id}");

    private async Task<string> WaitForStatus(string id, string expected)
    {
        var status = "";
        for (var i = 0; i < 40 && status != expected; i++)
        {
            if (i > 0) await Task.Delay(250);
            status = (await GetMatch(id)).GetProperty("status").GetString()!;
        }
        return status;
    }

    private Task<HttpResponseMessage> AskPublish(string path, string? query) =>
        api.CreateClient().PostAsJsonAsync(AuthUrl, new { action = "publish", path, query, protocol = "rtmp" });

    private Task<HttpResponseMessage> AskRead(string path, string protocol) =>
        api.CreateClient().PostAsJsonAsync(AuthUrl, new { action = "read", path, protocol });

    [Fact]
    public async Task Create_GivesAnInputOnAir_AddressesForEncodersAndViewers()
    {
        var created = await CreateMatch();
        var match = created.Body.GetProperty("match");
        var access = created.Body.GetProperty("access");

        Assert.Equal("waiting", match.GetProperty("status").GetString());
        Assert.Equal(created.InputId, match.GetProperty("program_input_id").GetString());
        var input = Assert.Single(match.GetProperty("inputs").EnumerateArray());
        Assert.True(input.GetProperty("is_program").GetBoolean());
        Assert.Equal("no_signal", input.GetProperty("signal").GetProperty("state").GetString());

        Assert.EndsWith($"/live/{created.InputId}?key={created.Key}", access.GetProperty("rtmp_url").GetString());
        Assert.Equal(access.GetProperty("rtmp_url").GetString(),
            $"{access.GetProperty("rtmp_server").GetString()}/{access.GetProperty("rtmp_stream_name").GetString()}");
        Assert.Contains($"streamid=publish:live/{created.InputId}:::key={created.Key}", access.GetProperty("srt_url").GetString());

        Assert.Equal($"{ApiFixture.WebOrigin}/m/{created.PublicId}", match.GetProperty("public_url").GetString());
        Assert.Contains($"{ApiFixture.WebOrigin}/embed/{created.PublicId}", match.GetProperty("embed_html").GetString());
    }

    [Fact]
    public async Task Create_RequiresAName()
    {
        var response = await api.Client.PostAsJsonAsync("/api/matches", new { name = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_MakesAnAnalysisForTheMatch()
    {
        var created = await CreateMatch();
        var analysisId = created.Body.GetProperty("match").GetProperty("analysis_id").GetInt32();

        var analysis = await api.Client.GetFromJsonAsync<JsonElement>($"/api/analyses/{analysisId}");

        Assert.Equal("Final", analysis.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Key_IsNeverReturnedAfterCreation()
    {
        var created = await CreateMatch();

        var read = await api.Client.GetStringAsync($"/api/matches/{created.Id}");
        var list = await api.Client.GetStringAsync("/api/matches");
        var pub = await api.CreateClient().GetStringAsync($"/api/public/matches/{created.PublicId}");

        Assert.DoesNotContain(created.Key, read);
        Assert.DoesNotContain(created.Key, list);
        Assert.DoesNotContain(created.Key, pub);
        Assert.DoesNotContain("stream_key", read);
    }

    [Fact]
    public async Task Tenant_CannotReachAnotherTenantsMatch()
    {
        var other = await api.CreateTenantClient();
        var created = await CreateMatch();
        var url = $"/api/matches/{created.Id}";

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{url}/playback")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{url}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{url}/inputs", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{url}/inputs/{created.InputId}/rotate-key", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{url}/program", new { input_id = created.InputId })).StatusCode);
        Assert.DoesNotContain(created.Id, await other.GetStringAsync("/api/matches"));
        Assert.Equal("waiting", await WaitForStatus(created.Id, "waiting"));
    }

    [Fact]
    public async Task Status_FollowsTheProgramInput_AndSurvivesAReconnect()
    {
        var created = await CreateMatch();
        var since = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        api.MediaMtx.SetPublishing(created.InputId, true, since);
        Assert.Equal("live", await WaitForStatus(created.Id, "live"));

        api.MediaMtx.SetPublishing(created.InputId, false);
        Assert.Equal("waiting", await WaitForStatus(created.Id, "waiting"));

        api.MediaMtx.SetPublishing(created.InputId, true, since.AddHours(1));
        Assert.Equal("live", await WaitForStatus(created.Id, "live"));

        // the first signal stays time zero of the broadcast
        Assert.Equal(since, (await GetMatch(created.Id)).GetProperty("started_at").GetDateTime());
        api.MediaMtx.SetPublishing(created.InputId, false);
    }

    [Fact]
    public async Task Signal_IsReportedPerInput()
    {
        var created = await CreateMatch();
        api.MediaMtx.SetPublishing(created.InputId, true, protocol: "srt");
        await WaitForStatus(created.Id, "live");

        var signal = (await GetMatch(created.Id)).GetProperty("inputs")[0].GetProperty("signal");

        Assert.Equal("srt", signal.GetProperty("protocol").GetString());
        Assert.NotEqual("no_signal", signal.GetProperty("state").GetString());
        api.MediaMtx.SetPublishing(created.InputId, false);
    }

    [Fact]
    public async Task SecondInput_DoesNotPutTheMatchOnAir_UntilItIsTheProgram()
    {
        var created = await CreateMatch();
        var added = await (await api.Client.PostAsJsonAsync($"/api/matches/{created.Id}/inputs", new { name = "Wide" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var secondId = added.GetProperty("access").GetProperty("input_id").GetString()!;

        api.MediaMtx.SetPublishing(secondId, true);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal("waiting", await WaitForStatus(created.Id, "waiting"));

        var set = await api.Client.PutAsJsonAsync($"/api/matches/{created.Id}/program", new { input_id = secondId });
        set.EnsureSuccessStatusCode();
        Assert.Equal("live", await WaitForStatus(created.Id, "live"));

        var playback = await api.Client.GetFromJsonAsync<JsonElement>($"/api/matches/{created.Id}/playback");
        Assert.Equal(secondId, playback.GetProperty("input_id").GetString());
        api.MediaMtx.SetPublishing(secondId, false);
    }

    [Fact]
    public async Task Program_MustBeAnInputOfThisMatch()
    {
        var created = await CreateMatch();
        var other = await CreateMatch();

        var response = await api.Client.PutAsJsonAsync($"/api/matches/{created.Id}/program", new { input_id = other.InputId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Playback_ConflictsUntilLive()
    {
        var created = await CreateMatch();

        var response = await api.Client.GetAsync($"/api/matches/{created.Id}/playback");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Playback_SignsThePathTheEdgeChecks_ForLongerThanAMatch()
    {
        var created = await CreateMatch();
        api.MediaMtx.SetPublishing(created.InputId, true);
        await WaitForStatus(created.Id, "live");

        var body = await api.Client.GetFromJsonAsync<JsonElement>($"/api/matches/{created.Id}/playback");

        var parts = new Uri(body.GetProperty("url").GetString()!).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["live", parts[1], parts[2], created.InputId, "index.m3u8"], parts);
        // the same formula as secure_link_md5 in nginx/nginx.conf.template
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{parts[2]} /live/{created.InputId}/ {ApiFixture.HlsSecret}"));
        Assert.Equal(Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('='), parts[1]);
        Assert.True(body.GetProperty("expires_at").GetDateTime() > DateTime.UtcNow.AddHours(4));
        api.MediaMtx.SetPublishing(created.InputId, false);
    }

    [Fact]
    public async Task PublicEndpoint_NeedsNoKey_AndOnlyHasPlaybackWhileLive()
    {
        var created = await CreateMatch();
        var anonymous = api.CreateClient();
        var url = $"/api/public/matches/{created.PublicId}";

        var waiting = await anonymous.GetFromJsonAsync<JsonElement>(url);
        Assert.Equal("waiting", waiting.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("playback").ValueKind);

        api.MediaMtx.SetPublishing(created.InputId, true);
        await WaitForStatus(created.Id, "live");
        var live = await anonymous.GetFromJsonAsync<JsonElement>(url);
        Assert.Contains($"/{created.InputId}/index.m3u8", live.GetProperty("playback").GetProperty("url").GetString());

        // nothing but what a viewer needs
        Assert.False(live.TryGetProperty("tenant_id", out _));
        Assert.False(live.TryGetProperty("inputs", out _));
        Assert.False(live.TryGetProperty("id", out _));
        api.MediaMtx.SetPublishing(created.InputId, false);
    }

    [Fact]
    public async Task PublicEndpoint_DoesNotAcceptTheMatchId()
    {
        var created = await CreateMatch();

        var response = await api.CreateClient().GetAsync($"/api/public/matches/{created.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task End_DisconnectsEveryEncoder_AndStaysEnded()
    {
        var created = await CreateMatch();
        var added = await (await api.Client.PostAsJsonAsync($"/api/matches/{created.Id}/inputs", new { name = "Wide" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var secondId = added.GetProperty("access").GetProperty("input_id").GetString()!;
        api.MediaMtx.SetPublishing(created.InputId, true);
        await WaitForStatus(created.Id, "live");

        (await api.Client.PostAsync($"/api/matches/{created.Id}/end", null)).EnsureSuccessStatusCode();

        lock (api.MediaMtx.Kicked)
        {
            Assert.Contains($"live/{created.InputId}", api.MediaMtx.Kicked);
            Assert.Contains($"live/{secondId}", api.MediaMtx.Kicked);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{created.InputId}", $"key={created.Key}")).StatusCode);
        // the monitor must not revive it while MediaMTX still reports the path
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal("ended", await WaitForStatus(created.Id, "ended"));

        var url = $"/api/matches/{created.Id}";
        Assert.Equal(HttpStatusCode.Conflict, (await api.Client.PostAsJsonAsync($"{url}/inputs", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Client.PostAsync($"{url}/inputs/{created.InputId}/rotate-key", null)).StatusCode);
        api.MediaMtx.SetPublishing(created.InputId, false);
    }

    [Fact]
    public async Task RotateKey_ReplacesTheKey_AndDisconnectsTheEncoder()
    {
        var created = await CreateMatch();

        var response = await api.Client.PostAsync($"/api/matches/{created.Id}/inputs/{created.InputId}/rotate-key", null);
        var newKey = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stream_key").GetString()!;

        Assert.NotEqual(created.Key, newKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{created.InputId}", $"key={created.Key}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AskPublish($"live/{created.InputId}", $"key={newKey}")).StatusCode);
        lock (api.MediaMtx.Kicked) Assert.Contains($"live/{created.InputId}", api.MediaMtx.Kicked);
    }

    [Fact]
    public async Task Publish_NeedsTheRightKeyForThatInput()
    {
        var created = await CreateMatch();
        var other = await CreateMatch();
        var path = $"live/{created.InputId}";

        Assert.Equal(HttpStatusCode.OK, (await AskPublish(path, $"key={created.Key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish($"live/{other.InputId}", $"key={created.Key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish(path, "key=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskPublish("live/not-a-guid", $"key={created.Key}")).StatusCode);
    }

    [Fact]
    public async Task AuthCallback_RejectsAWrongSecretAndUnknownActions()
    {
        var created = await CreateMatch();
        var client = api.CreateClient();

        var wrongSecret = await client.PostAsJsonAsync("/internal/mediamtx/auth/wrong",
            new { action = "publish", path = $"live/{created.InputId}", query = $"key={created.Key}" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        var unknown = await client.PostAsJsonAsync(AuthUrl, new { action = "api" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    // RTMP and SRT are open to the internet, and reading over them would skip the signature check.
    [Theory]
    [InlineData("hls", HttpStatusCode.OK)]
    [InlineData("rtmp", HttpStatusCode.Unauthorized)]
    [InlineData("srt", HttpStatusCode.Unauthorized)]
    public async Task Reading_IsOnlyAllowedThroughTheEdge(string protocol, HttpStatusCode expected)
    {
        var created = await CreateMatch();

        Assert.Equal(expected, (await AskRead($"live/{created.InputId}", protocol)).StatusCode);
    }
}
