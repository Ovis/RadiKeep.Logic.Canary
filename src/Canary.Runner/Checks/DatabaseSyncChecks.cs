using System.Text;
using Canary.Runner.Hosting;
using Canary.Runner.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RadiCorder.Logics.ApiClients;
using RadiCorder.Logics.Application;
using RadiCorder.Logics.Context;
using RadiCorder.Logics.Extensions;
using RadiCorder.Logics.Interfaces;

namespace Canary.Runner;

/// <summary>
/// 同じ実サービス応答を空DBと前回DBに反映し、本体の局・番組表同期を確認する。
/// </summary>
internal static class DatabaseSyncChecks
{
    internal static async Task<IReadOnlyList<CheckResult>> RunAsync(CanaryOptions options,
        Action<IServiceCollection>? configureServices = null, DateTimeOffset? now = null)
    {
        var results = new List<CheckResult>();
        var profile = StateProfile.From(options);
        var capturedAt = now ?? DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(capturedAt, CanaryInputs.ResolveJapanTimeZone()).DateTime);
        var cutoff = capturedAt.AddMonths(-1).ToRadioDate();
        var responses = new SyncHttpResponses();
        var inputs = new SyncExpectedInputs(cutoff);
        var initialLog = new StringBuilder("check=C020\n");
        var incrementalLog = new StringBuilder("check=C021\n");
        string? baseline = null;
        Exception? baselineError = null;
        SyncDatabaseSnapshot? current = null;

