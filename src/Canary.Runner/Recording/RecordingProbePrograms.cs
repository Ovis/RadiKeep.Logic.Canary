using Canary.Runner.Hosting;
using RadiCorder.Logics.Extensions;
using RadiCorder.Logics.Models.Enums;
using RadiCorder.Logics.Models.Radiko;
using RadiCorder.Logics.Models.NhkRadiru;
using RadiCorder.Logics.Models.NhkRadiru.JsonEntity;
using RadiCorder.Logics.RdbContext;

namespace Canary.Runner;

/// <summary>
/// 短時間録音に必要な番組データを確認用DBへ保存する。
/// </summary>
internal static class RecordingProbePrograms
{
    internal static async Task<string> SeedRadikoProgramForRealtimeRecordingAsync(
        LogicContext logicContext,
        string stationId,
        string title,
        string areaId,
        int recordSeconds)
    {
        var nowJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone());
        var start = nowJst.AddSeconds(-2);
        var end = nowJst.AddSeconds(recordSeconds);
        var programId = $"canary-radiko-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        if (!logicContext.RadikoStationDic.ContainsKey(stationId))
        {
            logicContext.RadikoStationDic[stationId] = stationId;
        }

        var station = await logicContext.DbContext.RadikoStations.FindAsync(stationId);
        if (station is null)
        {
            await logicContext.DbContext.RadikoStations.AddAsync(new RadikoStation
            {
                StationId = stationId,
                RegionId = areaId,
                RegionName = areaId,
                RegionOrder = 0,
                Area = areaId,
                StationName = logicContext.RadikoStationDic[stationId],
                StationUrl = string.Empty,
                LogoPath = string.Empty,
                AreaFree = true,
                TimeFree = true,
                StationOrder = 0
            });
        }

        await logicContext.DbContext.RadikoPrograms.AddAsync(new RadikoProgram
        {
            ProgramId = programId,
            StationId = stationId,
            Title = title,
            RadioDate = start.ToRadioDate(),
            DaysOfWeek = start.ToRadioDayOfWeek().ToDaysOfWeek(),
            StartTime = start,
            EndTime = end,
            Performer = string.Empty,
            Description = "canary realtime record",
            AvailabilityTimeFree = AvailabilityTimeFree.Available,
            ProgramUrl = string.Empty,
            ImageUrl = string.Empty
        });

