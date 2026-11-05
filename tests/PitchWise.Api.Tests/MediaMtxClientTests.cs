using System.Net;
using System.Text;
using PitchWise.Api.Services;

namespace PitchWise.Api.Tests;

/// <summary>Reads what the real MediaMTX sends, which the fake used by the other tests can't check.</summary>
public class MediaMtxClientTests
{
    private const string Publishing = """
        {"name":"live/a","confName":"~^live/","source":{"type":"srtConn","id":"c1"},"ready":true,
         "readyTime":"2026-11-05T22:22:58.659091293Z","available":true,"availableTime":"2026-11-05T22:22:58.659091293Z",
         "online":true,"onlineTime":"2026-11-05T22:22:58.659091293Z","tracks":["H264"],"inboundBytes":1000}
        """;

    // A path can stay listed with nobody publishing, while a viewer or a closing connection holds it.
    private const string NoPublisher = """
        {"name":"live/b","confName":"~^live/","source":null,"ready":false,"readyTime":null,"available":false,
         "availableTime":null,"online":false,"onlineTime":null,"tracks":[],"inboundBytes":0}
        """;

    private sealed class Stub(Func<string, string> respond) : HttpMessageHandler
    {
        public List<string> Posted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) Posted.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Reply(string path) => path.StartsWith("/v3/paths/list")
        ? $$"""{"itemCount":2,"pageCount":1,"items":[{{NoPublisher}},{{Publishing}}]}"""
        : """{"itemCount":1,"pageCount":1,"items":[{"id":"c1","packetsReceivedUnique":10,"packetsReceivedLoss":1,"packetsReceivedDrop":0,"msRTT":4.5}]}""";

    private static MediaMtxClient Client(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://mediamtx.test") });

    [Fact]
    public async Task Publishers_LeaveOutAPathWithNobodyPublishing()
    {
        var publishers = await Client(new Stub(Reply)).GetPublishersAsync(default);

        var publisher = Assert.Single(publishers).Value;
        Assert.Equal("srt", publisher.Protocol);
        Assert.Equal(new DateTime(2026, 11, 5, 22, 22, 58, DateTimeKind.Utc), publisher.ConnectedSince.AddTicks(-publisher.ConnectedSince.Ticks % TimeSpan.TicksPerSecond));
        Assert.Equal(4.5, publisher.Srt!.RttMs);
    }

    [Fact]
    public async Task Kick_CopesWithAPathWithNobodyPublishing()
    {
        var stub = new Stub(Reply);

        await Client(stub).KickPublisherAsync("live/b", default);
        await Client(stub).KickPublisherAsync("live/a", default);

        Assert.Equal(["/v3/srtconns/kick/c1"], stub.Posted);
    }
}
