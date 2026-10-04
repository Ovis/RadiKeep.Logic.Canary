using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.State;

internal sealed record SyncDifference(string Table, string Key, string Change, IReadOnlyList<string> Fields);

/// <summary>
/// DB採番と取得時刻を除いた保存内容を比較し、更新漏れ・残存局・欠落・重複を検出する。
/// </summary>
internal sealed class SyncDatabaseSnapshot
{
    internal Dictionary<string, Dictionary<string, string>> Tables { get; } = new(StringComparer.Ordinal);

    internal static async Task<SyncDatabaseSnapshot> ReadAsync(RadioDbContext database)
    {
        var snapshot = new SyncDatabaseSnapshot();
        snapshot.Add("RadikoStations", await database.RadikoStations.AsNoTracking().ToListAsync(), row => row.StationId);
        snapshot.Add("NhkRadiruAreas", await database.NhkRadiruAreas.AsNoTracking().ToListAsync(), row => row.AreaId);
        snapshot.Add("NhkRadiruAreaServices", await database.NhkRadiruAreaServices.AsNoTracking().Where(row => row.IsActive).ToListAsync(), row => $"{row.AreaId}:{row.ServiceId}");
        snapshot.Add("RadikoPrograms", await database.RadikoPrograms.AsNoTracking().ToListAsync(), row => row.ProgramId);
        snapshot.Add("NhkRadiruPrograms", await database.NhkRadiruPrograms.AsNoTracking().ToListAsync(), row => $"{row.AreaId}:{row.StationId}:{row.ProgramId}");
        return snapshot;
    }

    internal void ValidateInitial()
    {
        foreach (var (table, rows) in Tables)
        {
            if (rows.Count == 0) throw new InvalidDataException($"新規同期でデータが保存されませんでした: {table}");
        }
    }

    internal static SyncDatabaseSnapshot ExpectedAfter(SyncDatabaseSnapshot previous, SyncDatabaseSnapshot current, DateOnly retentionCutoff)
    {
        var expected = new SyncDatabaseSnapshot();
        foreach (var (table, rows) in current.Tables)
        {
            var merged = table == "NhkRadiruAreaServices"
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(previous.Tables[table], StringComparer.Ordinal);
            if (table == "RadikoStations")
            {
                foreach (var key in merged.Keys.Except(rows.Keys).ToList())
                {
                    var fields = JsonSerializer.Deserialize<SortedDictionary<string, JsonElement>>(merged[key])!;
                    fields["IsActive"] = JsonSerializer.SerializeToElement(false);
                    merged[key] = JsonSerializer.Serialize(fields);
                }
            }
            foreach (var row in rows) merged[row.Key] = row.Value;
            if (table is "RadikoPrograms" or "NhkRadiruPrograms")
            {
                foreach (var key in merged.Keys.ToList())
                {
                    using var document = JsonDocument.Parse(merged[key]);
                    if (DateOnly.Parse(document.RootElement.GetProperty("RadioDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture) < retentionCutoff)
                        merged.Remove(key);
                }
            }
            expected.Tables[table] = merged;
        }
        return expected;
    }

    internal IReadOnlyList<SyncDifference> DifferencesFrom(SyncDatabaseSnapshot actual)
    {
        var differences = new List<SyncDifference>();
        foreach (var (table, expectedRows) in Tables)
        {
            var actualRows = actual.Tables[table];
            foreach (var key in expectedRows.Keys.Union(actualRows.Keys).Order(StringComparer.Ordinal))
            {
                if (!expectedRows.TryGetValue(key, out var expected))
                    differences.Add(new(table, key, "unexpected", []));
                else if (!actualRows.TryGetValue(key, out var value))
                    differences.Add(new(table, key, "missing", []));
                else if (expected != value)
                {
                    var expectedFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(expected)!;
                    var actualFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value)!;
                    var fields = expectedFields.Keys.Union(actualFields.Keys)
                        .Where(field => !expectedFields.TryGetValue(field, out var left) ||
                            !actualFields.TryGetValue(field, out var right) || left.GetRawText() != right.GetRawText()).ToList();
                    differences.Add(new(table, key, "changed", fields));
                }
            }
        }
        return differences;
    }

    private void Add<T>(string table, IEnumerable<T> rows, Func<T, string> key)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!values.TryAdd(key(row), Normalize(row)))
                throw new InvalidDataException($"DBに確認キーの重複があります: {table}");
        }
        Tables[table] = values;
    }

    internal static string Normalize<T>(T row)
    {
        var fields = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(row).EnumerateObject())
            if (property.Name is not "Id" and not "LastSeenAtUtc" and not "LastSyncedAtUtc")
                fields[property.Name] = property.Value;
        return JsonSerializer.Serialize(fields);
    }
}