        await logicContext.DbContext.SaveChangesAsync();
        return programId;
    }

    internal static async Task<string> SeedRadikoProgramForTimeFreeRecordingAsync(
        LogicContext logicContext,
        string stationId,
        string title,
        string areaId,
        DateTimeOffset sourceStartTime,
        DateTimeOffset sourceEndTime,
        int recordSeconds)
    {
        var start = sourceStartTime;
        var endLimit = start.AddSeconds(recordSeconds);
        var end = sourceEndTime <= endLimit ? sourceEndTime : endLimit;
        if (end <= start)
        {
            end = start.AddSeconds(Math.Max(10, recordSeconds));
        }

        var programId = $"canary-radiko-timefree-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        if (!logicContext.RadikoStationDic.ContainsKey(stationId))
        {
            logicContext.RadikoStationDic[stationId] = stationId;
        }

        var station = await logicContext.DbContext.RadikoStations.FindAsync(stationId);
        if (station is null)
        {
            await logicContext.DbContext.RadikoStations.AddAsync(new RadikoStation
            {
                StationId = stationId,
                RegionId = areaId,
                RegionName = areaId,
                RegionOrder = 0,
                Area = areaId,
                StationName = logicContext.RadikoStationDic[stationId],
                StationUrl = string.Empty,
                LogoPath = string.Empty,
                AreaFree = true,
                TimeFree = true,
                StationOrder = 0
            });
        }

        await logicContext.DbContext.RadikoPrograms.AddAsync(new RadikoProgram
        {
            ProgramId = programId,
            StationId = stationId,
            Title = title,
            RadioDate = start.ToRadioDate(),
            DaysOfWeek = start.ToRadioDayOfWeek().ToDaysOfWeek(),
            StartTime = start,
            EndTime = end,
            Performer = string.Empty,
            Description = "canary timefree record",
            AvailabilityTimeFree = AvailabilityTimeFree.Available,
            ProgramUrl = string.Empty,
            ImageUrl = string.Empty
        });

        await logicContext.DbContext.SaveChangesAsync();
        return programId;
    }

    internal static async Task<string> SeedRadiruProgramForRealtimeRecordingAsync(
        LogicContext logicContext,
        string normalizedAreaId,
        RadiruStationKind stationKind,
        RadiruProgramJsonEntity sourceProgram,
        int recordSeconds)
    {
        var nowJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, CanaryInputs.ResolveJapanTimeZone());
        var start = nowJst.AddSeconds(-2);
        var end = nowJst.AddSeconds(recordSeconds);
        var programId = $"canary-radiru-{stationKind.ServiceId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        await logicContext.DbContext.NhkRadiruPrograms.AddAsync(new NhkRadiruProgram
        {
            ProgramId = programId,
            StationId = stationKind.ServiceId,
            AreaId = normalizedAreaId,
            Title = sourceProgram.Name,
            Subtitle = sourceProgram.IdentifierGroup.RadioEpisodeName ?? string.Empty,
            RadioDate = start.ToRadioDate(),
            DaysOfWeek = start.ToRadioDayOfWeek().ToDaysOfWeek(),
            StartTime = start,
            EndTime = end,
            Performer = string.Empty,
            Description = sourceProgram.Description ?? string.Empty,
            SiteId = sourceProgram.IdentifierGroup.SiteId ?? string.Empty,
            EventId = sourceProgram.About.Id ?? string.Empty,
            ProgramUrl = sourceProgram.About.Url ?? string.Empty,
            ImageUrl = sourceProgram.About.PartOfSeries.Logo.Medium.Url ?? string.Empty,
            OnDemandContentUrl = null,
            OnDemandExpiresAtUtc = null
        });

        await logicContext.DbContext.SaveChangesAsync();
        return programId;
    }

    internal static async Task<string> SeedRadiruProgramForOnDemandRecordingAsync(
        LogicContext logicContext,
        string normalizedAreaId,
        RadiruStationKind stationKind,
        RadiruProgramJsonEntity sourceProgram,
        string onDemandContentUrl,
        DateTime onDemandExpiresAtUtc)
    {
        var programId = $"canary-radiru-ondemand-{stationKind.ServiceId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        await logicContext.DbContext.NhkRadiruPrograms.AddAsync(new NhkRadiruProgram
        {
            ProgramId = programId,
            StationId = stationKind.ServiceId,
            AreaId = normalizedAreaId,
            Title = sourceProgram.Name,
            Subtitle = sourceProgram.IdentifierGroup.RadioEpisodeName ?? string.Empty,
            RadioDate = sourceProgram.StartDate.ToRadioDate(),
            DaysOfWeek = sourceProgram.StartDate.ToRadioDayOfWeek().ToDaysOfWeek(),
            StartTime = sourceProgram.StartDate,
            EndTime = sourceProgram.EndDate,
            Performer = string.Empty,
            Description = sourceProgram.Description ?? string.Empty,
            SiteId = sourceProgram.IdentifierGroup.SiteId ?? string.Empty,
            EventId = sourceProgram.About.Id ?? string.Empty,
            ProgramUrl = sourceProgram.About.Url ?? string.Empty,
            ImageUrl = sourceProgram.About.PartOfSeries.Logo.Medium.Url ?? string.Empty,
            OnDemandContentUrl = onDemandContentUrl,
            OnDemandExpiresAtUtc = onDemandExpiresAtUtc
        });

        await logicContext.DbContext.SaveChangesAsync();
        return programId;
    }
}
