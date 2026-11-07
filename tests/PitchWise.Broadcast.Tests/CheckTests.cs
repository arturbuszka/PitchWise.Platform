using System.Net;
using System.Text;
using PitchWise.Data.Outputs;

namespace PitchWise.Broadcast.Tests;

public class SignalCheckTests
{
    [Fact]
    public void H264WithAac_IsFine()
    {
        var verdict = SignalCheck.Check(["H264", "MPEG-4 Audio"]);
        Assert.Null(verdict.Problem);
        Assert.Null(verdict.Warning);
    }

    [Theory]
    [InlineData("H265")]
    [InlineData("AV1")]
    [InlineData("VP9")]
    public void OtherVideo_IsNamedInTheProblem(string codec)
    {
        Assert.Contains(codec, SignalCheck.Check([codec, "MPEG-4 Audio"]).Problem);
    }

    [Theory]
    [InlineData("Opus")]
    [InlineData("MPEG-1/2 Audio")]
    [InlineData("AC3")]
    public void OtherAudio_IsNamedInTheProblem(string codec)
    {
        Assert.Contains(codec, SignalCheck.Check(["H264", codec]).Problem);
    }

    [Fact]
    public void NoAudio_CanBeSentButIsWarnedAbout()
    {
        var verdict = SignalCheck.Check(["H264"]);
        Assert.Null(verdict.Problem);
        Assert.NotNull(verdict.Warning);
    }

    [Fact]
    public void NoVideo_IsAProblem()
    {
        Assert.NotNull(SignalCheck.Check(["MPEG-4 Audio"]).Problem);
        Assert.NotNull(SignalCheck.Check([]).Problem);
    }

    [Fact]
    public void TheFirstOfEachKind_IsWhatGetsSent()
    {
        Assert.NotNull(SignalCheck.Check(["H264", "Opus", "MPEG-4 Audio"]).Problem);
        Assert.Null(SignalCheck.Check(["H264", "MPEG-4 Audio", "Opus"]).Problem);
    }
}

public class TargetGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)] // cloud metadata
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void IsPrivate(string address, bool expected) =>
        Assert.Equal(expected, TargetGuard.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public async Task APrivateAddress_IsRefusedUnlessAllowedForTesting()
    {
        Assert.NotNull(await new TargetGuard(new BroadcastSettings()).ProblemWithAsync("rtmp://127.0.0.1/live", default));
        Assert.Null(await new TargetGuard(new BroadcastSettings { AllowPrivateTargets = true }).ProblemWithAsync("rtmp://127.0.0.1/live", default));
    }
}

public class OutputKeyCipherTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    [Fact]
    public void RoundTrips_AndHidesTheKey()
    {
        var cipher = new OutputKeyCipher(KeyA);
        var id = Guid.NewGuid();

        var encrypted = cipher.Encrypt(id, "live_123_secret");

        Assert.DoesNotContain("secret", encrypted);
        Assert.NotEqual(encrypted, cipher.Encrypt(id, "live_123_secret"));
        Assert.Equal("live_123_secret", cipher.Decrypt(id, encrypted));
    }

    [Fact]
    public void AChangedKey_FailsWithAMessageSomeoneCanActOn()
    {
        var id = Guid.NewGuid();
        var encrypted = new OutputKeyCipher(KeyA).Encrypt(id, "secret");

        var error = Assert.Throws<OutputKeyException>(() => new OutputKeyCipher(KeyB).Decrypt(id, encrypted));

        Assert.Contains("Enter the destination's key again", error.Message);
    }

    [Fact]
    public void ARowCopiedOverAnother_DoesNotDecrypt()
    {
        var cipher = new OutputKeyCipher(KeyA);
        var encrypted = cipher.Encrypt(Guid.NewGuid(), "secret");

        Assert.Throws<OutputKeyException>(() => cipher.Decrypt(Guid.NewGuid(), encrypted));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("v1.")]
    [InlineData("v1.AAAA")]
    [InlineData("v1.not base64")]
    public void Damaged_FailsCleanly(string encrypted)
    {
        Assert.Throws<OutputKeyException>(() => new OutputKeyCipher(KeyA).Decrypt(Guid.NewGuid(), encrypted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void AMissingOrBadKey_MeansNotConfigured(string? key)
    {
        var cipher = new OutputKeyCipher(key);

        Assert.False(cipher.IsConfigured);
        var error = Assert.Throws<OutputKeyException>(() => cipher.Encrypt(Guid.NewGuid(), "secret"));
        Assert.Equal(OutputKeyCipher.NotConfigured, error.Message);
    }
}

/// <summary>Reads what the real MediaMTX sends.</summary>
public class MediaMtxSourcesTests
{
    private const string Reply = """
        {"itemCount":2,"pageCount":1,"items":[
         {"name":"live/a","confName":"~^live/","source":{"type":"srtConn","id":"c1"},"ready":true,
          "readyTime":"2026-11-05T22:22:58.659091293Z","tracks":["H265","MPEG-4 Audio"],"inboundBytes":1000},
         {"name":"live/b","confName":"~^live/","source":null,"ready":false,"readyTime":null,"tracks":[],"inboundBytes":0}]}
        """;

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Reply, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task Get_ListsOnlyPathsWithASignal()
    {
        var sources = new MediaMtxSources(new HttpClient(new Stub()) { BaseAddress = new Uri("http://mediamtx.test") });

        var signals = await sources.GetAsync(default);

        var tracks = Assert.Single(signals).Value;
        Assert.Equal(["H265", "MPEG-4 Audio"], tracks);
    }
}

public class OutputTargetTests
{
    [Fact]
    public void Publish_JoinsAddressAndKey()
    {
        Assert.Equal("rtmp://a/live2/k", OutputTarget.Publish("rtmp://a/live2/", "k"));
        Assert.Equal("rtmp://a/live2/k", OutputTarget.Publish("rtmp://a/live2", "k"));
    }

    [Theory]
    [InlineData("live_1234_abc")]
    [InlineData("abcd-efgh-ijkl-mnop")]
    public void RealKeyShapes_AreAccepted(string key) => Assert.Null(OutputTarget.KeyProblem(key));
}
