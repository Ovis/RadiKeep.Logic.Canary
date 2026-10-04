using System.Text;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.Tests;

[TestFixture]
public class ContractTests
{
    [TestCase("-1", 10, 10)]
    [TestCase("10", 10, 10)]
    [TestCase("90", 90, 90)]
    [TestCase("200", 180, 120)]
    [TestCase("invalid", 30, 30)]
    public void CLIの秒数制限と無効入力の既定値を維持する(string input, int realtime, int timefree)
    {
        var options = CanaryOptions.Parse(["--realtime-record-seconds", input, "--timefree-record-seconds", input]);
        Assert.That(options.RealtimeRecordSeconds, Is.EqualTo(realtime));
        Assert.That(options.TimefreeRecordSeconds, Is.EqualTo(timefree));
    }

    [Test]
    public void CLIの既定値と重複指定時の優先順位を維持する()
    {
        var defaults = CanaryOptions.Parse([]);
        Assert.That(defaults.StatusPath, Is.EqualTo("results/status.json"));
        Assert.That(defaults.LogDirectory, Is.EqualTo("logs"));
        Assert.That(defaults.RecordOutputDirectory, Is.EqualTo("artifacts/recordings"));
        Assert.That(defaults.RadikoStationId, Is.EqualTo("TBS"));
        Assert.That(defaults.RadiruAreaId, Is.EqualTo("JP13"));
        Assert.That(defaults.RadiruStationId, Is.EqualTo("r1"));
        var options = CanaryOptions.Parse(["--RADIKO-STATION-ID", "A", "--radiko-station-id", "B", "--radiko-user-id", "offline-user", "--radiko-password", "offline-password"]);
        Assert.That(options.RadikoStationId, Is.EqualTo("B"));
        Assert.That(options.RadikoUserId, Is.EqualTo("offline-user"));
        Assert.That(options.RadikoPassword, Is.EqualTo("offline-password"));
    }

    [TestCase("JP13", "130")]
    [TestCase(" jp27 ", "270")]
    [TestCase("130", "130")]
    [TestCase("unknown", "unknown")]
    public void らじるの入力エリアを従来どおり正規化する(string input, string expected) =>
        Assert.That(CanaryInputs.NormalizeRadiruAreaKey(input), Is.EqualTo(expected));

    [TestCase("connection reset", "WARN")]
    [TestCase("DNS failure", "WARN")]
    [TestCase("timed out", "WARN")]
    [TestCase("schema changed", "FAIL")]
    [TestCase("missing file", "FAIL")]
    public void 既存メッセージによる通信障害判定を維持する(string message, string expected)
    {
        var result = CheckFailures.CreateFailureResult("CHECK", "ERROR", message);
        Assert.That(result.Result, Is.EqualTo(expected));
        Assert.That(result.CheckId, Is.EqualTo("CHECK"));
        Assert.That(result.ErrorCode, Is.EqualTo("ERROR"));
    }

    [Test]
    public void 内部例外までたどって一時的通信障害を判定する()
    {
        Assert.That(CheckFailures.CreateFailureResult("CHECK", "ERROR", "request failed",
            new InvalidOperationException("wrapper", new HttpRequestException("fixture"))).Result, Is.EqualTo("WARN"));
        Assert.That(CheckFailures.CreateFailureResult("CHECK", "ERROR", "request failed", new TimeoutException()).Result, Is.EqualTo("WARN"));
        Assert.That(CheckFailures.CreateFailureResult("CHECK", "ERROR", "request failed", new TaskCanceledException()).Result, Is.EqualTo("WARN"));
        Assert.That(CheckFailures.CreateFailureResult("CHECK", "ERROR", "request failed", new FormatException()).Result, Is.EqualTo("FAIL"));
    }

