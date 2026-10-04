using Canary.Runner.Hosting;
using RadiCorder.Logics.ApiClients;
using RadiCorder.Logics.Interfaces;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner.State;

/// <summary>
/// 番組表の取得対象を指定局に限定し、本体が空応答をスキップする前に異常を検出する。
/// </summary>
internal sealed class SyncRadikoApiClient(RadikoApiClient client, StateProfile profile, SyncExpectedInputs inputs) : IRadikoApiClient
{
    public async Task<List<RadikoStation>> GetRadikoStationsAsync(CancellationToken cancellationToken = default)
    {
        var stations = await client.GetRadikoStationsAsync(cancellationToken);
        if (stations.Count == 0 || stations.Any(station => new[]
            { station.StationId, station.StationName, station.RegionId, station.RegionName, station.Area }.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("radiko局定義の必須項目が不足しています。");
        if (!stations.Any(station => station.StationId == profile.RadikoStationId))
            throw new InvalidDataException("番組表の確認対象局が全国局定義にありません。");
        inputs.RecordStations(stations);
        return stations;
    }

    public async Task<List<RadikoProgram>> GetWeeklyProgramsAsync(string stationId, CancellationToken cancellationToken = default)
    {
        if (stationId != profile.RadikoStationId) return [];
        var programs = await client.GetWeeklyProgramsAsync(stationId, cancellationToken);
        if (programs.Count == 0 || ProgramSchemaValidator.ValidateRadikoPrograms(programs).RequiredIssues.Count > 0)
            throw new InvalidDataException("radiko週間番組表が空または必須項目が不正です。");
        inputs.RecordRadikoPrograms(programs);
        return programs;
    }

    public Task<List<string>> GetStationsByAreaAsync(string area, CancellationToken cancellationToken = default) => client.GetStationsByAreaAsync(area, cancellationToken);
    public Task<List<string>> GetRealTimePlaylistUrlsAsync(string stationId, bool useAreaFreeConnection, string? requestStationId = null, CancellationToken cancellationToken = default) => client.GetRealTimePlaylistUrlsAsync(stationId, useAreaFreeConnection, requestStationId, cancellationToken);
    public Task<List<string>> GetTimeFreePlaylistCreateUrlsAsync(string stationId, bool useAreaFreeConnection, CancellationToken cancellationToken = default) => client.GetTimeFreePlaylistCreateUrlsAsync(stationId, useAreaFreeConnection, cancellationToken);
}

internal sealed class SyncRadiruApiClient(CanaryRadiruApiClient client, SyncExpectedInputs inputs) : IRadiruApiClient
{
    public ValueTask<List<(string AreaId, string ServiceId)>> GetAvailableAreaServicesAsync(DateTimeOffset date, CancellationToken cancellationToken = default) => client.GetAvailableAreaServicesAsync(date, cancellationToken);

    public async Task<List<RadiruProgramJsonEntity>> GetDailyProgramsAsync(string areaId, string serviceId, DateTimeOffset date, CancellationToken cancellationToken = default)
    {
        var programs = await client.GetDailyProgramsAsync(areaId, serviceId, date, cancellationToken);
        if (programs.Count == 0 || ProgramSchemaValidator.ValidateRadiruPrograms(programs).RequiredIssues.Count > 0)
            throw new InvalidDataException("らじる日次番組表が空または必須項目が不正です。");
        inputs.RecordRadiruPrograms(areaId, serviceId, programs);
        return programs;
    }
}
