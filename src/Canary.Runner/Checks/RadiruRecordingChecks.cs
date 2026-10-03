using Canary.Runner.Hosting;
using System.Text;
using RadiCorder.Logics.Domain.Recording;
using RadiCorder.Logics.Models.Enums;
using RadiCorder.Logics.Models.NhkRadiru;
using RadiCorder.Logics.Primitives;
using RadiCorder.Logics.Primitives.DataAnnotations;

namespace Canary.Runner;

/// <summary>
/// 本体の録音ソースと変換処理でらじる録音を確認する。
/// </summary>
internal static class RadiruRecordingChecks
{
    internal static async Task<CheckResult> CheckRadiruRealtimeRecordingAsync(
        LogicContext logicContext,
        string areaId,
        string stationId,
        int recordSeconds,
        string recordOutputDir,
        string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine($"check=C003_RADIRU area={areaId} station={stationId} seconds={recordSeconds}");
        HashSet<string>? ffmpegLogSnapshot = null;

        try
        {
            var normalizedArea = CanaryInputs.NormalizeRadiruAreaKey(areaId);
            if (!Enum.GetValues<RadiruAreaKind>().Any(x => x.GetEnumCodeId() == normalizedArea))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Radiru area kind not found.", "E-C003-RECORD-EXEC");
            }

            var stationKind = Enumeration.GetAll<RadiruStationKind>()
                .FirstOrDefault(x => string.Equals(x.ServiceId, stationId, StringComparison.OrdinalIgnoreCase));
            if (stationKind is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Radiru station kind not found.", "E-C003-RECORD-EXEC");
            }

            await logicContext.StationLobLogic.UpdateRadiruStationInformationAsync();
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone());
            var programs = await logicContext.RadiruApiClient.GetDailyProgramsAsync(normalizedArea, stationKind.ServiceId, now);
            var onAirProgram = programs.FirstOrDefault(p => now >= p.StartDate && now <= p.EndDate);

