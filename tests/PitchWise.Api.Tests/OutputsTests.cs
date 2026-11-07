using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Models;
using PitchWise.Data.Outputs;

namespace PitchWise.Api.Tests;

[Collection(nameof(ApiCollection))]
public class OutputsTests(ApiFixture api)
{
    private const string Key = "abcd-efgh-ijkl-mnop";
    private string AuthUrl => $"/internal/mediamtx/auth/{ApiFixture.MediaMtxSecret}";

    private async Task<(string MatchId, string InputId, string StreamKey)> CreateMatch()
    {
        var response = await api.Client.PostAsJsonAsync("/api/matches", new { name = "Final" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("match").GetProperty("id").GetString()!,
            body.GetProperty("access").GetProperty("input_id").GetString()!,
            body.GetProperty("access").GetProperty("stream_key").GetString()!);
    }

    private Task<HttpResponseMessage> Create(string matchId, string url = "rtmps://a.rtmps.youtube.com/live2", string key = Key, HttpClient? client = null) =>
        (client ?? api.Client).PostAsJsonAsync($"/api/matches/{matchId}/outputs", new { name = "YouTube", url, stream_key = key });

    private async Task<string> CreateOutput(string matchId)
    {
        var response = await Create(matchId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> Match(string matchId) =>
        await (await api.Client.GetAsync($"/api/matches/{matchId}")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Output_IsStoppedUntilAskedAndNeverReturnsItsKey()
    {
        var (matchId, _, _) = await CreateMatch();

        var created = await Create(matchId);
        var text = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("stream_key", text);

        var output = (await Match(matchId)).GetProperty("outputs")[0];
        Assert.Equal("stopped", output.GetProperty("status").GetString());
        Assert.False(output.GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(Key, output.GetRawText());

        var listed = await (await api.Client.GetAsync($"/api/matches/{matchId}/outputs")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, listed);
    }

    [Fact]
    public async Task Key_IsEncryptedInTheDatabaseAndTiedToItsOutput()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = Guid.Parse(await CreateOutput(matchId));

        await api.WithDb(async db =>
        {
            var row = await db.MatchOutputs.SingleAsync(o => o.Id == id);
            Assert.DoesNotContain(Key, row.StreamKeyEncrypted);
            var cipher = new OutputKeyCipher(ApiFixture.EncryptionKey);
            Assert.Equal(Key, cipher.Decrypt(id, row.StreamKeyEncrypted));
            Assert.Throws<OutputKeyException>(() => cipher.Decrypt(Guid.NewGuid(), row.StreamKeyEncrypted));
        });
    }

    [Theory]
    [InlineData("http://example.com/live")]
    [InlineData("file:///etc/passwd")]
    [InlineData("-y")]
    [InlineData("rtmp://user:pass@example.com/live")]
    [InlineData("rtmp://example.com/live?key=secret")]
    public async Task Create_RefusesAnAddressThatIsNotAPlainRtmpOne(string url)
    {
        var (matchId, _, _) = await CreateMatch();
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(matchId, url)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../other")]
    public async Task Create_RefusesAKeyThatWouldChangeTheAddress(string key)
    {
        var (matchId, _, _) = await CreateMatch();
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(matchId, key: key)).StatusCode);
    }

    [Fact]
    public async Task Create_WithoutAnEncryptionKey_SaysSoAndLeavesTheRestWorking()
    {
        var (matchId, _, _) = await CreateMatch();

        await api.WithoutEncryptionKey(async client =>
        {
            var response = await Create(matchId, client: client);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("OUTPUT_ENCRYPTION_KEY", await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/matches/{matchId}")).StatusCode);
        });
    }

    [Fact]
    public async Task Start_ClearsFailuresAndAsksTheServiceToBeginAgain()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = await CreateOutput(matchId);
        var guid = Guid.Parse(id);
        await api.WithDb(async db =>
        {
            var row = await db.MatchOutputs.SingleAsync(o => o.Id == guid);
            row.Enabled = true;
            row.Status = OutputStatus.Failed;
            row.Error = "gave up";
            row.Attempts = 11;
            row.LastSeenAt = DateTime.UtcNow;
        });

        var started = await (await api.Client.PostAsync($"/api/matches/{matchId}/outputs/{id}/start", null))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("connecting", started.GetProperty("status").GetString());
        Assert.Equal(0, started.GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, started.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task EveryChangeRaisesTheGeneration_SoAReportAboutOldSettingsIsIgnored()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = await CreateOutput(matchId);
        var guid = Guid.Parse(id);
        async Task<int> Generation()
        {
            var generation = 0;
            await api.WithDb(async db => generation = (await db.MatchOutputs.AsNoTracking().SingleAsync(o => o.Id == guid)).Generation);
            return generation;
        }

        var before = await Generation();
        await api.Client.PostAsync($"/api/matches/{matchId}/outputs/{id}/start", null);
        var afterStart = await Generation();
        await api.Client.PutAsJsonAsync($"/api/matches/{matchId}/outputs/{id}", new { stream_key = "new-key" });
        var afterKey = await Generation();
        await api.Client.PutAsJsonAsync($"/api/matches/{matchId}/outputs/{id}", new { name = "Renamed" });
        var afterRename = await Generation();
        await api.Client.PostAsync($"/api/matches/{matchId}/outputs/{id}/stop", null);
        var afterStop = await Generation();

        Assert.True(afterStart > before);
        Assert.True(afterKey > afterStart);
        Assert.Equal(afterKey, afterRename); // a name doesn't affect what is being sent
        Assert.True(afterStop > afterRename);
    }

    [Fact]
    public async Task AQuietService_IsNotReportedAsAWorkingOutput()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = await CreateOutput(matchId);
        var guid = Guid.Parse(id);
        await api.WithDb(async db =>
        {
            var row = await db.MatchOutputs.SingleAsync(o => o.Id == guid);
            row.Enabled = true;
            row.Status = OutputStatus.Live;
            row.LastSeenAt = DateTime.UtcNow.AddMinutes(-1);
        });

        var output = (await Match(matchId)).GetProperty("outputs")[0];

        Assert.Equal("offline", output.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EndingAMatch_StopsItsOutputsAndRefusesNewOnes()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = await CreateOutput(matchId);
        await api.Client.PostAsync($"/api/matches/{matchId}/outputs/{id}/start", null);

        await api.Client.PostAsync($"/api/matches/{matchId}/end", null);

        var output = (await Match(matchId)).GetProperty("outputs")[0];
        Assert.False(output.GetProperty("enabled").GetBoolean());
        Assert.Equal("stopped", output.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Create(matchId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Client.PostAsync($"/api/matches/{matchId}/outputs/{id}/start", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesIt()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = await CreateOutput(matchId);

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client.DeleteAsync($"/api/matches/{matchId}/outputs/{id}")).StatusCode);

        Assert.Equal(0, (await Match(matchId)).GetProperty("outputs").GetArrayLength());
    }

    [Fact]
    public async Task AnotherTenant_CantSeeOrTouchIt()
    {
        var (matchId, _, _) = await CreateMatch();
        var id = await CreateOutput(matchId);
        var other = await api.CreateTenantClient();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/matches/{matchId}/outputs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/matches/{matchId}/outputs/{id}/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/matches/{matchId}/outputs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Create(matchId, client: other)).StatusCode);
    }

    [Theory]
    [InlineData("rtsp", OutputReader.User, ApiFixture.ReadSecret, HttpStatusCode.OK)]
    [InlineData("rtsp", OutputReader.User, "wrong", HttpStatusCode.Unauthorized)]
    [InlineData("rtsp", "someone", ApiFixture.ReadSecret, HttpStatusCode.Unauthorized)]
    [InlineData("rtsp", "", "", HttpStatusCode.Unauthorized)]
    // The internet can reach these, so not even the right secret opens them for reading.
    [InlineData("rtmp", OutputReader.User, ApiFixture.ReadSecret, HttpStatusCode.Unauthorized)]
    [InlineData("srt", OutputReader.User, ApiFixture.ReadSecret, HttpStatusCode.Unauthorized)]
    [InlineData("webrtc", OutputReader.User, ApiFixture.ReadSecret, HttpStatusCode.Unauthorized)]
    [InlineData("moq", OutputReader.User, ApiFixture.ReadSecret, HttpStatusCode.Unauthorized)]
    public async Task Read_NeedsBothTheInternalProtocolAndItsSecret(string protocol, string user, string password, HttpStatusCode expected)
    {
        var (_, inputId, _) = await CreateMatch();

        var response = await api.CreateClient().PostAsJsonAsync(AuthUrl,
            new { action = "read", path = $"live/{inputId}", protocol, user, password, query = "" });

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("rtmp", HttpStatusCode.OK)]
    [InlineData("srt", HttpStatusCode.OK)]
    [InlineData("rtsp", HttpStatusCode.Unauthorized)]
    [InlineData("webrtc", HttpStatusCode.Unauthorized)]
    public async Task Publish_OnlyWorksOverWhatEncodersUse(string protocol, HttpStatusCode expected)
    {
        var (_, inputId, key) = await CreateMatch();

        var response = await api.CreateClient().PostAsJsonAsync(AuthUrl,
            new { action = "publish", path = $"live/{inputId}", protocol, query = $"key={key}" });

        Assert.Equal(expected, response.StatusCode);
    }
}
