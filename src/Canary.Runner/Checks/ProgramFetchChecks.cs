using Canary.Runner.Hosting;
using System.Text;
using RadiCorder.Logics.Models.Enums;
using RadiCorder.Logics.Models.NhkRadiru;
using RadiCorder.Logics.Primitives;
using RadiCorder.Logics.Primitives.DataAnnotations;

namespace Canary.Runner;

/// <summary>
/// 実サービスの番組表を取得し、スキーマを確認する。
/// </summary>
internal static class ProgramFetchChecks
{
    internal static async Task<CheckResult> CheckRadikoDailyFetchAsync(LogicContext logicContext, string stationId, DateTime dateJst, string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine($"check=C001 station={stationId} date={dateJst:yyyy-MM-dd}");

        try
        {
            var programs = (await logicContext.RadikoApiClient.GetWeeklyProgramsAsync(stationId))
                .Where(p =>
                {
                    var jstStart = TimeZoneInfo.ConvertTime(p.StartTime, CanaryInputs.ResolveJapanTimeZone());
                    return jstStart.Date == dateJst.Date;
                })
                .ToList();
            var programDataPath = CanaryReportWriter.GetProgramDataLogPath(logPath);
            await CanaryReportWriter.WriteJsonLogAsync(programDataPath, programs);

            log.AppendLine($"program_count={programs.Count}");
            log.AppendLine($"program_data_log={programDataPath}");
            if (programs.Count == 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", "Program list is empty.", "E-C001-EMPTY");
            }

            var validation = ProgramSchemaValidator.ValidateRadikoPrograms(programs);
            ProgramSchemaValidator.AppendValidationLog(log, validation, programs.Count);

            if (validation.RequiredIssues.Count > 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", $"Invalid programs found: {validation.RequiredIssues.Count}", "E-C001-SCHEMA");
            }

            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C001_RADIKO_DAILY_FETCH", "PASS", $"Fetched {programs.Count} programs.", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C001_RADIKO_DAILY_FETCH", "E-C001-NETWORK", $"Failed to fetch radiko daily programs: {ex.Message}", ex);
        }
    }

    internal static async Task<CheckResult> CheckRadiruDailyFetchAsync(
        LogicContext logicContext,
        string areaId,
        string stationId,
        DateTime dateJst,
        string logPath)
    {
        var log = new StringBuilder();
        var normalizedAreaKey = CanaryInputs.NormalizeRadiruAreaKey(areaId);
        log.AppendLine($"check=C002 area={areaId} normalized_area={normalizedAreaKey} station={stationId} date={dateJst:yyyy-MM-dd}");

        try
        {
            if (!Enum.GetValues<RadiruAreaKind>().Any(x => x.GetEnumCodeId() == normalizedAreaKey))
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "Unknown radiru area.", "E-C002-SCHEMA");
            }

            var stationKind = Enumeration.GetAll<RadiruStationKind>()
                .FirstOrDefault(x => string.Equals(x.ServiceId, stationId, StringComparison.OrdinalIgnoreCase));
            if (stationKind is null)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "Unknown radiru station.", "E-C002-SCHEMA");
            }

            await logicContext.StationLobLogic.UpdateRadiruStationInformationAsync();

            var targetDate = new DateTimeOffset(dateJst, CanaryInputs.ResolveJapanTimeZone().GetUtcOffset(dateJst));
            var publications = await logicContext.RadiruApiClient.GetDailyProgramsAsync(normalizedAreaKey, stationKind.ServiceId, targetDate);
            var programDataPath = CanaryReportWriter.GetProgramDataLogPath(logPath);
            await CanaryReportWriter.WriteJsonLogAsync(programDataPath, publications);

            log.AppendLine($"program_count={publications.Count}");
            log.AppendLine($"program_data_log={programDataPath}");
            if (publications.Count == 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "Program list is empty.", "E-C002-EMPTY");
            }

            var validation = ProgramSchemaValidator.ValidateRadiruPrograms(publications);
            ProgramSchemaValidator.AppendValidationLog(log, validation, publications.Count);

            if (validation.RequiredIssues.Count > 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", $"Invalid programs found: {validation.RequiredIssues.Count}", "E-C002-SCHEMA");
            }

            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "PASS", $"Fetched {publications.Count} programs.", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C002_RADIRU_DAILY_FETCH", "E-C002-NETWORK", $"Failed to fetch radiru daily programs: {ex.Message}", ex);
        }
    }
}