            if (onAirProgram is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "No on-air radiru program found.", "E-C003-NO-ONAIR");
            }

            var seededProgramId = await RecordingProbePrograms.SeedRadiruProgramForRealtimeRecordingAsync(logicContext, normalizedArea, stationKind, onAirProgram, recordSeconds);
            var command = new RecordingCommand(
                RadioServiceKind.Radiru,
                seededProgramId,
                onAirProgram.Name,
                IsTimeFree: false,
                StartDelaySeconds: 0,
                EndDelaySeconds: 0);
            var source = logicContext.GetRecordingSource(RadioServiceKind.Radiru);
            var sourceResult = await source.PrepareAsync(command);

            var outputPath = Path.Combine(recordOutputDir, $"radiru-realtime-{normalizedArea}-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
            var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
            ffmpegLogSnapshot = FfmpegLogs.CaptureFfmpegLogSnapshot(logicContext.FfmpegLogDirectory);
            var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

            log.AppendLine($"onair_program={onAirProgram.Name}");
            log.AppendLine($"seed_program_id={seededProgramId}");
            log.AppendLine($"output={outputPath}");
            log.AppendLine($"logic_recorded={recorded}");

            if (!recorded)
            {
                await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C003_RADIRU_REALTIME_RECORD");
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Radiru realtime recording failed.", "E-C003-RECORD-EXEC");
            }

            if (!File.Exists(outputPath))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Recorded file was not found.", "E-C003-OUTPUT-MISSING");
            }

            var bytes = new FileInfo(outputPath).Length;
            if (bytes < 32 * 1024)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C003-OUTPUT-TOO-SMALL");
            }

            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIRU_REALTIME_RECORD", "PASS", $"Radiru realtime recording succeeded. bytes={bytes}", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C003_RADIRU_REALTIME_RECORD");
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C003_RADIRU_REALTIME_RECORD", "E-C003-RECORD-EXEC", $"Radiru realtime check failed: {ex.Message}", ex);
        }
    }

    internal static async Task<CheckResult> CheckRadiruOnDemandRecordingAsync(
        LogicContext logicContext,
        string areaId,
        string stationId,
        string recordOutputDir,
        string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine($"check=C005_RADIRU_ONDEMAND area={areaId} station={stationId}");
        HashSet<string>? ffmpegLogSnapshot = null;

        try
        {
            var normalizedArea = CanaryInputs.NormalizeRadiruAreaKey(areaId);
            if (!Enum.GetValues<RadiruAreaKind>().Any(x => x.GetEnumCodeId() == normalizedArea))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Radiru area kind not found.", "E-C003-RECORD-EXEC");
            }

            var stationKind = Enumeration.GetAll<RadiruStationKind>()
                .FirstOrDefault(x => string.Equals(x.ServiceId, stationId, StringComparison.OrdinalIgnoreCase));
            if (stationKind is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C005_RADIRU_ONDEMAND_RECORD", "FAIL", "Radiru station kind not found.", "E-C005-RECORD-EXEC");
            }

            await logicContext.StationLobLogic.UpdateRadiruStationInformationAsync();

            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone());
            var selection = await ProgramCandidateSelector.FindRadiruOnDemandCandidateAsync(logicContext, normalizedArea, stationKind, now);
            if (selection.Candidate is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult(
                    "C005_RADIRU_ONDEMAND_RECORD",
                    "FAIL",
                    selection.HasExpiredCandidate ? "On-demand candidates exist but all are expired." : "No on-demand program candidate found.",
                    selection.HasExpiredCandidate ? "E-C005-EXPIRED" : "E-C005-NO-ONDEMAND-CANDIDATE");
            }

            var candidate = selection.Candidate;

            var seededProgramId = await RecordingProbePrograms.SeedRadiruProgramForOnDemandRecordingAsync(
                logicContext,
                normalizedArea,
                stationKind,
                candidate.Program,
                candidate.OnDemandUrl,
                candidate.ExpiresAtUtc);
            var command = new RecordingCommand(
                RadioServiceKind.Radiru,
                seededProgramId,
                candidate.Program.Name,
                IsTimeFree: false,
                StartDelaySeconds: 0,
                EndDelaySeconds: 0,
                IsOnDemand: true);
            var source = logicContext.GetRecordingSource(RadioServiceKind.Radiru);
            var sourceResult = await source.PrepareAsync(command);

            var outputPath = Path.Combine(recordOutputDir, $"radiru-ondemand-{normalizedArea}-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
            var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
            ffmpegLogSnapshot = FfmpegLogs.CaptureFfmpegLogSnapshot(logicContext.FfmpegLogDirectory);
            var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

            log.AppendLine($"ondemand_program={candidate.Program.Name}");
            log.AppendLine($"ondemand_program_start={candidate.Program.StartDate:O}");
            log.AppendLine($"ondemand_program_end={candidate.Program.EndDate:O}");
            log.AppendLine($"ondemand_expires_utc={candidate.ExpiresAtUtc:O}");
            log.AppendLine($"seed_program_id={seededProgramId}");
            log.AppendLine($"output={outputPath}");
            log.AppendLine($"logic_recorded={recorded}");

            if (!recorded)
            {
                await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C005_RADIRU_ONDEMAND_RECORD");
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C005_RADIRU_ONDEMAND_RECORD", "FAIL", "Radiru on-demand recording failed.", "E-C005-RECORD-EXEC");
            }

            if (!File.Exists(outputPath))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C005_RADIRU_ONDEMAND_RECORD", "FAIL", "Recorded file was not found.", "E-C005-OUTPUT-MISSING");
            }

            var bytes = new FileInfo(outputPath).Length;
            if (bytes < 32 * 1024)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C005_RADIRU_ONDEMAND_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C005-OUTPUT-TOO-SMALL");
            }

            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C005_RADIRU_ONDEMAND_RECORD", "PASS", $"Radiru on-demand recording succeeded. bytes={bytes}", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await FfmpegLogs.AppendNewFfmpegLogsAsync(log, ffmpegLogSnapshot, logicContext.FfmpegLogDirectory, logPath, "C005_RADIRU_ONDEMAND_RECORD");
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C005_RADIRU_ONDEMAND_RECORD", "E-C005-RECORD-EXEC", $"Radiru on-demand check failed: {ex.Message}", ex);
        }
    }
}
