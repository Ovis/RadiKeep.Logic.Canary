using Canary.Runner.Hosting;
using RadiCorder.Logics.Models.NhkRadiru;
using Microsoft.EntityFrameworkCore;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner;

/// <summary>
/// 実サービスから取得した番組から確認対象を選ぶ。
/// </summary>
internal static class ProgramCandidateSelector
{
    internal static async Task<(string StationId, string ProgramId, string Title)?> FindRadikoNowOnAirProgramAsync(LogicContext logicContext, string stationId, DateTimeOffset nowJst)
    {
        var programs = await logicContext.RadikoApiClient.GetWeeklyProgramsAsync(stationId);
        foreach (var p in programs)
        {
            var startJst = TimeZoneInfo.ConvertTime(p.StartTime, CanaryInputs.ResolveJapanTimeZone());
            var endJst = TimeZoneInfo.ConvertTime(p.EndTime, CanaryInputs.ResolveJapanTimeZone());
            if (nowJst >= startJst && nowJst <= endJst)
            {
                return (stationId, p.ProgramId, p.Title);
            }
        }

        return null;
    }

    internal static async Task<(string ProgramId, string Title, DateTimeOffset StartTime, DateTimeOffset EndTime)?> FindRadikoTimeFreeCandidateAsync(
        LogicContext logicContext,
        string stationId,
        DateTimeOffset nowJst,
        int recordSeconds)
    {
        var programs = await logicContext.RadikoApiClient.GetWeeklyProgramsAsync(stationId);
        var minimumDuration = Math.Max(recordSeconds, 10);
        var cutoff = nowJst.AddMinutes(-3);

        var candidate = programs
            .Where(p =>
                !string.IsNullOrWhiteSpace(p.ProgramId) &&
                !string.IsNullOrWhiteSpace(p.Title) &&
                p.StartTime != default &&
                p.EndTime != default &&
                p.EndTime > p.StartTime)
            .Select(p => new
            {
                Program = p,
                StartJst = TimeZoneInfo.ConvertTime(p.StartTime, CanaryInputs.ResolveJapanTimeZone()),
                EndJst = TimeZoneInfo.ConvertTime(p.EndTime, CanaryInputs.ResolveJapanTimeZone())
            })
            .Where(x =>
                x.EndJst <= cutoff &&
                (x.EndJst - x.StartJst).TotalSeconds >= minimumDuration)
            .OrderByDescending(x => x.EndJst)
            .FirstOrDefault();

        if (candidate is null)
        {
            return null;
        }

        return (candidate.Program.ProgramId, candidate.Program.Title, candidate.StartJst, candidate.EndJst);
    }

    internal static async Task<(NhkRadiruProgram? Candidate, bool HasExpiredCandidate)> FindRadiruOnDemandCandidateAsync(
        LogicContext logicContext,
        string areaId,
        RadiruStationKind stationKind,
        DateTimeOffset nowJst)
    {
        // URL選択と番組データの変換・保存も本体の実装を通す。
        await logicContext.ProgramScheduleLobLogic.UpdateRadiruProgramDataAsync();
        var programs = await logicContext.DbContext.NhkRadiruPrograms.AsNoTracking()
            .Where(program => program.AreaId == areaId && program.StationId == stationKind.ServiceId)
            .ToListAsync();
        var candidates = programs.Where(program =>
            program.StartTime != default && program.EndTime != default &&
            program.EndTime > program.StartTime && program.EndTime <= nowJst &&
            !string.IsNullOrWhiteSpace(program.OnDemandContentUrl)).ToList();
        var nowUtc = DateTime.UtcNow;
        var hasExpiredCandidate = candidates.Any(program =>
            !program.OnDemandExpiresAtUtc.HasValue || program.OnDemandExpiresAtUtc.Value <= nowUtc);
        var candidate = candidates
            .Where(program => program.OnDemandExpiresAtUtc.HasValue && program.OnDemandExpiresAtUtc.Value > nowUtc)
            .OrderBy(program => program.EndTime - program.StartTime)
            .ThenByDescending(program => program.EndTime)
            .FirstOrDefault();
        return (candidate, hasExpiredCandidate);
    }
}
