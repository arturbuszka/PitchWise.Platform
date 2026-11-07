using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Services;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Tests;

[Collection(nameof(ApiCollection))]
public class SwitcherTests(ApiFixture api)
{
    private async Task<(string MatchId, string First, string Second)> CreateMatch()
    {
        var response = await api.Client.PostAsJsonAsync("/api/matches", new { name = "Final" });
        var match = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("match");
        var matchId = match.GetProperty("id").GetString()!;
        var first = match.GetProperty("inputs")[0].GetProperty("id").GetString()!;
        var added = await api.Client.PostAsJsonAsync($"/api/matches/{matchId}/inputs", new { name = "Wide" });
        var second = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("input").GetProperty("id").GetString()!;
        return (matchId, first, second);
    }

    private Task<HttpResponseMessage> Enable(string matchId) => api.Client.PostAsync($"/api/matches/{matchId}/switcher", null);

    private async Task<JsonElement> Match(string matchId) =>
        await (await api.Client.GetAsync($"/api/matches/{matchId}")).Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Cut(string matchId, string inputId, HttpClient? client = null) =>
        (client ?? api.Client).PostAsJsonAsync($"/api/matches/{matchId}/switcher/cut", new { input_id = inputId });

    private async Task<JsonElement> Cuts(string matchId) =>
        await (await api.Client.GetAsync($"/api/matches/{matchId}/switcher/cuts")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task NewMatch_HasNoSwitcher_SoAReadyProgramGoesStraightThrough()
    {
        var (matchId, first, _) = await CreateMatch();

        var match = await Match(matchId);

        Assert.Equal(JsonValueKind.Null, match.GetProperty("switcher").ValueKind);
        Assert.Equal(first, match.GetProperty("program_input_id").GetString());
    }

    [Fact]
    public async Task Enable_MakesTheProgramAHiddenInput_WithTheCurrentCameraChosen()
    {
        var (matchId, first, _) = await CreateMatch();

        var response = await Enable(matchId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = await Match(matchId);
        var switcher = match.GetProperty("switcher");
        var program = switcher.GetProperty("program_input_id").GetString();
        Assert.Equal(program, match.GetProperty("program_input_id").GetString());
        Assert.NotEqual(first, program);
        Assert.Equal(first, switcher.GetProperty("video_input_id").GetString());
        Assert.Equal(first, switcher.GetProperty("audio_input_id").GetString());
        Assert.Equal("waiting_for_cameras", switcher.GetProperty("status").GetString());
        Assert.Equal(2, match.GetProperty("inputs").GetArrayLength());
        Assert.DoesNotContain(match.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("id").GetString() == program);
    }

    [Fact]
    public async Task Enable_Twice_KeepsOneProgramAndWhatWasChosen()
    {
        var (matchId, _, second) = await CreateMatch();
        await Enable(matchId);
        var program = (await Match(matchId)).GetProperty("program_input_id").GetString();
        await Cut(matchId, second);

        await Enable(matchId);

        var switcher = (await Match(matchId)).GetProperty("switcher");
        Assert.Equal(program, switcher.GetProperty("program_input_id").GetString());
        Assert.Equal(second, switcher.GetProperty("video_input_id").GetString());
    }

    [Fact]
    public async Task Disable_PutsTheChosenCameraOnAir_AndEnablingAgainReusesTheProgram()
    {
        var (matchId, _, second) = await CreateMatch();
        await Enable(matchId);
        var program = (await Match(matchId)).GetProperty("program_input_id").GetString();
        await Cut(matchId, second);

        var response = await api.Client.DeleteAsync($"/api/matches/{matchId}/switcher");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = await Match(matchId);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("switcher").ValueKind);
        Assert.Equal(second, match.GetProperty("program_input_id").GetString());

        await Enable(matchId);
        var again = await Match(matchId);
        Assert.Equal(program, again.GetProperty("program_input_id").GetString());
        Assert.Equal(second, again.GetProperty("switcher").GetProperty("video_input_id").GetString());
    }

    [Fact]
    public async Task Cut_ChangesVideoOnly_AndIsRememberedInTheHistory()
    {
        var (matchId, first, second) = await CreateMatch();
        await Enable(matchId);

        var response = await Cut(matchId, second);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var switcher = (await Match(matchId)).GetProperty("switcher");
        Assert.Equal(second, switcher.GetProperty("video_input_id").GetString());
        Assert.Equal(first, switcher.GetProperty("audio_input_id").GetString());

        var newest = (await Cuts(matchId))[0];
        Assert.Equal("video", newest.GetProperty("kind").GetString());
        Assert.Equal(second, newest.GetProperty("input_id").GetString());
        Assert.Equal("operator", newest.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Audio_ChangesSoundOnly()
    {
        var (matchId, first, second) = await CreateMatch();
        await Enable(matchId);

        var response = await api.Client.PostAsJsonAsync($"/api/matches/{matchId}/switcher/audio", new { input_id = second });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var switcher = (await Match(matchId)).GetProperty("switcher");
        Assert.Equal(first, switcher.GetProperty("video_input_id").GetString());
        Assert.Equal(second, switcher.GetProperty("audio_input_id").GetString());
        Assert.Equal("audio", (await Cuts(matchId))[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Cut_ToWhatIsAlreadyChosen_AddsNothingToTheHistory()
    {
        var (matchId, first, _) = await CreateMatch();
        await Enable(matchId);
        var before = (await Cuts(matchId)).GetArrayLength();

        var response = await Cut(matchId, first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, (await Cuts(matchId)).GetArrayLength());
    }

    [Fact]
    public async Task History_GivesThePositionOnTheMatchTimeline()
    {
        var (matchId, _, second) = await CreateMatch();
        await Enable(matchId);
        var id = Guid.Parse(matchId);
        await api.WithDb(db => db.Matches.Where(m => m.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.StartedAt, DateTime.UtcNow.AddSeconds(-90))));

        await Cut(matchId, second);

        var seconds = (await Cuts(matchId))[0].GetProperty("match_seconds").GetDouble();
        Assert.InRange(seconds, 89, 95);
    }

    [Fact]
    public async Task Cut_WithoutASwitcher_IsRefused()
    {
        var (matchId, _, second) = await CreateMatch();

        Assert.Equal(HttpStatusCode.Conflict, (await Cut(matchId, second)).StatusCode);
    }

    [Fact]
    public async Task Cut_ToTheProgramOrAnotherMatchsInput_IsNotFound()
    {
        var (matchId, _, _) = await CreateMatch();
        var (_, otherInput, _) = await CreateMatch();
        await Enable(matchId);
        var program = (await Match(matchId)).GetProperty("program_input_id").GetString()!;

        Assert.Equal(HttpStatusCode.NotFound, (await Cut(matchId, program)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cut(matchId, otherInput)).StatusCode);
    }

    [Fact]
    public async Task OtherTenant_CantSeeOrCut()
    {
        var (matchId, _, second) = await CreateMatch();
        await Enable(matchId);
        var other = await api.CreateTenantClient();

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/matches/{matchId}/switcher", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Cut(matchId, second, other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/matches/{matchId}/switcher/cuts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/matches/{matchId}/switcher/preview")).StatusCode);
    }

    [Fact]
    public async Task SetProgram_IsRefusedWhileTheSwitcherDecides()
    {
        var (matchId, _, second) = await CreateMatch();
        await Enable(matchId);

        var response = await api.Client.PutAsJsonAsync($"/api/matches/{matchId}/program", new { input_id = second });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ProgramInput_CantBeChosenOrHaveItsKeyReplaced()
    {
        var (matchId, _, _) = await CreateMatch();
        await Enable(matchId);
        var program = (await Match(matchId)).GetProperty("program_input_id").GetString()!;

        var rotate = await api.Client.PostAsync($"/api/matches/{matchId}/inputs/{program}/rotate-key", null);

        Assert.Equal(HttpStatusCode.NotFound, rotate.StatusCode);
    }

    [Fact]
    public async Task End_StopsTheSwitcher_AndKeepsTheHistoryAndProgram()
    {
        var (matchId, _, second) = await CreateMatch();
        await Enable(matchId);
        await Cut(matchId, second);
        var program = (await Match(matchId)).GetProperty("program_input_id").GetString();

        await api.Client.PostAsync($"/api/matches/{matchId}/end", null);

        var match = await Match(matchId);
        Assert.Equal(JsonValueKind.Null, match.GetProperty("switcher").ValueKind);
        Assert.Equal(program, match.GetProperty("program_input_id").GetString());
        Assert.True((await Cuts(matchId)).GetArrayLength() >= 3);
        Assert.Equal(HttpStatusCode.Conflict, (await Enable(matchId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Cut(matchId, second)).StatusCode);
    }

    [Fact]
    public async Task QuietService_ReadsAsOffline_NotAsRunning()
    {
        var (matchId, _, _) = await CreateMatch();
        await Enable(matchId);
        var id = Guid.Parse(matchId);
        await api.WithDb(db => db.MatchSwitchers.Where(s => s.MatchId == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Status, SwitcherStatus.Running)
            .SetProperty(s => s.LastSeenAt, DateTime.UtcNow.AddMinutes(-1))));

        Assert.Equal("offline", (await Match(matchId)).GetProperty("switcher").GetProperty("status").GetString());
    }

    private Task<HttpResponseMessage> AskToRead(string protocol, string path, string query) =>
        api.CreateClient().PostAsJsonAsync($"/internal/mediamtx/auth/{ApiFixture.MediaMtxSecret}",
            new { action = "read", path, query, protocol });

    [Fact]
    public async Task Preview_OpensExactlyThePathItWasSignedFor()
    {
        var (matchId, first, second) = await CreateMatch();
        await Enable(matchId);

        var preview = await (await api.Client.GetAsync($"/api/matches/{matchId}/switcher/preview")).Content.ReadFromJsonAsync<JsonElement>();
        var source = preview.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("input_id").GetString() == first);
        var url = new Uri(source.GetProperty("whep_url").GetString()!);
        var query = url.Query.TrimStart('?');

        Assert.Equal($"/live/{first}/whep", url.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, (await AskToRead("webrtc", $"live/{first}", query)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskToRead("webrtc", $"live/{second}", query)).StatusCode);
        // The token is for WebRTC. It opens nothing that is open to the internet.
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskToRead("rtmp", $"live/{first}", query)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskToRead("srt", $"live/{first}", query)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token=")]
    [InlineData("token=abc")]
    [InlineData("token=99999999999.0000")]
    public async Task Preview_WithoutAValidToken_IsRefused(string query)
    {
        var (_, first, _) = await CreateMatch();

        Assert.Equal(HttpStatusCode.Unauthorized, (await AskToRead("webrtc", $"live/{first}", query)).StatusCode);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void PreviewToken_Expires()
    {
        var clock = new Clock();
        var signer = new PreviewSigner(new AppSettings { MediaMtxReadSecret = "secret" }, clock);
        var (token, _) = signer.Sign("live/x");

        Assert.True(signer.IsValid("live/x", token));
        clock.Now += PreviewSigner.Lifetime + TimeSpan.FromSeconds(2);
        Assert.False(signer.IsValid("live/x", token));
    }

    [Fact]
    public void PreviewToken_IsWorthlessWithoutTheSecret()
    {
        var clock = new Clock();
        var (token, _) = new PreviewSigner(new AppSettings { MediaMtxReadSecret = "" }, clock).Sign("live/x");

        Assert.False(new PreviewSigner(new AppSettings { MediaMtxReadSecret = "" }, clock).IsValid("live/x", token));
        Assert.False(new PreviewSigner(new AppSettings { MediaMtxReadSecret = "other" }, clock).IsValid("live/x", token));
    }
}
