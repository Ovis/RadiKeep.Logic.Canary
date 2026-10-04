using System.Text;
using System.Text.Json;

namespace Canary.Runner;

/// <summary>
/// 既存形式のstatus.jsonと番組表の証跡を保存する。
/// </summary>
internal static class CanaryReportWriter
{
    internal static string GetProgramDataLogPath(string logPath)
    {
        var logDirectory = Path.GetDirectoryName(logPath) ?? ".";
        var logFileNameWithoutExtension = Path.GetFileNameWithoutExtension(logPath);
        return Path.Combine(logDirectory, $"{logFileNameWithoutExtension}_programs.json");
    }

    internal static async Task WriteJsonLogAsync<T>(string path, T payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    internal static async Task WriteStatusAsync(string path, CanaryStatus status)
    {
        var json = SerializeStatus(status);
        await File.WriteAllTextAsync(path, json);
    }

    internal static async Task PersistStatusWithFallbackAsync(string primaryPath, CanaryStatus status, string logDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath) ?? ".");
            await WriteStatusAsync(primaryPath, status);
        }
        catch (Exception ex)
        {
            var fallbackDir = string.IsNullOrWhiteSpace(logDir) ? "." : logDir;
            Directory.CreateDirectory(fallbackDir);

            var fallbackStatusPath = Path.Combine(fallbackDir, Path.GetFileName(primaryPath));
            var fallbackLogPath = Path.Combine(fallbackDir, "C998_STATUS_WRITE_FAILURE.log");
            var payload = SerializeStatus(status);

            var diagnostic = new StringBuilder();
            diagnostic.AppendLine($"primary_status_path={primaryPath}");
            diagnostic.AppendLine($"fallback_status_path={fallbackStatusPath}");
            diagnostic.AppendLine(ex.ToString());

            await TryWriteTextFileAsync(fallbackLogPath, diagnostic.ToString());
            await TryWriteTextFileAsync(fallbackStatusPath, payload);
            Console.Error.WriteLine($"Failed to write status file to '{primaryPath}'. Fallback written to '{fallbackStatusPath}'.");
        }
    }

    internal static async Task TryWriteTextFileAsync(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await File.WriteAllTextAsync(path, content);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write file '{path}': {ex}");
        }
    }

    internal static string SerializeStatus(CanaryStatus status)
        => JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
}
