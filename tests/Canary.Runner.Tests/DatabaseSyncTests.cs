using System.Net;
using System.Text;
using System.Text.Json;
using Canary.Runner.Hosting;
using Canary.Runner.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using RadiCorder.Logics.Application;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.Tests;

[TestFixture]
public class DatabaseSyncTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T12:00:00+09:00");
    private string root = null!;
    private CanaryOptions Options => CanaryOptions.Parse([
        "--radiko-station-id", "TOKYO", "--radiru-area-id", "130", "--radiru-station-id", "r1",
        "--log-dir", Path.Combine(root, "logs"), "--state-input-dir", Path.Combine(root, "input"),
        "--state-output-dir", Path.Combine(root, "output")]);

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "canary-sync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    [Test]
    public async Task 初回は増分確認を明示的にスキップし資格情報を含まないDBを作る()
    {
        var handler = new FixtureHandler(false);
        var results = await Run(handler);
        Assert.That(results.Select(result => result.Result), Is.EqualTo(new[] { "PASS", "SKIP" }));
        Assert.That(handler.Requests, Has.Count.EqualTo(5));
        var path = await CanaryStateStore.ReadBaselineAsync(Options.StateOutputDirectory, StateProfile.From(Options));
        await using var context = await LogicContext.CreateAsync("", "", databaseSnapshotPath: path, startProxy: false);
        Assert.That(context.DbContext.AppConfigurations.Any(), Is.False);
        Assert.That(context.DbContext.RadikoPrograms.Count(), Is.EqualTo(1));
        Assert.That(context.DbContext.NhkRadiruPrograms.Count(), Is.EqualTo(2));
        Assert.That(Directory.EnumerateFiles(Options.StateOutputDirectory).Select(Path.GetFileName),
            Is.EquivalentTo(new[] { "canary.db", "manifest.json" }));
        Assert.That(handler.Requests.All(uri => !uri.AbsolutePath.Contains("/member/") && !uri.AbsolutePath.Contains("auth")), Is.True);
    }

    [Test]
    public async Task 局の増減と属性更新と番組追加更新を同じ実応答で比較し前回DBを変更しない()
    {
        await Bootstrap();
        var originalHash = await CanaryStateStore.ComputeHashAsync(Path.Combine(Options.StateInputDirectory, "canary.db"));
        var handler = new FixtureHandler(true);
        var results = await Run(handler);
        Assert.That(results.Select(result => result.Result), Is.EqualTo(new[] { "PASS", "PASS" }));
        Assert.That(handler.Requests, Has.Count.EqualTo(5), "増分同期では新規取得と同じ応答を再利用する");
        Assert.That(await CanaryStateStore.ComputeHashAsync(Path.Combine(Options.StateInputDirectory, "canary.db")), Is.EqualTo(originalHash));
        var path = await CanaryStateStore.ReadBaselineAsync(Options.StateOutputDirectory, StateProfile.From(Options));
        await using var context = await LogicContext.CreateAsync("", "", databaseSnapshotPath: path, startProxy: false);
        Assert.That(context.DbContext.RadikoStations.Single(row => row.StationId == "REMOVED").IsActive, Is.False);
        Assert.That(context.DbContext.RadikoStations.Single(row => row.StationId == "NEW").IsActive, Is.True);
        Assert.That(context.DbContext.RadikoStations.Single(row => row.StationId == "TOKYO").StationName, Is.EqualTo("updated station"));
        Assert.That(context.DbContext.RadikoPrograms.Count(), Is.EqualTo(2));
        Assert.That(context.DbContext.RadikoPrograms.All(row => row.Title == "updated program"), Is.True);
        Assert.That(context.DbContext.NhkRadiruPrograms.All(row => row.Title == "updated program"), Is.True);
        Assert.That(context.DbContext.NhkRadiruAreaServices.Select(row => row.ServiceId), Is.EquivalentTo(new[] { "r1", "r2" }));
        var changes = await File.ReadAllTextAsync(Path.Combine(Options.LogDirectory, "C021_INCREMENTAL_DATABASE_SYNC_changes.json"));
        Assert.That(changes, Does.Contain("IsActive").And.Contain("Title"));
    }

    [Test]
    public async Task 同じ番組表の再取得で重複せず保存結果が一致する()
    {
        await Bootstrap();
        Assert.That((await Run(new FixtureHandler(false))).All(result => result.Result == "PASS"), Is.True);
        UseOutputAsBaseline();
        Assert.That((await Run(new FixtureHandler(false))).All(result => result.Result == "PASS"), Is.True);
        var path = await CanaryStateStore.ReadBaselineAsync(Options.StateOutputDirectory, StateProfile.From(Options));
        await using var context = await LogicContext.CreateAsync("", "", databaseSnapshotPath: path, startProxy: false);
        Assert.That(context.DbContext.RadikoPrograms.Count(), Is.EqualTo(1));
        Assert.That(context.DbContext.NhkRadiruPrograms.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task 最新応答にない過去の番組は保持し保持期限を過ぎた番組だけを削除する()
    {
        await Bootstrap();
        var path = await CanaryStateStore.ReadBaselineAsync(Options.StateInputDirectory, StateProfile.From(Options));
        await using (var context = await LogicContext.CreateAsync("", "", databaseSnapshotPath: path, startProxy: false))
        {
            foreach (var days in new[] { 10, 40 })
            {
                context.DbContext.RadikoPrograms.Add(new RadikoProgram { ProgramId = $"old-{days}", StationId = "TOKYO", Title = "old", RadioDate = DateOnly.FromDateTime(Now.AddDays(-days).Date), StartTime = Now.AddDays(-days), EndTime = Now.AddDays(-days).AddHours(1) });
                context.DbContext.NhkRadiruPrograms.Add(new NhkRadiruProgram { ProgramId = $"old-{days}", AreaId = "130", StationId = "r1", Title = "old", RadioDate = DateOnly.FromDateTime(Now.AddDays(-days).Date), StartTime = Now.AddDays(-days), EndTime = Now.AddDays(-days).AddHours(1) });
            }
            await context.DbContext.SaveChangesAsync();
            await CanaryStateStore.SaveCandidateAsync(context.DbContext, Options.StateInputDirectory, StateProfile.From(Options), Now);
        }
        var results = await Run(new FixtureHandler(false));
        Assert.That(results.All(result => result.Result == "PASS"), Is.True);
        var saved = await CanaryStateStore.ReadBaselineAsync(Options.StateOutputDirectory, StateProfile.From(Options));
        await using var restored = await LogicContext.CreateAsync("", "", databaseSnapshotPath: saved, startProxy: false);
        Assert.That(restored.DbContext.RadikoPrograms.Any(row => row.ProgramId == "old-10"), Is.True);
        Assert.That(restored.DbContext.RadikoPrograms.Any(row => row.ProgramId == "old-40"), Is.False);
        Assert.That(restored.DbContext.NhkRadiruPrograms.Any(row => row.ProgramId == "old-10"), Is.True);
        Assert.That(restored.DbContext.NhkRadiruPrograms.Any(row => row.ProgramId == "old-40"), Is.False);
    }

    [TestCase("station")]
    [TestCase("program")]
    public async Task 局無効化や既存番組更新が失われる不具合を検出し候補DBを作らない(string fault)
    {
        await Bootstrap();
        var results = await Run(new FixtureHandler(true), services =>
            services.AddDbContext<RadioDbContext>(options => options.AddInterceptors(new LostUpdateInterceptor(fault))));
        Assert.That(results.Single(result => result.CheckId == "C020_INITIAL_DATABASE_SYNC").Result, Is.EqualTo("PASS"));
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-MISMATCH"));
        Assert.That(File.Exists(Path.Combine(Options.StateOutputDirectory, "canary.db")), Is.False);
    }

    [Test]
    public async Task 新規DBでも一部の有効番組だけが保存されない不具合を検出する()
    {
        var results = await Run(new FixtureHandler(true), services =>
            services.AddDbContext<RadioDbContext>(options => options.AddInterceptors(new LostUpdateInterceptor("missing"))));
        Assert.That(results.Single(result => result.CheckId == "C020_INITIAL_DATABASE_SYNC").Result, Is.EqualTo("FAIL"));
        Assert.That(File.Exists(Path.Combine(Options.StateOutputDirectory, "canary.db")), Is.False);
    }

    [Test]
    public async Task らじるの消えたエリアにサービスが残存する現行の本体動作を検出する()
    {
        await Bootstrap(new FixtureHandler(false, extraArea: true));
        var results = await Run(new FixtureHandler(false));
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-MISMATCH"));
        var mismatches = await File.ReadAllTextAsync(Path.Combine(Options.LogDirectory, "C021_INCREMENTAL_DATABASE_SYNC_mismatches.json"));
        Assert.That(mismatches, Does.Contain("270:r1").And.Contain("unexpected"));
    }

    [TestCase("empty")]
    [TestCase("duplicate")]
    [TestCase("http")]
    public async Task 番組表の空応答や重複やHTTP失敗で初期同期の成功を偽装しない(string fault)
    {
        var results = await Run(new FixtureHandler(false, fault: fault));
        Assert.That(results.Single(result => result.CheckId == "C020_INITIAL_DATABASE_SYNC").Result, Is.EqualTo("FAIL"));
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").Result, Is.EqualTo("SKIP"));
        Assert.That(File.Exists(Path.Combine(Options.StateOutputDirectory, "canary.db")), Is.False);
    }

    [Test]
    public async Task 前回DBのチェックサム不一致は初回扱いにせず新規確認を継続する()
    {
        await Bootstrap();
        await File.AppendAllTextAsync(Path.Combine(Options.StateInputDirectory, "canary.db"), "corrupt");
        var results = await Run(new FixtureHandler(false));
        Assert.That(results.Single(result => result.CheckId == "C020_INITIAL_DATABASE_SYNC").Result, Is.EqualTo("PASS"));
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-BASELINE"));
        Assert.That(File.Exists(Path.Combine(Options.StateOutputDirectory, "canary.db")), Is.False);
    }

    [TestCase("scope")]
    [TestCase("wal")]
    public async Task 前回DBの対象変更やチェックサム対象外のWALを拒否する(string fault)
    {
        await Bootstrap();
        var manifestPath = Path.Combine(Options.StateInputDirectory, "manifest.json");
        if (fault == "scope")
        {
            var manifest = JsonSerializer.Deserialize<StateManifest>(await File.ReadAllTextAsync(manifestPath))!;
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with { Profile = new StateProfile("OTHER", "130", "r1") }));
        }
        else await File.WriteAllTextAsync(Path.Combine(Options.StateInputDirectory, "canary.db-wal"), "unverified content");
        var results = await Run(new FixtureHandler(false));
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-BASELINE"));
        Assert.That(File.Exists(Path.Combine(Options.StateOutputDirectory, "canary.db")), Is.False);
    }

    [Test]
    public async Task 現在の本体が認識しないmigrationを含む前回DBを拒否する()
    {
        await Bootstrap();
        var baseline = await CanaryStateStore.ReadBaselineAsync(Options.StateInputDirectory, StateProfile.From(Options));
        await using (var context = await LogicContext.CreateAsync("", "", databaseSnapshotPath: baseline, startProxy: false))
        {
            await context.DbContext.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('20990101000000_Unknown', '10.0.0')");
            await CanaryStateStore.SaveCandidateAsync(context.DbContext, Options.StateInputDirectory, StateProfile.From(Options), Now);
        }
        var hash = await CanaryStateStore.ComputeHashAsync(baseline!);
        var results = await Run(new FixtureHandler(false));
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-SYNC"));
        Assert.That(await CanaryStateStore.ComputeHashAsync(baseline!), Is.EqualTo(hash));
    }

    [Test]
    public async Task 候補DBの保存失敗を全体判定に含める()
    {
        await File.WriteAllTextAsync(Options.StateOutputDirectory, "not a directory");
        var results = await Run(new FixtureHandler(false));
        Assert.That(results.Single(result => result.CheckId == "C022_STATE_SNAPSHOT").Result, Is.EqualTo("FAIL"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task 入出力が同じ場所や末尾区切り文字付きの親子ディレクトリでも前回DBを削除しない(bool nested)
    {
        await Bootstrap();
        var hash = await CanaryStateStore.ComputeHashAsync(Path.Combine(Options.StateInputDirectory, "canary.db"));
        var options = CanaryOptions.Parse(["--radiko-station-id", "TOKYO", "--radiru-area-id", "130",
            "--log-dir", Options.LogDirectory, "--state-input-dir", Options.StateInputDirectory + Path.DirectorySeparatorChar,
            "--state-output-dir", nested ? Path.Combine(Options.StateInputDirectory, "child") : Options.StateInputDirectory]);
        var results = await Run(new FixtureHandler(false), options: options);
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-BASELINE"));
        Assert.That(await CanaryStateStore.ComputeHashAsync(Path.Combine(Options.StateInputDirectory, "canary.db")), Is.EqualTo(hash));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task 別名のシンボリックリンクでも前回DBやその子を出力先にしない(bool nested)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linuxでディレクトリのシンボリックリンクを検証する。");
        await Bootstrap();
        var hash = await CanaryStateStore.ComputeHashAsync(Path.Combine(Options.StateInputDirectory, "canary.db"));
        var alias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(alias, Options.StateInputDirectory);
        var options = CanaryOptions.Parse(["--radiko-station-id", "TOKYO", "--radiru-area-id", "130",
            "--log-dir", Options.LogDirectory, "--state-input-dir", Options.StateInputDirectory,
            "--state-output-dir", nested ? Path.Combine(alias, "child") : alias]);
        var results = await Run(new FixtureHandler(false), options: options);
        Assert.That(results.Single(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC").ErrorCode, Is.EqualTo("E-C021-BASELINE"));
        Assert.That(await CanaryStateStore.ComputeHashAsync(Path.Combine(Options.StateInputDirectory, "canary.db")), Is.EqualTo(hash));
    }

    [Test]
    public async Task 認証用DBを保存対象として受け付けない()
    {
        await using var context = await LogicContext.CreateAsync("offline-user", "offline-password", startProxy: false);
        Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CanaryStateStore.SaveCandidateAsync(context.DbContext, Options.StateOutputDirectory, StateProfile.From(Options), Now));
        Assert.That(File.Exists(Path.Combine(Options.StateOutputDirectory, "canary.db")), Is.False);
    }

    [Test]
    public async Task 古い局定義スキーマをコピー側だけでmigrationする()
    {
        var path = Path.Combine(root, "old.db");
        await using (var database = new RadioDbContext(new DbContextOptionsBuilder<RadioDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options))
        {
            await database.GetService<IMigrator>().MigrateAsync("20260309072051_DropLegacyNhkRadiruStations");
        }
        var hash = await CanaryStateStore.ComputeHashAsync(path);
        await using var context = await LogicContext.CreateAsync("", "", databaseSnapshotPath: path, startProxy: false);
        Assert.That(await context.DbContext.Database.GetPendingMigrationsAsync(), Is.Empty);
        Assert.That(await CanaryStateStore.ComputeHashAsync(path), Is.EqualTo(hash));
    }

    private async Task Bootstrap(FixtureHandler? handler = null)
    {
        Assert.That((await Run(handler ?? new FixtureHandler(false))).Select(result => result.Result), Is.EqualTo(new[] { "PASS", "SKIP" }));
        UseOutputAsBaseline();
    }

    private void UseOutputAsBaseline()
    {
        Directory.CreateDirectory(Options.StateInputDirectory);
        foreach (var file in Directory.EnumerateFiles(Options.StateOutputDirectory))
            File.Copy(file, Path.Combine(Options.StateInputDirectory, Path.GetFileName(file)), overwrite: true);
    }

    private Task<IReadOnlyList<CheckResult>> Run(FixtureHandler handler, Action<IServiceCollection>? configure = null, CanaryOptions? options = null) =>
        DatabaseSyncChecks.RunAsync(options ?? Options, services =>
        {
            services.AddHttpClient(HttpClientNames.Radiko).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient(HttpClientNames.Radiru).ConfigurePrimaryHttpMessageHandler(() => handler);
            configure?.Invoke(services);
        }, Now);

    private sealed class LostUpdateInterceptor(string fault) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries().Where(entry => entry.State is EntityState.Modified or EntityState.Added).ToList())
            {
                if (fault == "missing" && entry.Entity is RadikoProgram missing && missing.ProgramId.Contains("2026100410000020261004110000")) entry.State = EntityState.Detached;
                if (fault == "station" && entry.Entity is RadikoStation station && !station.IsActive) station.IsActive = true;
                if (fault == "program" && entry.State == EntityState.Modified && entry.Entity is RadikoProgram program) program.Title = "initial program";
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixtureHandler(bool updated, bool extraArea = false, string? fault = null) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var title = updated ? "updated program" : "initial program";
            if (uri.AbsolutePath.EndsWith("full.xml"))
                return Text($"<radiko><stations region_id='kanto' region_name='関東'><station><id>TOKYO</id><name>{(updated ? "updated station" : "initial station")}</name><area_id>JP13</area_id></station><station><id>{(updated ? "NEW" : "REMOVED")}</id><name>other</name><area_id>JP13</area_id></station></stations></radiko>", "application/xml");
            if (uri.AbsolutePath.EndsWith("config_web.xml"))
                return Text($"<radiru_config><url_program_day>https://fixture.invalid/{{area}}/{{service}}/[YYYY-MM-DD].json</url_program_day><stream_url><data><areakey>130</areakey><areajp>東京</areajp><r1hls>https://fixture.invalid/{(updated ? "new" : "old")}.m3u8</r1hls>{(updated ? "<r2hls>https://fixture.invalid/r2.m3u8</r2hls>" : "<fmhls>https://fixture.invalid/fm.m3u8</fmhls>")}</data>{(extraArea ? "<data><areakey>270</areakey><areajp>大阪</areajp><r1hls>https://fixture.invalid/osaka.m3u8</r1hls></data>" : "")}</stream_url></radiru_config>", "application/xml");
            if (uri.AbsolutePath.Contains("/weekly/"))
            {
                if (fault == "http") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                var program = $"<prog ft='20261004090000' to='20261004100000'><title>{title}</title><ts_in_ng>0</ts_in_ng></prog>";
                var extra = updated ? $"<prog ft='20261004100000' to='20261004110000'><title>{title}</title><ts_in_ng>0</ts_in_ng></prog>" : "";
                return Text($"<radiko>{(fault == "empty" ? "" : program + (fault == "duplicate" ? program : extra))}</radiko>", "application/xml");
            }
            var date = DateOnly.Parse(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
            var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(9));
            var programJson = new RadiruProgramJsonEntity { Id = $"fixture-{date:yyyyMMdd}", Name = title, StartDate = start, EndDate = start.AddHours(1) };
            return Text(JsonSerializer.Serialize(new { r1 = new { publication = new[] { programJson } } }), "application/json");
        }
        private static Task<HttpResponseMessage> Text(string value, string type) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, type)
        });
    }
}
