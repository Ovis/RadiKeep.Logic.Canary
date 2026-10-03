using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Xml.Linq;
using Canary.Runner.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RadiCorder.Logics.Application;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.Tests;

[TestFixture]
public class RadikoChecksTests
{
    private const string UserId = "offline+user@example.invalid";
    private const string Password = "offline&password";
    private const string ProbeSession = "fixture-logout-session";
    private const string StationXml = """
        <radiko>
          <stations region_id="kanto" region_name="関東">
            <station><id>TOKYO</id><name>ＦＭ東京</name><area_id>JP13</area_id><areafree>1</areafree><timefree>1</timefree></station>
          </stations>
          <stations region_id="kinki" region_name="近畿">
            <station><id>OSAKA</id><name>大阪</name><area_id>JP27</area_id></station>
          </stations>
        </radiko>
        """;
    private string artifactRoot = null!;
    private string LogPath => Path.Combine(artifactRoot, "check.log");

    [SetUp]
    public void SetUp()
    {
        artifactRoot = Path.Combine(Path.GetTempPath(), "canary-radiko-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(artifactRoot, recursive: true);

    [Test]
    public async Task 全国局定義を本体で解析して任意項目の欠落を許容し取得結果を保存する()
    {
        var handler = new FixtureHandler(_ => Text(StationXml, "application/xml"));
        await using var context = await CreateContext(handler);
        var result = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(context, LogPath);

        Assert.That(result.CheckId, Is.EqualTo("C006_RADIKO_STATIONS_FETCH"));
        Assert.That(result.Result, Is.EqualTo("PASS"));
        Assert.That(result.ErrorCode, Is.Empty);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.Requests[0].Uri.AbsoluteUri, Is.EqualTo("http://radiko.jp/v3/station/region/full.xml"));
        Assert.That(handler.Requests[0].UserAgent, Is.EqualTo("RadiCorder.Logic.Canary/0.1"));
        var stations = JsonSerializer.Deserialize<List<RadikoStation>>(await File.ReadAllTextAsync(Path.Combine(artifactRoot, "check_stations.json")))!;
        Assert.That(stations.Select(station => station.StationId), Is.EqualTo(new[] { "TOKYO", "OSAKA" }));
        Assert.That(stations[0].StationName, Is.EqualTo("FM東京"));
        Assert.That(stations[0].AreaFree && stations[0].TimeFree, Is.True);
        Assert.That(stations.Select(station => station.RegionOrder), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(stations[1].LogoPath, Is.Empty);
        Assert.That(context.DbContext.RadikoStations.Any(), Is.False);
    }

    [TestCase("StationId", "id")]
    [TestCase("StationName", "name")]
    [TestCase("Area", "area_id")]
    [TestCase("RegionId", "region_id")]
    [TestCase("RegionName", "region_name")]
    public async Task 全国局定義の必須項目欠落を記録して失敗とする(string field, string xmlName)
    {
        var doc = XDocument.Parse(StationXml);
        if (xmlName.StartsWith("region_"))
        {
            doc.Descendants("stations").First().Attribute(xmlName)!.Remove();
        }
        else
        {
            doc.Descendants("station").First().Element(xmlName)!.Remove();
        }
        await using var context = await CreateContext(new FixtureHandler(_ => Text(doc.ToString(), "application/xml")));
        var result = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("FAIL"));
        Assert.That(result.ErrorCode, Is.EqualTo("E-C006-SCHEMA"));
        Assert.That(await File.ReadAllTextAsync(LogPath), Does.Contain($"missing_fields={field}"));
        Assert.That(File.Exists(Path.Combine(artifactRoot, "check_stations.json")), Is.True);
    }

    [TestCase("<radiko/>", "E-C006-EMPTY")]
    [TestCase("<radiko>", "E-C006-FETCH")]
    public async Task 空の全国局定義と解析不能な応答を失敗とする(string xml, string errorCode)
    {
        await using var context = await CreateContext(new FixtureHandler(_ => Text(xml, "application/xml")));
        var result = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("FAIL"));
        Assert.That(result.ErrorCode, Is.EqualTo(errorCode));
    }

