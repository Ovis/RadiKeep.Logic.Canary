using System.Text;

namespace Canary.Runner;

/// <summary>
/// 失敗した録音のffmpegログを成果物へコピーする。
/// </summary>
internal static class FfmpegLogs
{
    internal static HashSet<string> CaptureFfmpegLogSnapshot(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task AppendNewFfmpegLogsAsync(
        StringBuilder log,
        HashSet<string>? previousSnapshot,
        string sourceDirectory,
        string checkLogPath,
        string checkId)
    {
        if (previousSnapshot is null)
        {
            log.AppendLine("ffmpeg_logs=recording_not_started");
            return;
        }

        if (!Directory.Exists(sourceDirectory))
        {
            log.AppendLine("ffmpeg_logs=source_directory_missing");
            return;
        }

        var artifactLogDirectory = Path.GetDirectoryName(checkLogPath) ?? ".";
        Directory.CreateDirectory(artifactLogDirectory);

        var copied = new List<string>();
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*.log", SearchOption.TopDirectoryOnly)
                     .Select(Path.GetFullPath)
                     .Where(path => !previousSnapshot.Contains(path))
                     .OrderBy(File.GetLastWriteTimeUtc))
        {
            var destinationPath = BuildCopiedFfmpegLogPath(artifactLogDirectory, checkId, sourcePath);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            copied.Add(destinationPath);
        }

        if (copied.Count == 0)
        {
            log.AppendLine("ffmpeg_logs=none");
            return;
        }

        foreach (var copiedPath in copied)
        {
            log.AppendLine($"ffmpeg_log={Path.GetFileName(copiedPath)}");
        }

        await Task.CompletedTask;
    }

    internal static string BuildCopiedFfmpegLogPath(string artifactLogDirectory, string checkId, string sourcePath)
    {
        var fileName = $"{checkId}_ffmpeg_{Path.GetFileName(sourcePath)}";
        var destinationPath = Path.Combine(artifactLogDirectory, fileName);
        if (!File.Exists(destinationPath))
        {
            return destinationPath;
        }

        return Path.Combine(
            artifactLogDirectory,
            $"{checkId}_ffmpeg_{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
    }
}