        try
        {
            var input = Path.GetFullPath(options.StateInputDirectory);
            var output = Path.GetFullPath(options.StateOutputDirectory);
            if (input == output || input.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("状態の入力と出力には別のディレクトリを指定してください。");
            ClearCandidate(options.StateOutputDirectory);
            baseline = await CanaryStateStore.ReadBaselineAsync(options.StateInputDirectory, profile);
        }
        catch (Exception ex) { baselineError = ex; }

        Task<LogicContext> CreateContext(bool capture, string? snapshot = null) => LogicContext.CreateAsync("", "", services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IRadioAppContext>(new RadioAppContext(capturedAt)));
            services.AddSingleton(new RadiruProbeTarget(profile.RadiruAreaId, profile.RadiruStationId, today));
            services.AddScoped<RadikoApiClient>();
            services.Replace(ServiceDescriptor.Scoped<IRadikoApiClient>(provider => new SyncRadikoApiClient(provider.GetRequiredService<RadikoApiClient>(), profile, inputs)));
            services.Replace(ServiceDescriptor.Scoped<IRadiruApiClient>(provider => new SyncRadiruApiClient(
                new CanaryRadiruApiClient(provider.GetRequiredService<RadiruApiClient>(), provider.GetRequiredService<RadiruProbeTarget>()), inputs)));
            configureServices?.Invoke(services);
            services.AddHttpClient(HttpClientNames.Radiko).AddHttpMessageHandler(() => responses.CreateHandler(capture));
            services.AddHttpClient(HttpClientNames.Radiru).AddHttpMessageHandler(() => responses.CreateHandler(capture));
        }, profile.RadiruAreaId, profile.RadiruStationId, snapshot, startProxy: false);

        try
        {
            await using var fresh = await CreateContext(capture: true);
            await SynchronizeAsync(fresh, profile);
            current = await SyncDatabaseSnapshot.ReadAsync(fresh.DbContext);
            current.ValidateInitial();
            inputs.VerifyInitial(current);
            foreach (var (table, rows) in current.Tables) initialLog.AppendLine($"{table}_count={rows.Count}");
            results.Add(new("C020_INITIAL_DATABASE_SYNC", "PASS", "Initial station and program synchronization succeeded.", ""));
            if (baseline is null && baselineError is null)
                await SaveCandidateAsync(fresh, options, profile, capturedAt, results);
        }
        catch (Exception ex)
        {
            current = null;
            initialLog.AppendLine(ex.ToString());
            results.RemoveAll(result => result.CheckId == "C020_INITIAL_DATABASE_SYNC");
            results.Add(CheckFailures.CreateFailureResult("C020_INITIAL_DATABASE_SYNC", "E-C020-SYNC", "Initial database synchronization failed.", ex));
        }
        await File.WriteAllTextAsync(Path.Combine(options.LogDirectory, "C020_INITIAL_DATABASE_SYNC.log"), initialLog.ToString());

        if (baselineError is not null)
        {
            incrementalLog.AppendLine(baselineError.ToString());
            results.Add(new("C021_INCREMENTAL_DATABASE_SYNC", "FAIL", "Stored baseline could not be validated; it was not reset.", "E-C021-BASELINE"));
        }
        else if (current is null)
        {
            incrementalLog.AppendLine("skipped=initial_sync_failed");
            results.Add(new("C021_INCREMENTAL_DATABASE_SYNC", "SKIP", "Incremental check requires successful initial synchronization.", ""));
        }
        else if (baseline is null)
        {
            incrementalLog.AppendLine("skipped=no_previous_baseline bootstrap=true");
            results.Add(new("C021_INCREMENTAL_DATABASE_SYNC", "SKIP", "First run: no previous database exists; baseline initialization is required.", ""));
        }
        else
        {
            try
            {
                await using var incremental = await CreateContext(capture: false, baseline);
                var previous = await SyncDatabaseSnapshot.ReadAsync(incremental.DbContext);
                await SynchronizeAsync(incremental, profile);
                var actual = await SyncDatabaseSnapshot.ReadAsync(incremental.DbContext);
                var expected = SyncDatabaseSnapshot.ExpectedAfter(previous, current, cutoff);
                var differences = expected.DifferencesFrom(actual);
                var changes = previous.DifferencesFrom(actual);
                await CanaryReportWriter.WriteJsonLogAsync(Path.Combine(options.LogDirectory, "C021_INCREMENTAL_DATABASE_SYNC_changes.json"), changes);
                await CanaryReportWriter.WriteJsonLogAsync(Path.Combine(options.LogDirectory, "C021_INCREMENTAL_DATABASE_SYNC_mismatches.json"), differences);
                incrementalLog.AppendLine($"change_count={changes.Count} mismatch_count={differences.Count} retention_cutoff={cutoff:yyyy-MM-dd}");
                if (differences.Count > 0)
                    results.Add(new("C021_INCREMENTAL_DATABASE_SYNC", "FAIL", $"Incremental database differs from expected synchronization: {differences.Count} rows.", "E-C021-MISMATCH"));
                else
                {
                    results.Add(new("C021_INCREMENTAL_DATABASE_SYNC", "PASS", "Incremental synchronization matches current data and retention rules.", ""));
                    await SaveCandidateAsync(incremental, options, profile, capturedAt, results);
                }
            }
            catch (Exception ex)
            {
                incrementalLog.AppendLine(ex.ToString());
                results.RemoveAll(result => result.CheckId == "C021_INCREMENTAL_DATABASE_SYNC");
                results.Add(CheckFailures.CreateFailureResult("C021_INCREMENTAL_DATABASE_SYNC", "E-C021-SYNC", "Incremental database synchronization failed.", ex));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(options.LogDirectory, "C021_INCREMENTAL_DATABASE_SYNC.log"), incrementalLog.ToString());
        return results;
    }

    private static async Task SynchronizeAsync(LogicContext context, StateProfile profile)
    {
        await context.StationLobLogic.UpsertRadikoStationDefinitionAsync();
        await context.StationLobLogic.UpdateRadiruStationInformationAsync();
        if (!await context.DbContext.NhkRadiruAreaServices.AsNoTracking().AnyAsync(row =>
            row.AreaId == profile.RadiruAreaId && row.ServiceId == profile.RadiruStationId && row.IsActive))
            throw new InvalidDataException("番組表の確認対象サービスが局定義にありません。");
        await context.ProgramScheduleLobLogic.UpdateLatestRadikoProgramDataAsync();
        await context.ProgramScheduleLobLogic.UpdateRadiruProgramDataAsync();
        await context.ProgramScheduleLobLogic.DeleteOldRadikoProgramAsync();
        await context.ProgramScheduleLobLogic.DeleteOldRadiruProgramAsync();
    }

    private static async Task SaveCandidateAsync(LogicContext context, CanaryOptions options, StateProfile profile,
        DateTimeOffset capturedAt, List<CheckResult> results)
    {
        try { await CanaryStateStore.SaveCandidateAsync(context.DbContext, options.StateOutputDirectory, profile, capturedAt); }
        catch (Exception ex)
        {
            try { ClearCandidate(options.StateOutputDirectory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await File.WriteAllTextAsync(Path.Combine(options.LogDirectory, "C022_STATE_SNAPSHOT.log"), ex.ToString());
            results.Add(new("C022_STATE_SNAPSHOT", "FAIL", "Database snapshot could not be saved.", "E-C022-SNAPSHOT"));
        }
    }

    private static void ClearCandidate(string directory)
    {
        if (!Directory.Exists(directory)) return;
        File.Delete(Path.Combine(directory, CanaryStateStore.DatabaseFileName));
        File.Delete(Path.Combine(directory, CanaryStateStore.ManifestFileName));
        File.Delete(Path.Combine(directory, CanaryStateStore.DatabaseFileName + ".tmp"));
    }
}
