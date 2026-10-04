using System.Text;
using Canary.Runner.Hosting;

namespace Canary.Runner;

/// <summary>
/// 本体のAPIクライアントでradiko全国局定義を取得し、必須項目を確認する。
/// </summary>
internal static class RadikoStationDefinitionsCheck
{
    internal static async Task<CheckResult> CheckRadikoStationsFetchAsync(LogicContext logicContext, string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine("check=C006");

        try
        {
            var stations = await logicContext.RadikoApiClient.GetRadikoStationsAsync();
            var dataPath = Path.Combine(Path.GetDirectoryName(logPath) ?? ".", Path.GetFileNameWithoutExtension(logPath) + "_stations.json");
            await CanaryReportWriter.WriteJsonLogAsync(dataPath, stations);
            log.AppendLine($"station_count={stations.Count}");
            log.AppendLine($"station_data_log={dataPath}");

            if (stations.Count == 0)
            {
                await File.WriteAllTextAsync(logPath, log.ToString());
                return new CheckResult("C006_RADIKO_STATIONS_FETCH", "FAIL", "Station list is empty.", "E-C006-EMPTY");
            }

            var invalidCount = 0;
            for (var index = 0; index < stations.Count; index++)
            {
                var station = stations[index];
                var fields = new[]
                {
                    (nameof(station.StationId), station.StationId),
                    (nameof(station.StationName), station.StationName),
                    (nameof(station.RegionId), station.RegionId),
                    (nameof(station.RegionName), station.RegionName),
                    (nameof(station.Area), station.Area)
                };
                var missing = fields.Where(field => string.IsNullOrWhiteSpace(field.Item2)).Select(field => field.Item1).ToList();
                if (missing.Count > 0)
                {
                    invalidCount++;
                    log.AppendLine($"station_index={index} missing_fields={string.Join(',', missing)}");
                }
            }

            // 本体のrepositoryは局IDの重複を集約するため、重複だけで異常とは判定しない。
            log.AppendLine($"unique_station_count={stations.Select(station => station.StationId).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            log.AppendLine($"region_count={stations.Select(station => station.RegionId).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            log.AppendLine($"invalid_station_count={invalidCount}");
            await File.WriteAllTextAsync(logPath, log.ToString());
            return invalidCount > 0
                ? new CheckResult("C006_RADIKO_STATIONS_FETCH", "FAIL", $"Invalid stations found: {invalidCount}", "E-C006-SCHEMA")
                : new CheckResult("C006_RADIKO_STATIONS_FETCH", "PASS", $"Fetched {stations.Count} stations.", string.Empty);
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            await File.WriteAllTextAsync(logPath, log.ToString());
            return CheckFailures.CreateFailureResult("C006_RADIKO_STATIONS_FETCH", "E-C006-FETCH", $"Failed to fetch radiko station definitions: {ex.Message}", ex);
        }
    }
}
