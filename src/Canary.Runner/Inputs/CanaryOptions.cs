namespace Canary.Runner;

/// <summary>
/// CLI入力を既存の既定値と秒数制限で読み取る。
/// </summary>
internal sealed class CanaryOptions
{
    internal required string StatusPath { get; init; }
    internal required string LogDirectory { get; init; }
    internal required string RecordOutputDirectory { get; init; }
    internal required string RadikoStationId { get; init; }
    internal required string RadiruAreaId { get; init; }
    internal required string RadiruStationId { get; init; }
    internal required string RadikoUserId { get; init; }
    internal required string RadikoPassword { get; init; }
    internal int RealtimeRecordSeconds { get; init; }
    internal int TimefreeRecordSeconds { get; init; }
    internal string StateInputDirectory { get; init; } = "state/input";
    internal string StateOutputDirectory { get; init; } = "state/output";

    internal static CanaryOptions Parse(string[] args)
    {
        var map = ParseArgs(args);
        return new CanaryOptions
        {
            StatusPath = GetArg(map, "status-json", "results/status.json"),
            LogDirectory = GetArg(map, "log-dir", "logs"),
            RecordOutputDirectory = GetArg(map, "record-output-dir", "artifacts/recordings"),
            StateInputDirectory = GetArg(map, "state-input-dir", "state/input"),
            StateOutputDirectory = GetArg(map, "state-output-dir", "state/output"),
            RadikoStationId = GetArg(map, "radiko-station-id", "TBS"),
            RadiruAreaId = GetArg(map, "radiru-area-id", "JP13"),
            RadiruStationId = GetArg(map, "radiru-station-id", "r1"),
            RadikoUserId = GetArg(map, "radiko-user-id", Environment.GetEnvironmentVariable("RADIKO_USER_ID") ?? string.Empty),
            RadikoPassword = GetArg(map, "radiko-password", Environment.GetEnvironmentVariable("RADIKO_PASSWORD") ?? string.Empty),
            RealtimeRecordSeconds = int.TryParse(GetArg(map, "realtime-record-seconds", "30"), out var realtimeSeconds)
                ? Math.Clamp(realtimeSeconds, 10, 180) : 30,
            TimefreeRecordSeconds = int.TryParse(GetArg(map, "timefree-record-seconds", "30"), out var timefreeSeconds)
                ? Math.Clamp(timefreeSeconds, 10, 120) : 30
        };
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = current[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            map[key] = value;
        }

        return map;
    }

    private static string GetArg(Dictionary<string, string> map, string key, string defaultValue)
        => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;
}
