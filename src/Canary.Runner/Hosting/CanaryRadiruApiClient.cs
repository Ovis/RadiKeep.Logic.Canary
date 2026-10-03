using RadiCorder.Logics.ApiClients;
using RadiCorder.Logics.Interfaces;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;

namespace Canary.Runner.Hosting;

/// <summary>
/// 本体の一括取得を、Canaryで確認する局と今日・昨日に絞る。
/// 応答の取得・解析は本体のAPIクライアントへ委譲する。
/// </summary>
internal sealed class CanaryRadiruApiClient(RadiruApiClient client, RadiruProbeTarget target) : IRadiruApiClient
{
    public async ValueTask<List<(string AreaId, string ServiceId)>> GetAvailableAreaServicesAsync(
        DateTimeOffset targetDateJst, CancellationToken cancellationToken = default)
    {
        var date = DateOnly.FromDateTime(targetDateJst.DateTime);
        if (date != target.TodayJst && date != target.TodayJst.AddDays(-1))
        {
            return [];
        }

        var services = await client.GetAvailableAreaServicesAsync(targetDateJst, cancellationToken);
        return services.Where(service =>
            string.Equals(service.AreaId, target.AreaId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(service.ServiceId, target.ServiceId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public Task<List<RadiruProgramJsonEntity>> GetDailyProgramsAsync(
        string areaId, string serviceId, DateTimeOffset date, CancellationToken cancellationToken = default) =>
        client.GetDailyProgramsAsync(areaId, serviceId, date, cancellationToken);
}

internal sealed record RadiruProbeTarget(string AreaId, string ServiceId, DateOnly TodayJst);
