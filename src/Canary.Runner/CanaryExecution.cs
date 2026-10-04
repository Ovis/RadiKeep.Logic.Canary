using Canary.Runner.Hosting;

namespace Canary.Runner;

/// <summary>
/// チェックの実行順序と全体の結果を管理する。
/// </summary>
internal static class CanaryExecution
{
    internal static async Task<int> RunAsync(CanaryOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.StatusPath) ?? ".");
        Directory.CreateDirectory(options.LogDirectory);
        Directory.CreateDirectory(options.RecordOutputDirectory);

        var checks = new List<CheckResult>();
        var summary = new CanaryStatus
        {
            Result = "FAIL",
            Message = "Canary execution did not complete.",
            TimestampJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone()).ToString("O"),
            Checks = checks
        };
        var exitCode = 2;

        try
        {
            var ffmpegCheck = await FfmpegCheck.CheckFfmpegAsync(Path.Combine(options.LogDirectory, "C000_FFMPEG.log"));
            checks.Add(ffmpegCheck);

            checks.AddRange(await DatabaseSyncChecks.RunAsync(options));

            var todayJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone()).Date;
            await using var logicContext = await LogicContext.CreateAsync(
                options.RadikoUserId, options.RadikoPassword,
                radiruAreaId: options.RadiruAreaId, radiruStationId: options.RadiruStationId);
            var c006 = await RadikoStationDefinitionsCheck.CheckRadikoStationsFetchAsync(
                logicContext, Path.Combine(options.LogDirectory, "C006_RADIKO_STATIONS_FETCH.log"));
            checks.Add(c006);

            var c001 = await ProgramFetchChecks.CheckRadikoDailyFetchAsync(logicContext, options.RadikoStationId, todayJst, Path.Combine(options.LogDirectory, "C001_RADIKO_DAILY_FETCH.log"));
            checks.Add(c001);

            var c002 = await ProgramFetchChecks.CheckRadiruDailyFetchAsync(
                logicContext,
                options.RadiruAreaId,
                options.RadiruStationId,
                todayJst,
                Path.Combine(options.LogDirectory, "C002_RADIRU_DAILY_FETCH.log"));
            checks.Add(c002);

            var c010 = await RadikoLoginCheck.CheckRadikoLoginAsync(
                logicContext,
                options.RadikoUserId,
                options.RadikoPassword,
                Path.Combine(options.LogDirectory, "C010_RADIKO_LOGIN.log"));
            checks.Add(c010);

            var c003Radiko = await RadikoRecordingChecks.CheckRadikoRealtimeRecordingAsync(
                logicContext,
                options.RadikoStationId,
                options.RealtimeRecordSeconds,
                options.RecordOutputDirectory,
                Path.Combine(options.LogDirectory, "C003_RADIKO_REALTIME_RECORD.log"));
            checks.Add(c003Radiko);

            var c004RadikoTimefree = await RadikoRecordingChecks.CheckRadikoTimeFreeRecordingAsync(
                logicContext,
                options.RadikoStationId,
                options.TimefreeRecordSeconds,
                options.RecordOutputDirectory,
                Path.Combine(options.LogDirectory, "C004_RADIKO_TIMEFREE_RECORD.log"));
            checks.Add(c004RadikoTimefree);

            var c003Radiru = await RadiruRecordingChecks.CheckRadiruRealtimeRecordingAsync(
                logicContext,
                options.RadiruAreaId,
                options.RadiruStationId,
                options.RealtimeRecordSeconds,
                options.RecordOutputDirectory,
                Path.Combine(options.LogDirectory, "C003_RADIRU_REALTIME_RECORD.log"));
            checks.Add(c003Radiru);

            var c005RadiruOnDemand = await RadiruRecordingChecks.CheckRadiruOnDemandRecordingAsync(
                logicContext,
                options.RadiruAreaId,
                options.RadiruStationId,
                options.RecordOutputDirectory,
                Path.Combine(options.LogDirectory, "C005_RADIRU_ONDEMAND_RECORD.log"));
            checks.Add(c005RadiruOnDemand);

            var c011 = await RadikoLogoutCheck.CheckRadikoLogoutAsync(
                logicContext, Path.Combine(options.LogDirectory, "C011_RADIKO_LOGOUT.log"));
            checks.Add(c011);

            var overall = checks.Any(c => c.Result == "FAIL")
                ? "FAIL"
                : checks.Any(c => c.Result == "WARN")
                    ? "WARN"
                    : "PASS";
            summary = new CanaryStatus
            {
                Result = overall,
                Message = overall switch
                {
                    "PASS" => "Bootstrap checks passed.",
                    "WARN" => "Canary completed with warnings.",
                    _ => "One or more bootstrap checks failed."
                },
                TimestampJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone()).ToString("O"),
                Checks = checks
            };

            exitCode = overall switch
            {
                "PASS" => 0,
                "WARN" => 1,
                _ => 2
            };
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult("C999_UNHANDLED_EXCEPTION", "FAIL", $"Unhandled exception: {ex.Message}", "E-C999-UNHANDLED"));
            await CanaryReportWriter.TryWriteTextFileAsync(Path.Combine(options.LogDirectory, "C999_UNHANDLED_EXCEPTION.log"), ex.ToString());

            summary = new CanaryStatus
            {
                Result = "FAIL",
                Message = "Unhandled exception occurred.",
                TimestampJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone()).ToString("O"),
                Checks = checks
            };
        }

        await CanaryReportWriter.PersistStatusWithFallbackAsync(options.StatusPath, summary, options.LogDirectory);
        return exitCode;
    }
}
