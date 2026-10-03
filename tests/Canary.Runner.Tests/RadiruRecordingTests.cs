using System.Net;
using System.Text;
using System.Text.Json;
using Canary.Runner.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RadiCorder.Logics.Application;
using RadiCorder.Logics.Domain.Recording;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;

namespace Canary.Runner.Tests;

[TestFixture]
public class RadiruRecordingTests
{
    [TestCase(true, 32768, "PASS", "")]
    [TestCase(true, 32767, "FAIL", "E-C003-OUTPUT-TOO-SMALL")]
    [TestCase(true, -1, "FAIL", "E-C003-OUTPUT-MISSING")]
    [TestCase(false, 32768, "FAIL", "E-C003-RECORD-EXEC")]
    public async Task 本体の定義取得とDBと録音ソースを使い出力結果を判定する(bool recorded, int size, string expected, string errorCode)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new RadiruProgramJsonEntity
        {
            Id = "fixture", Name = "fixture program", StartDate = now.AddMinutes(-1), EndDate = now.AddMinutes(1)
        };
        var transcoder = new FixtureTranscoder(recorded, size);
        var artifactRoot = Path.Combine(Path.GetTempPath(), "canary-record-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var context = await CreateContext(program, transcoder);
            var result = await RadiruRecordingChecks.CheckRadiruRealtimeRecordingAsync(context, "JP13", "r1", 10, artifactRoot, Path.Combine(artifactRoot, "check.log"));
            Assert.That(result.CheckId, Is.EqualTo("C003_RADIRU_REALTIME_RECORD"));
            Assert.That(result.Result, Is.EqualTo(expected));
            Assert.That(result.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(transcoder.Source, Is.Not.Null);
            Assert.That(transcoder.Source!.StreamUrl, Is.EqualTo("https://fixture.invalid/live.m3u8"));
            Assert.That(transcoder.Source.ProgramInfo.Title, Is.EqualTo("fixture program"));
            Assert.That(transcoder.Source.ProgramInfo.EndTime - transcoder.Source.ProgramInfo.StartTime, Is.EqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(context.DbContext.NhkRadiruAreas.Any(area => area.AreaId == "130"), Is.True);
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task 聞き逃し候補を選び本体の録音ソースで期限とURLを確認する(bool expired)
    {
        var now = DateTimeOffset.UtcNow;
        var program = new RadiruProgramJsonEntity
        {
            Id = "fixture", Name = "fixture on-demand", StartDate = now.AddMinutes(-20), EndDate = now.AddMinutes(-10),
            About = new About { Audio = new Audio { Expires = now.AddDays(expired ? -1 : 1), DetailedContent = [new DetailedContent { Name = "fallback", ContentUrl = "https://fixture.invalid/fallback.m3u8" }, new DetailedContent { Name = "hls_widevine", ContentUrl = "https://fixture.invalid/ondemand.m3u8" }] } }
        };
        var transcoder = new FixtureTranscoder(true, 32768);
        var requests = new List<Uri>();
        var artifactRoot = Path.Combine(Path.GetTempPath(), "canary-ondemand-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var context = await CreateContext(program, transcoder, requests);
            var result = await RadiruRecordingChecks.CheckRadiruOnDemandRecordingAsync(context, "130", "r1", artifactRoot, Path.Combine(artifactRoot, "check.log"));
            Assert.That(result.Result, Is.EqualTo(expired ? "FAIL" : "PASS"));
            Assert.That(result.CheckId, Is.EqualTo("C005_RADIRU_ONDEMAND_RECORD"));
            Assert.That(result.ErrorCode, Is.EqualTo(expired ? "E-C005-EXPIRED" : ""));
            var dailyRequests = requests.Where(uri => !uri.AbsolutePath.EndsWith("config_web.xml")).ToList();
            Assert.That(dailyRequests, Has.Count.EqualTo(2));
            Assert.That(dailyRequests.All(uri => uri.AbsolutePath.StartsWith("/130/r1/")), Is.True);
            var today = TimeZoneInfo.ConvertTime(now, CanaryInputs.ResolveJapanTimeZone());
            Assert.That(dailyRequests.Select(uri => Path.GetFileNameWithoutExtension(uri.AbsolutePath)),
                Is.EquivalentTo(new[] { today.ToString("yyyy-MM-dd"), today.AddDays(-1).ToString("yyyy-MM-dd") }));
            if (expired)
            {
                Assert.That(transcoder.Source, Is.Null);
            }
            else
            {
                Assert.That(transcoder.Source!.StreamUrl, Is.EqualTo("https://fixture.invalid/ondemand.m3u8"));
                Assert.That(transcoder.Source.ProgramInfo.ProgramId, Is.EqualTo("fixture"));
                Assert.That(transcoder.Source.Options.IsOnDemand, Is.True);
            }
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private static Task<LogicContext> CreateContext(RadiruProgramJsonEntity program, FixtureTranscoder transcoder, List<Uri>? requests = null) =>
        LogicContext.CreateAsync("", "", services =>
        {
            services.AddHttpClient(HttpClientNames.Radiru).ConfigurePrimaryHttpMessageHandler(() => new RadiruHandler(program, requests));
            services.AddSingleton<IMediaTranscodeService>(transcoder);
        });

    private sealed class RadiruHandler(RadiruProgramJsonEntity program, List<Uri>? requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests?.Add(request.RequestUri!);
            const string config = """
                <radiru_config>
                  <url_program_day>https://fixture.invalid/{area}/{service}/[YYYY-MM-DD].json</url_program_day>
                  <stream_url><data><areakey>130</areakey><areajp>東京</areajp><apikey>fixture</apikey><r1hls>https://fixture.invalid/live.m3u8</r1hls><fmhls>https://fixture.invalid/fm.m3u8</fmhls></data><data><areakey>270</areakey><areajp>大阪</areajp><r1hls>https://fixture.invalid/osaka.m3u8</r1hls></data></stream_url>
                </radiru_config>
                """;
            var content = request.RequestUri!.AbsolutePath.EndsWith("config_web.xml")
                ? new StringContent(config, Encoding.UTF8, "application/xml")
                : new StringContent(JsonSerializer.Serialize(new { r1 = new { publication = new[] { program } } }), Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FixtureTranscoder(bool recorded, int size) : IMediaTranscodeService
    {
        internal RecordingSourceResult? Source { get; private set; }
        public async ValueTask<bool> RecordAsync(RecordingSourceResult source, MediaPath path, CancellationToken cancellationToken = default)
        {
            Source = source;
            if (size >= 0)
            {
                await File.WriteAllBytesAsync(path.TempFilePath, new byte[size], cancellationToken);
            }
            return recorded;
        }
    }
}