    [Test]
    public void radikoの必須欠落と大小文字を区別しないID重複と時刻逆転を検知する()
    {
        var start = DateTimeOffset.Parse("2026-10-03T09:00:00+09:00");
        var result = ProgramSchemaValidator.ValidateRadikoPrograms([
            new RadikoProgram { ProgramId = "id", StationId = "TBS", Title = "fixture", StartTime = start, EndTime = start.AddHours(1) },
            new RadikoProgram { ProgramId = "ID", StationId = "TBS", Title = "", StartTime = start, EndTime = start }
        ]);
        Assert.That(result.RequiredIssues.Select(issue => (issue.Field, issue.Reason)), Is.EqualTo(new[]
        {
            ("ProgramId", "duplicate"), ("Title", "missing"), ("Duration", "end_before_or_equal_start")
        }));
        Assert.That(result.OptionalMissingCounts["Performer"], Is.EqualTo(2));
    }

    [Test]
    public void らじるの任意項目欠落は必須スキーマ違反にしない()
    {
        var start = DateTimeOffset.Parse("2026-10-03T09:00:00+09:00");
        var result = ProgramSchemaValidator.ValidateRadiruPrograms([
            new RadiruProgramJsonEntity { Id = "fixture", Name = "fixture", StartDate = start, EndDate = start.AddHours(1) }
        ]);
        Assert.That(result.RequiredIssues, Is.Empty);
        Assert.That(result.OptionalMissingCounts["IdentifierGroup.AreaId"], Is.EqualTo(1));
        Assert.That(result.OptionalMissingCounts["About.PartOfSeries.Logo.Medium.Url"], Is.EqualTo(1));
    }

    [Test]
    public async Task statusのJSON形式を改修前のfixtureと一致させる()
    {
        var status = SampleStatus();
        var expected = await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/status.json"));
        Assert.That(CanaryReportWriter.SerializeStatus(status).Replace("\r\n", "\n"), Is.EqualTo(expected.TrimEnd()));
    }

    [Test]
    public async Task status保存に失敗した場合はログ配下へ同じ内容と診断を保存する()
    {
        var root = Path.Combine(Path.GetTempPath(), "canary-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blocker = Path.Combine(root, "file");
            await File.WriteAllTextAsync(blocker, "not a directory");
            var logDirectory = Path.Combine(root, "logs");
            await CanaryReportWriter.PersistStatusWithFallbackAsync(Path.Combine(blocker, "status.json"), SampleStatus(), logDirectory);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(logDirectory, "status.json")), Is.EqualTo(CanaryReportWriter.SerializeStatus(SampleStatus())));
            Assert.That(File.Exists(Path.Combine(logDirectory, "C998_STATUS_WRITE_FAILURE.log")), Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task 失敗した録音の新規ffmpegログだけをコピーする()
    {
        var root = Path.Combine(Path.GetTempPath(), "canary-ffmpeg-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "logs");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "old.log"), "previous");
            var snapshot = FfmpegLogs.CaptureFfmpegLogSnapshot(source);
            await File.WriteAllTextAsync(Path.Combine(source, "new.log"), "recording failure");
            var log = new StringBuilder();
            await FfmpegLogs.AppendNewFfmpegLogsAsync(log, snapshot, source, Path.Combine(destination, "CHECK.log"), "CHECK");
            Assert.That(Directory.GetFiles(destination).Select(Path.GetFileName), Is.EqualTo(new[] { "CHECK_ffmpeg_new.log" }));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(destination, "CHECK_ffmpeg_new.log")), Is.EqualTo("recording failure"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CanaryStatus SampleStatus() => new()
    {
        Result = "FAIL", Message = "One or more bootstrap checks failed.", TimestampJst = "2026-10-03T09:00:00.0000000+09:00",
        Checks = [new CheckResult("C001_RADIKO_DAILY_FETCH", "PASS", "Fetched 1 programs.", ""),
            new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "WARN", "timeout", "E-C004-RECORD-EXEC"),
            new CheckResult("C010_RADIKO_LOGIN", "FAIL", "RADIKO credentials are missing.", "E-C010-NO-CREDENTIALS")]
    };
}
