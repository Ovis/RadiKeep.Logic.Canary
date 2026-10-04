using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.State;

internal sealed record StateProfile(string RadikoStationId, string RadiruAreaId, string RadiruStationId)
{
    internal static StateProfile From(CanaryOptions options) => new(options.RadikoStationId,
        CanaryInputs.NormalizeRadiruAreaKey(options.RadiruAreaId), options.RadiruStationId);
}

internal sealed record StateManifest(int FormatVersion, StateProfile Profile, string DatabaseSha256,
    DateTimeOffset CapturedAtUtc, string RadiCorderRevision, string CanaryRevision);

/// <summary>
/// 局定義・番組表専用DBの整合性と内容を検証し、持ち運び可能なスナップショットを作る。
/// </summary>
internal static class CanaryStateStore
{
    internal const string DatabaseFileName = "canary.db";
    internal const string ManifestFileName = "manifest.json";
    private static readonly HashSet<string> AllowedTables = new(StringComparer.Ordinal)
    {
        "__EFMigrationsHistory", "RadikoStations", "NhkRadiruAreas", "NhkRadiruAreaServices",
        "RadikoPrograms", "NhkRadiruPrograms"
    };

    internal static async Task<string?> ReadBaselineAsync(string directory, StateProfile profile)
    {
        if (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return null;
        }
        var files = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(new[] { DatabaseFileName, ManifestFileName }))
            throw new InvalidDataException("保存DBにはDBとmanifestのみを指定してください。WALなどの別ファイルは利用できません。");
        var databasePath = Path.Combine(directory, DatabaseFileName);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var manifest = JsonSerializer.Deserialize<StateManifest>(await File.ReadAllTextAsync(manifestPath))
            ?? throw new InvalidDataException("保存DBのmanifestが空です。");
        if (manifest.FormatVersion != 1 || manifest.Profile != profile)
        {
            throw new InvalidDataException("保存DBの形式または確認対象が一致しません。明示的な状態リセットが必要です。");
        }
        if (manifest.DatabaseSha256 != await ComputeHashAsync(databasePath))
        {
            throw new InvalidDataException("保存DBのチェックサムが一致しません。");
        }
        ValidateDatabase(databasePath);
        return databasePath;
    }

    internal static async Task SaveCandidateAsync(RadioDbContext database, string directory, StateProfile profile, DateTimeOffset capturedAt)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, DatabaseFileName + ".tmp");
        var destination = Path.Combine(directory, DatabaseFileName);
        try
        {
            var source = (SqliteConnection)database.Database.GetDbConnection();
            var wasClosed = source.State != System.Data.ConnectionState.Open;
            if (wasClosed) await source.OpenAsync();
            try
            {
                using var backup = Open(temporary, SqliteOpenMode.ReadWriteCreate);
                source.BackupDatabase(backup);
                using var compact = backup.CreateCommand();
                compact.CommandText = "PRAGMA journal_mode=DELETE; VACUUM;";
                compact.ExecuteNonQuery();
            }
            finally
            {
                if (wasClosed) await source.CloseAsync();
            }
            ValidateDatabase(temporary);
            File.Move(temporary, destination, overwrite: true);
            var manifest = new StateManifest(1, profile, await ComputeHashAsync(destination), capturedAt,
                Environment.GetEnvironmentVariable("RADICORDER_SUBMODULE_SHA") ?? "local",
                Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local");
            await File.WriteAllTextAsync(Path.Combine(directory, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void CopyDatabase(string sourcePath, string destinationPath)
    {
        ValidateDatabase(sourcePath);
        using var source = Open(sourcePath, SqliteOpenMode.ReadOnly);
        using var destination = Open(destinationPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
    }

    internal static void ValidateDatabase(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("保存DBが見つかりません。", path);
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new InvalidDataException("保存DBの整合性検証に失敗しました。");
        }
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read()) throw new InvalidDataException("保存DBに外部キー違反があります。");
        }
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) tables.Add(reader.GetString(0));
        if (!AllowedTables.IsSubsetOf(tables)) throw new InvalidDataException("保存DBに必要なテーブルがありません。");
        foreach (var table in tables.Where(table => !AllowedTables.Contains(table)))
        {
            command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidDataException($"局・番組表以外のテーブルにデータがあります: {table}");
        }
    }

    internal static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
