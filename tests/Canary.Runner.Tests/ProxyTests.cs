using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Canary.Runner.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RadiCorder.Logics.Application;
using RadiCorder.Logics.Services;

namespace Canary.Runner.Tests;

[TestFixture]
public class ProxyTests
{
    [TestCase("/api/programs/radiko-proxy")]
    [TestCase("/api/programs/radiko-proxy/file.m3u8")]
    public async Task 既存の両ルートで認証なしの要求を拒否する(string route)
    {
        var upstream = new FixtureHandler(_ => throw new InvalidOperationException("Unexpected request."));
        await using var context = await CreateContext(upstream);
        using var client = CreateClient(context);
        using var response = await client.GetAsync(route + "?target=https%3A%2F%2Fradiko.jp%2Fa.m3u8");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("\"target and proxyKey/token are required.\""));
        Assert.That(upstream.Requests, Is.Empty);
    }

    [TestCase("http://radiko.jp/a.m3u8", "token=fixture-token")]
    [TestCase("https://example.com/a.m3u8", "token=fixture-token")]
    [TestCase("https://radiko.jp/a.m3u8", "proxyKey=invalid-ticket")]
    public async Task 許可外URLと無効なチケットを上流へ送らない(string target, string credential)
    {
        var upstream = new FixtureHandler(_ => throw new InvalidOperationException("Unexpected request."));
        await using var context = await CreateContext(upstream);
        using var client = CreateClient(context);
        using var response = await client.GetAsync($"/api/programs/radiko-proxy?target={Uri.EscapeDataString(target)}&{credential}");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(upstream.Requests, Is.Empty);
    }

    [Test]
    public async Task チケットから認証ヘッダーを付け本体の処理でセグメントと鍵のURLを書き換える()
    {
        var upstream = new FixtureHandler(_ => Text("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:5,\na.aac\n", "application/vnd.apple.mpegurl"));
        await using var context = await CreateContext(upstream);
        var key = context.Services.GetRequiredService<IRadikoProxyTicketService>().IssueTokenTicket("fixture-token");
        using var client = CreateClient(context);
        using var response = await client.GetAsync($"/api/programs/radiko-proxy?target=https%3A%2F%2Fradiko.jp%2Flive%2Fmedia.m3u8&proxyKey={key}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Does.Contain(Uri.EscapeDataString("https://radiko.jp/live/key.bin")));
        Assert.That(body, Does.Contain(Uri.EscapeDataString("https://radiko.jp/live/a.aac")));
        Assert.That(body, Does.Contain("proxyKey=" + key));
        Assert.That(body, Does.Not.Contain("fixture-token"));
        Assert.That(upstream.Requests.Single().Token, Is.EqualTo("fixture-token"));
        Assert.That(upstream.Requests.Single().UserAgent, Is.EqualTo("RadiCorder.Logic.Canary/0.1"));
    }

    [Test]
    public async Task 本体のライブ解決処理に録音開始時刻を渡す()
    {
        var upstream = new FixtureHandler(request => request.RequestUri!.AbsolutePath.EndsWith("master.m3u8")
            ? Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100\nmedia.m3u8\n", "application/vnd.apple.mpegurl")
            : Text("#EXTM3U\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:1\n#EXT-X-PROGRAM-DATE-TIME:2026-10-03T00:00:00Z\n#EXTINF:5,\na.aac\n#EXT-X-PROGRAM-DATE-TIME:2026-10-03T00:00:05Z\n#EXTINF:5,\nb.aac\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl"));
        await using var context = await CreateContext(upstream);
        using var client = CreateClient(context);
        using var response = await client.GetAsync("/api/programs/radiko-proxy?target=https%3A%2F%2Fradiko.jp%2Flive%2Fmaster.m3u8&token=fixture-token&resolveLivePlaylist=true&recordingStartUtc=2026-10-03T00%3A00%3A00Z");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(upstream.Requests.Select(request => request.Uri.AbsolutePath), Is.EqualTo(new[] { "/live/master.m3u8", "/live/media.m3u8" }));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(Uri.EscapeDataString("https://radiko.jp/live/b.aac")));
    }

    [Test]
    public async Task 音声データを変更せず中継する()
    {
        byte[] bytes = [0, 1, 2, 128, 255];
        var upstream = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        await using var context = await CreateContext(upstream);
        using var client = CreateClient(context);
        using var response = await client.GetAsync("/api/programs/radiko-proxy/file.aac?target=https%3A%2F%2Fradiko.jp%2Fa.aac&token=fixture-token");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
    }

    [Test]
    public async Task 上流の失敗ステータスを保持する()
    {
        var upstream = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var context = await CreateContext(upstream);
        using var client = CreateClient(context);
        using var response = await client.GetAsync("/api/programs/radiko-proxy?target=https%3A%2F%2Fradiko.jp%2Fa.aac&token=fixture-token");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
    }

    [Test]
    public async Task 上流の通信例外を502にする()
    {
        var upstream = new FixtureHandler(_ => throw new HttpRequestException("Fixture failure."));
        await using var context = await CreateContext(upstream);
        using var client = CreateClient(context);
        using var response = await client.GetAsync("/api/programs/radiko-proxy?target=https%3A%2F%2Fradiko.jp%2Fa.aac&token=fixture-token");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
    }

    private static Task<LogicContext> CreateContext(FixtureHandler upstream) => LogicContext.CreateAsync("", "", services =>
        services.AddHttpClient(HttpClientNames.RadikoStreaming).ConfigurePrimaryHttpMessageHandler(() => upstream));

    private static HttpClient CreateClient(LogicContext context) => new()
    {
        BaseAddress = new Uri(context.Services.GetRequiredService<ILocalApplicationUrlService>().GetBaseUrl()!),
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static HttpResponseMessage Text(string text, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, mediaType)
    };

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal ConcurrentQueue<(Uri Uri, string Token, string UserAgent)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!, request.Headers.GetValues("X-Radiko-Authtoken").Single(),
                request.Headers.GetValues("User-Agent").Single()));
            return Task.FromResult(respond(request));
        }
    }
}
