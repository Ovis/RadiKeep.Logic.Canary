using System.Diagnostics;
using System.Text.Json;

var argsMap = ParseArgs(args);

var statusPath = GetArg(argsMap, "status-json", "results/status.json");
var logDir = GetArg(argsMap, "log-dir", "logs");
var recordOutputDir = GetArg(argsMap, "record-output-dir", "artifacts/recordings");

Directory.CreateDirectory(Path.GetDirectoryName(statusPath) ?? ".");
Directory.CreateDirectory(logDir);
Directory.CreateDirectory(recordOutputDir);

var checks = new List<CheckResult>();

try
{
    var ffmpegCheck = await CheckFfmpegAsync(Path.Combine(logDir, "C000_FFMPEG.log"));
    checks.Add(ffmpegCheck);

    var overall = checks.All(c => c.Result == "PASS") ? "PASS" : "FAIL";
    var summary = new CanaryStatus
    {
        Result = overall,
        Message = overall == "PASS" ? "Bootstrap checks passed." : "One or more bootstrap checks failed.",
        TimestampJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone()).ToString("O"),
        Checks = checks
    };

    await WriteStatusAsync(statusPath, summary);
    return overall == "PASS" ? 0 : 2;
}
catch (Exception ex)
{
    checks.Add(new CheckResult("C999_UNHANDLED_EXCEPTION", "FAIL", $"Unhandled exception: {ex.Message}", "E-C999-UNHANDLED"));

    var summary = new CanaryStatus
    {
        Result = "FAIL",
        Message = "Unhandled exception occurred.",
        TimestampJst = DateTimeOffset.UtcNow.ToString("O"),
        Checks = checks
    };

    await WriteStatusAsync(statusPath, summary);
    return 2;
}

static TimeZoneInfo ResolveJapanTimeZone()
{
    var candidates = new[] { "Asia/Tokyo", "Tokyo Standard Time" };
    foreach (var id in candidates)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            // try next
        }
    }

    return TimeZoneInfo.Utc;
}

static async Task<CheckResult> CheckFfmpegAsync(string logPath)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            await File.WriteAllTextAsync(logPath, "ffmpeg process could not be started.");
            return new CheckResult("C000_FFMPEG", "FAIL", "ffmpeg process start failed.", "E-C000-START");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        await File.WriteAllTextAsync(logPath, stdout + Environment.NewLine + stderr);

        return process.ExitCode == 0
            ? new CheckResult("C000_FFMPEG", "PASS", "ffmpeg is available.", string.Empty)
            : new CheckResult("C000_FFMPEG", "FAIL", "ffmpeg returned non-zero exit code.", "E-C000-EXIT");
    }
    catch (Exception ex)
    {
        await File.WriteAllTextAsync(logPath, ex.ToString());
        return new CheckResult("C000_FFMPEG", "FAIL", $"ffmpeg check failed: {ex.Message}", "E-C000-EXCEPTION");
    }
}

static async Task WriteStatusAsync(string path, CanaryStatus status)
{
    var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(path, json);
}

static Dictionary<string, string> ParseArgs(string[] args)
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

static string GetArg(Dictionary<string, string> map, string key, string defaultValue)
    => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;

file sealed class CanaryStatus
{
    public required string Result { get; init; }
    public required string Message { get; init; }
    public required string TimestampJst { get; init; }
    public required List<CheckResult> Checks { get; init; }
}

file sealed record CheckResult(string CheckId, string Result, string Message, string ErrorCode);