    [Test]
    public async Task 全国局定義の重複局IDを本体の集約仕様に合わせて許容する()
    {
        var doc = XDocument.Parse(StationXml);
        doc.Descendants("station").Last().Element("id")!.Value = "TOKYO";
        await using var context = await CreateContext(new FixtureHandler(_ => Text(doc.ToString(), "application/xml")));
        var result = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("PASS"));
        Assert.That(await File.ReadAllTextAsync(LogPath), Does.Contain("unique_station_count=1"));
    }

    [Test]
    public async Task 全国局定義の通信例外は本体の例外包装を通して警告とする()
    {
        await using var context = await CreateContext(new FixtureHandler(_ => throw new TimeoutException("fixture timeout")));
        var result = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("WARN"));
        Assert.That(result.ErrorCode, Is.EqualTo("E-C006-FETCH"));
    }

    [TestCase(HttpStatusCode.Forbidden, 1)]
    [TestCase(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task 全国局定義のHTTP失敗で正常応答として保存しない(HttpStatusCode status, int requestCount)
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(status));
        await using var context = await CreateContext(handler);
        var result = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(context, LogPath);
        // 本体の取得処理がHTTP失敗を通信例外に変換するため、共通判定ではWARNとなる。
        Assert.That(result.Result, Is.EqualTo("WARN"));
        Assert.That(result.ErrorCode, Is.EqualTo("E-C006-FETCH"));
        Assert.That(handler.Requests, Has.Count.EqualTo(requestCount));
        Assert.That(File.Exists(Path.Combine(artifactRoot, "check_stations.json")), Is.False);
    }

    [Test]
    public async Task ログアウト専用のセッションを使い録音用キャッシュと資格情報の秘匿を維持する()
    {
        var loginCount = 0;
        var handler = new FixtureHandler(request => request.Uri.AbsolutePath.EndsWith("/login")
            ? LoginResponse(++loginCount == 1 ? "fixture-recording-session" : ProbeSession)
            : new HttpResponseMessage(HttpStatusCode.OK));
        await using var context = await CreateContext(handler);
        var recordingLogin = await context.RadikoLogic.LoginRadikoAsync();
        var result = await RadikoLogoutCheck.CheckRadikoLogoutAsync(context, LogPath);
        var cachedLogin = await context.RadikoLogic.LoginRadikoAsync();

        Assert.That(result.CheckId, Is.EqualTo("C011_RADIKO_LOGOUT"));
        Assert.That(result.Result, Is.EqualTo("PASS"));
        Assert.That(result.ErrorCode, Is.Empty);
        Assert.That(recordingLogin.IsSuccess && cachedLogin.IsSuccess, Is.True);
        Assert.That(cachedLogin.Session, Is.EqualTo("fixture-recording-session"));
        Assert.That(handler.Requests, Has.Count.EqualTo(3));
        Assert.That(handler.Requests.All(request => request.Method == HttpMethod.Post), Is.True);
        Assert.That(handler.Requests.All(request => request.UserAgent == "RadiCorder.Logic.Canary/0.1"), Is.True);
        var loginRequest = handler.Requests[1];
        Assert.That(loginRequest.Uri.AbsoluteUri, Is.EqualTo("https://radiko.jp/ap/member/webapi/member/login"));
        var loginForm = HttpUtility.ParseQueryString(loginRequest.Body);
        Assert.That(loginForm["mail"], Is.EqualTo(UserId));
        Assert.That(loginForm["pass"], Is.EqualTo(Password));
        var logoutRequest = handler.Requests[2];
        Assert.That(logoutRequest.Uri.AbsoluteUri, Is.EqualTo("https://radiko.jp/v4/api/member/logout"));
        Assert.That(HttpUtility.ParseQueryString(logoutRequest.Body)["radiko_session"], Is.EqualTo(ProbeSession));
        var output = await File.ReadAllTextAsync(LogPath) + JsonSerializer.Serialize(result);
        Assert.That(output, Does.Not.Contain(UserId).And.Not.Contain(Password).And.Not.Contain(ProbeSession).And.Not.Contain(recordingLogin.Session));
    }

    [TestCase(HttpStatusCode.OK, "", "FAIL", "E-C011-LOGIN")]
    [TestCase(HttpStatusCode.Forbidden, "", "FAIL", "E-C011-LOGIN")]
    [TestCase(HttpStatusCode.OK, "invalid-json", "FAIL", "E-C011-EXCEPTION")]
    public async Task 専用ログインの失敗やセッション欠落時はログアウトを送信しない(HttpStatusCode status, string json, string expected, string errorCode)
    {
        var handler = new FixtureHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json == "" ? "{\"radiko_session\":\"\"}" : json, Encoding.UTF8, "application/json")
        });
        await using var context = await CreateContext(handler);
        var result = await RadikoLogoutCheck.CheckRadikoLogoutAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo(expected));
        Assert.That(result.ErrorCode, Is.EqualTo(errorCode));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Uri.AbsolutePath, Does.EndWith("/login"));
    }

    [Test]
    public async Task 資格情報がない場合はログアウトのための通信を行わない()
    {
        var handler = new FixtureHandler(_ => throw new InvalidOperationException("Unexpected request"));
        await using var context = await CreateContext(handler, credentials: false);
        var result = await RadikoLogoutCheck.CheckRadikoLogoutAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("FAIL"));
        Assert.That(result.ErrorCode, Is.EqualTo("E-C011-NO-CREDENTIALS"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task 専用ログインの通信例外を警告とし資格情報をログに含めない()
    {
        await using var context = await CreateContext(new FixtureHandler(_ => throw new TimeoutException($"{UserId} {Password}")));
        var result = await RadikoLogoutCheck.CheckRadikoLogoutAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("WARN"));
        Assert.That(result.ErrorCode, Is.EqualTo("E-C011-EXCEPTION"));
        Assert.That(await File.ReadAllTextAsync(LogPath) + JsonSerializer.Serialize(result), Does.Not.Contain(UserId).And.Not.Contain(Password));
    }

    [TestCase(HttpStatusCode.Forbidden, false, 2)]
    [TestCase(HttpStatusCode.ServiceUnavailable, false, 4)]
    [TestCase(HttpStatusCode.OK, true, 2)]
    public async Task ログアウトのHTTP失敗と本体がfalseに変換する通信例外を失敗とする(HttpStatusCode status, bool networkFailure, int requestCount)
    {
        var handler = new FixtureHandler(request => request.Uri.AbsolutePath.EndsWith("/login")
            ? LoginResponse(ProbeSession)
            : networkFailure ? throw new TimeoutException("fixture timeout") : new HttpResponseMessage(status));
        await using var context = await CreateContext(handler);
        var result = await RadikoLogoutCheck.CheckRadikoLogoutAsync(context, LogPath);
        Assert.That(result.Result, Is.EqualTo("FAIL"));
        Assert.That(result.ErrorCode, Is.EqualTo("E-C011-LOGOUT"));
        Assert.That(handler.Requests, Has.Count.EqualTo(requestCount));
        Assert.That(await File.ReadAllTextAsync(LogPath), Does.Contain("logout_success=False"));
    }

    private static Task<LogicContext> CreateContext(FixtureHandler handler, bool credentials = true) =>
        LogicContext.CreateAsync(credentials ? UserId : "", credentials ? Password : "", services =>
            services.AddHttpClient(HttpClientNames.Radiko).ConfigurePrimaryHttpMessageHandler(() => handler));

    private static HttpResponseMessage LoginResponse(string session) => Text(JsonSerializer.Serialize(new
    {
        radiko_session = session, paid_member = "1", areafree = "1"
    }), "application/json");

    private static HttpResponseMessage Text(string content, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, mediaType)
    };

    private sealed record FixtureRequest(Uri Uri, HttpMethod Method, string UserAgent, string Body);

    private sealed class FixtureHandler(Func<FixtureRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<FixtureRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new FixtureRequest(request.RequestUri!, request.Method, request.Headers.UserAgent.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return respond(captured);
        }
    }
}
