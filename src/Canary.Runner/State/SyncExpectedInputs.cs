using System.Text.Json;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.State;

/// <summary>
/// 保存前の本体API取得結果を保持し、新規DBでも有効なデータの保存漏れを検出する。
/// </summary>
internal sealed class SyncExpectedInputs(DateOnly retentionCutoff)
{
    private readonly Dictionary<string, string> stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> radikoPrograms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExpectedRadiruProgram> radiruPrograms = new(StringComparer.Ordinal);
    private sealed record ExpectedRadiruProgram(string Title, DateTimeOffset Start, DateTimeOffset End);

    internal void RecordStations(IEnumerable<RadikoStation> values)
    {
        foreach (var station in values) stations.TryAdd(station.StationId, SyncDatabaseSnapshot.Normalize(station));
    }

    internal void RecordRadikoPrograms(IEnumerable<RadikoProgram> values)
    {
        foreach (var program in values.Where(program => program.RadioDate >= retentionCutoff))
            radikoPrograms[program.ProgramId] = SyncDatabaseSnapshot.Normalize(program);
    }

    internal void RecordRadiruPrograms(string area, string service, IEnumerable<RadiruProgramJsonEntity> values)
    {
        foreach (var program in values)
            radiruPrograms[$"{area}:{service}:{program.Id}"] = new(program.GetTitle(), program.StartDate, program.EndDate);
    }

    internal void VerifyInitial(SyncDatabaseSnapshot snapshot)
    {
        VerifyRows(stations, snapshot.Tables["RadikoStations"], "radiko局定義");
        VerifyRows(radikoPrograms, snapshot.Tables["RadikoPrograms"], "radiko番組表");
        var saved = snapshot.Tables["NhkRadiruPrograms"];
        if (saved.Count != radiruPrograms.Count) throw new InvalidDataException("らじる番組表の保存件数が取得結果と一致しません。");
        foreach (var (key, expected) in radiruPrograms)
        {
            if (!saved.TryGetValue(key, out var value)) throw new InvalidDataException("らじる番組表に保存漏れがあります。");
            using var document = JsonDocument.Parse(value);
            var fields = document.RootElement;
            if (fields.GetProperty("Title").GetString() != expected.Title ||
                fields.GetProperty("StartTime").Deserialize<DateTimeOffset>() != expected.Start ||
                fields.GetProperty("EndTime").Deserialize<DateTimeOffset>() != expected.End)
                throw new InvalidDataException("らじる番組表の必須項目が取得結果と一致しません。");
        }
    }

    private static void VerifyRows(Dictionary<string, string> expected, Dictionary<string, string> actual, string name)
    {
        if (expected.Count != actual.Count || expected.Any(row => !actual.TryGetValue(row.Key, out var value) || row.Value != value))
            throw new InvalidDataException($"{name}の保存内容が取得結果と一致しません。");
    }
}
