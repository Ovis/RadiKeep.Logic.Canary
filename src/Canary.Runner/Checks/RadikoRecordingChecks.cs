using Canary.Runner.Hosting;
using System.Text;
using RadiCorder.Logics.Domain.Recording;
using RadiCorder.Logics.Models.Enums;

namespace Canary.Runner;

/// <summary>
/// 本体の録音ソースと変換処理でradiko録音を確認する。
/// </summary>
internal static class RadikoRecordingChecks
{
    internal static async Task<CheckResult> CheckRadikoRealtimeRecordingAsync(
        LogicContext logicContext,
        string preferredStationId,
        int recordSeconds,
        string recordOutputDir,
        string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine($"check=C003_RADIKO preferred_station={preferredStationId} seconds={recordSeconds}");
        HashSet<string>? ffmpegLogSnapshot = null;

        try
        {
            var login = await logicContext.RadikoLogic.LoginRadikoAsync(forceRefresh: true);
            if (!login.IsSuccess || string.IsNullOrWhiteSpace(login.Session))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "radiko login failed.", "E-C003-RECORD-EXEC");
            }

            var areaResult = await logicContext.RadikoLogic.GetRadikoAreaAsync(forceRefresh: true);
            if (!areaResult.IsSuccess || string.IsNullOrWhiteSpace(areaResult.Area))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "radiko area detection failed.", "E-C003-RECORD-EXEC");
            }
            var area = areaResult.Area;

            var currentAreaStations = await logicContext.RadikoApiClient.GetStationsByAreaAsync(area);
            if (currentAreaStations.Count == 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "No stations resolved for current area.", "E-C003-RECORD-EXEC");
            }

            var stationId = preferredStationId;
            if (!login.IsAreaFree && !currentAreaStations.Contains(preferredStationId, StringComparer.OrdinalIgnoreCase))
            {
                stationId = currentAreaStations[0];
            }

            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone());
            var onAirProgram = await ProgramCandidateSelector.FindRadikoNowOnAirProgramAsync(logicContext, stationId, now);
            if (onAirProgram is null && !login.IsAreaFree)
            {
                foreach (var candidateStation in currentAreaStations.Take(5))
                {
                    onAirProgram = await ProgramCandidateSelector.FindRadikoNowOnAirProgramAsync(logicContext, candidateStation, now);
                    if (onAirProgram is not null)
                    {
                        stationId = candidateStation;
                        break;
                    }
                }
            }

            if (onAirProgram is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "No on-air program found for realtime record.", "E-C003-NO-ONAIR");
            }

            var seededProgramId = await RecordingProbePrograms.SeedRadikoProgramForRealtimeRecordingAsync(logicContext, stationId, onAirProgram.Value.Title, area, recordSeconds);
            var command = new RecordingCommand(
                RadioServiceKind.Radiko,
                seededProgramId,
                onAirProgram.Value.Title,
                IsTimeFree: false,
                StartDelaySeconds: 0,
                EndDelaySeconds: 0);
            var source = logicContext.GetRecordingSource(RadioServiceKind.Radiko);
            var sourceResult = await source.PrepareAsync(command);

            var outputPath = Path.Combine(recordOutputDir, $"radiko-realtime-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
            var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
            ffmpegLogSnapshot = FfmpegLogs.CaptureFfmpegLogSnapshot(logicContext.FfmpegLogDirectory);
            var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

            log.AppendLine($"selected_station={stationId}");
            log.AppendLine($"onair_program={onAirProgram.Value.Title}");
            log.AppendLine($"seed_program_id={seededProgramId}");
            log.AppendLine($"output={outputPath}");
            log.AppendLine($"logic_recorded={recorded}");

            if (!recorded)
            {
                await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C003_RADIKO_REALTIME_RECORD");
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "radiko realtime recording failed.", "E-C003-RECORD-EXEC");
            }

            if (!File.Exists(outputPath))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "Recorded file was not found.", "E-C003-OUTPUT-MISSING");
            }

            var bytes = new FileInfo(outputPath).Length;
            if (bytes < 32 * 1024)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C003-OUTPUT-TOO-SMALL");
            }

            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "PASS", $"radiko realtime recording succeeded. bytes={bytes}", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C003_RADIKO_REALTIME_RECORD");
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C003_RADIKO_REALTIME_RECORD", "E-C003-RECORD-EXEC", $"radiko realtime check failed: {ex.Message}", ex);
        }
    }

    internal static async Task<CheckResult> CheckRadikoTimeFreeRecordingAsync(
        LogicContext logicContext,
        string preferredStationId,
        int recordSeconds,
        string recordOutputDir,
        string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine($"check=C004_RADIKO_TIMEFREE preferred_station={preferredStationId} seconds={recordSeconds}");
        HashSet<string>? ffmpegLogSnapshot = null;

        try
        {
            var login = await logicContext.RadikoLogic.LoginRadikoAsync(forceRefresh: true);
            if (!login.IsSuccess || string.IsNullOrWhiteSpace(login.Session))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "radiko login failed.", "E-C004-AUTH");
            }

            var areaResult = await logicContext.RadikoLogic.GetRadikoAreaAsync(forceRefresh: true);
            if (!areaResult.IsSuccess || string.IsNullOrWhiteSpace(areaResult.Area))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "radiko area detection failed.", "E-C004-AUTH");
            }
            var area = areaResult.Area;

            var currentAreaStations = await logicContext.RadikoApiClient.GetStationsByAreaAsync(area);
            if (currentAreaStations.Count == 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "No stations resolved for current area.", "E-C004-AUTH");
            }

            var stationId = preferredStationId;
            if (!login.IsAreaFree && !currentAreaStations.Contains(preferredStationId, StringComparer.OrdinalIgnoreCase))
            {
                stationId = currentAreaStations[0];
            }

            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone());
            var candidate = await ProgramCandidateSelector.FindRadikoTimeFreeCandidateAsync(logicContext, stationId, now, recordSeconds);
            if (candidate is null && !login.IsAreaFree)
            {
                foreach (var candidateStation in currentAreaStations.Take(5))
                {
                    candidate = await ProgramCandidateSelector.FindRadikoTimeFreeCandidateAsync(logicContext, candidateStation, now, recordSeconds);
                    if (candidate is not null)
                    {
                        stationId = candidateStation;
                        break;
                    }
                }
            }

            if (candidate is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "No timefree candidate program found.", "E-C004-NO-TIMEFREE-CANDIDATE");
            }

            var seededProgramId = await RecordingProbePrograms.SeedRadikoProgramForTimeFreeRecordingAsync(
                logicContext,
                stationId,
                candidate.Value.Title,
                area,
                candidate.Value.StartTime,
                candidate.Value.EndTime,
                recordSeconds);
            var command = new RecordingCommand(
                RadioServiceKind.Radiko,
                seededProgramId,
                candidate.Value.Title,
                IsTimeFree: true,
                StartDelaySeconds: 0,
                EndDelaySeconds: 0);
            var source = logicContext.GetRecordingSource(RadioServiceKind.Radiko);
            var sourceResult = await source.PrepareAsync(command);

            var outputPath = Path.Combine(recordOutputDir, $"radiko-timefree-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
            var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
            ffmpegLogSnapshot = FfmpegLogs.CaptureFfmpegLogSnapshot(logicContext.FfmpegLogDirectory);
            var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

            log.AppendLine($"selected_station={stationId}");
            log.AppendLine($"timefree_program={candidate.Value.Title}");
            log.AppendLine($"timefree_program_start={candidate.Value.StartTime:O}");
            log.AppendLine($"timefree_program_end={candidate.Value.EndTime:O}");
            log.AppendLine($"seed_program_id={seededProgramId}");
            log.AppendLine($"output={outputPath}");
            log.AppendLine($"logic_recorded={recorded}");

            if (!recorded)
            {
                await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C004_RADIKO_TIMEFREE_RECORD");
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "radiko timefree recording failed.", "E-C004-RECORD-EXEC");
            }

            if (!File.Exists(outputPath))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "Recorded file was not found.", "E-C004-OUTPUT-MISSING");
            }

            var bytes = new FileInfo(outputPath).Length;
            if (bytes < 32 * 1024)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C004-OUTPUT-TOO-SMALL");
            }

            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "PASS", $"radiko timefree recording succeeded. bytes={bytes}", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C004_RADIKO_TIMEFREE_RECORD");
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C004_RADIKO_TIMEFREE_RECORD", "E-C004-RECORD-EXEC", $"radiko timefree check failed: {ex.Message}", ex);
        }
    }
}
