using System.Diagnostics;

namespace Canary.Runner;

/// <summary>
/// 録音に必要なffmpegの実行可否を確認する。
/// </summary>
internal static class FfmpegCheck
{
    internal static async Task<CheckResult> CheckFfmpegAsync(string logPath)
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
}
