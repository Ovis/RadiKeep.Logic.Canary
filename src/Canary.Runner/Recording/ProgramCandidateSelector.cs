using Canary.Runner.Hosting;
using RadiCorder.Logics.Models.NhkRadiru;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;

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

    internal static async Task<(RadiruOnDemandCandidate? Candidate, bool HasExpiredCandidate)> FindRadiruOnDemandCandidateAsync(
        LogicContext logicContext,
        string areaId,
        RadiruStationKind stationKind,
        DateTimeOffset nowJst)
    {
        var targetDates = new[]
        {
            nowJst,
            nowJst.AddDays(-1)
        };

        var candidates = new List<RadiruOnDemandCandidate>();
        var hasExpiredCandidate = false;

        foreach (var targetDate in targetDates)
        {
            var programs = await logicContext.RadiruApiClient.GetDailyProgramsAsync(areaId, stationKind.ServiceId, targetDate);
            foreach (var program in programs)
            {
                if (program.StartDate == default || program.EndDate == default || program.EndDate <= program.StartDate)
                {
                    continue;
                }

                if (program.EndDate > nowJst)
                {
                    continue;
                }

                var onDemandUrl = SelectRadiruOnDemandContentUrl(program);
                if (string.IsNullOrWhiteSpace(onDemandUrl))
                {
                    continue;
                }

                var expiresAtUtc = program.About.Audio.Expires == default
                    ? DateTime.MinValue
                    : program.About.Audio.Expires.UtcDateTime;
                if (expiresAtUtc <= DateTime.UtcNow)
                {
                    hasExpiredCandidate = true;
                    continue;
                }

                candidates.Add(new RadiruOnDemandCandidate(program, onDemandUrl, expiresAtUtc));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, hasExpiredCandidate);
        }

        var candidate = candidates
            .OrderBy(x => x.Program.EndDate - x.Program.StartDate)
            .ThenByDescending(x => x.Program.EndDate)
            .First();
        return (candidate, hasExpiredCandidate);
    }

    internal static string? SelectRadiruOnDemandContentUrl(RadiruProgramJsonEntity program)
    {
        var detailedContents = program.About.Audio.DetailedContent
            .Where(d => !string.IsNullOrWhiteSpace(d.ContentUrl))
            .ToList();
        if (detailedContents.Count == 0)
        {
            return null;
        }

        var prioritized = detailedContents.FirstOrDefault(d =>
            string.Equals(d.Name, "hls_widevine", StringComparison.OrdinalIgnoreCase) &&
            IsM3u8Url(d.ContentUrl));
        if (prioritized is not null)
        {
            return prioritized.ContentUrl;
        }

        var fallback = detailedContents.FirstOrDefault(d => IsM3u8Url(d.ContentUrl));
        return fallback?.ContentUrl;
    }

    internal static bool IsM3u8Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }
}
