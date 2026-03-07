using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RadiKeep.Logics.ApiClients;
using RadiKeep.Logics.Application;
using RadiKeep.Logics.Context;
using RadiKeep.Logics.Domain.Station;
using RadiKeep.Logics.Domain.Recording;
using RadiKeep.Logics.Extensions;
using RadiKeep.Logics.Interfaces;
using RadiKeep.Logics.Infrastructure.ProgramSchedule;
using RadiKeep.Logics.Infrastructure.Recording;
using RadiKeep.Logics.Logics.ProgramScheduleLogic;
using RadiKeep.Logics.Logics.RecordJobLogic;
using RadiKeep.Logics.Logics.RadikoLogic;
using RadiKeep.Logics.Logics.StationLogic;
using RadiKeep.Logics.Mappers;
using RadiKeep.Logics.Models.NhkRadiru;
using RadiKeep.Logics.Models.NhkRadiru.JsonEntity;
using RadiKeep.Logics.Models.Enums;
using RadiKeep.Logics.Models.Radiko;
using RadiKeep.Logics.Primitives;
using RadiKeep.Logics.Primitives.DataAnnotations;
using RadiKeep.Logics.RdbContext;
using RadiKeep.Logics.Services;

var argsMap = ParseArgs(args);

var statusPath = GetArg(argsMap, "status-json", "results/status.json");
var logDir = GetArg(argsMap, "log-dir", "logs");
var recordOutputDir = GetArg(argsMap, "record-output-dir", "artifacts/recordings");
var radikoStationId = GetArg(argsMap, "radiko-station-id", "TBS");
var radiruAreaId = GetArg(argsMap, "radiru-area-id", "JP13");
var radiruStationId = GetArg(argsMap, "radiru-station-id", "r1");
var radikoUserId = GetArg(argsMap, "radiko-user-id", Environment.GetEnvironmentVariable("RADIKO_USER_ID") ?? string.Empty);
var radikoPassword = GetArg(argsMap, "radiko-password", Environment.GetEnvironmentVariable("RADIKO_PASSWORD") ?? string.Empty);
var realtimeRecordSeconds = int.TryParse(GetArg(argsMap, "realtime-record-seconds", "30"), out var parsedSeconds)
    ? Math.Max(10, Math.Min(parsedSeconds, 180))
    : 30;
var timefreeRecordSeconds = int.TryParse(GetArg(argsMap, "timefree-record-seconds", "30"), out var parsedTimefreeSeconds)
    ? Math.Max(10, Math.Min(parsedTimefreeSeconds, 120))
    : 30;

Directory.CreateDirectory(Path.GetDirectoryName(statusPath) ?? ".");
Directory.CreateDirectory(logDir);
Directory.CreateDirectory(recordOutputDir);

var checks = new List<CheckResult>();

try
{
    var ffmpegCheck = await CheckFfmpegAsync(Path.Combine(logDir, "C000_FFMPEG.log"));
    checks.Add(ffmpegCheck);

    var todayJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone()).Date;
    using var httpClient = CreateHttpClient();
    using var logicContext = CreateLogicContext(radikoUserId, radikoPassword);
    var c001 = await CheckRadikoDailyFetchAsync(logicContext, radikoStationId, todayJst, Path.Combine(logDir, "C001_RADIKO_DAILY_FETCH.log"));
    checks.Add(c001);

    var c002 = await CheckRadiruDailyFetchAsync(
        logicContext,
        radiruAreaId,
        radiruStationId,
        todayJst,
        Path.Combine(logDir, "C002_RADIRU_DAILY_FETCH.log"));
    checks.Add(c002);

    var c010 = await CheckRadikoLoginAsync(
        logicContext,
        radikoUserId,
        radikoPassword,
        Path.Combine(logDir, "C010_RADIKO_LOGIN.log"));
    checks.Add(c010);

    var c003Radiko = await CheckRadikoRealtimeRecordingAsync(
        logicContext,
        radikoStationId,
        realtimeRecordSeconds,
        recordOutputDir,
        Path.Combine(logDir, "C003_RADIKO_REALTIME_RECORD.log"));
    checks.Add(c003Radiko);

    var c004RadikoTimefree = await CheckRadikoTimeFreeRecordingAsync(
        logicContext,
        radikoStationId,
        timefreeRecordSeconds,
        recordOutputDir,
        Path.Combine(logDir, "C004_RADIKO_TIMEFREE_RECORD.log"));
    checks.Add(c004RadikoTimefree);

    var c003Radiru = await CheckRadiruRealtimeRecordingAsync(
        logicContext,
        radiruAreaId,
        radiruStationId,
        realtimeRecordSeconds,
        recordOutputDir,
        Path.Combine(logDir, "C003_RADIRU_REALTIME_RECORD.log"));
    checks.Add(c003Radiru);

    var overall = checks.All(c => c.Result == "PASS") ? "PASS" : "FAIL";
    var summary = new CanaryStatus
    {
        Result = overall,
        Message = overall == "PASS" ? "Bootstrap checks passed." : "One or more bootstrap checks failed.",
        TimestampJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone()).ToString("O"),
        Checks = checks
    };

    await WriteStatusAsync(statusPath, summary);
    return overall == "PASS" ? 0 : 2;
}
catch (Exception ex)
{
    checks.Add(new CheckResult("C999_UNHANDLED_EXCEPTION", "FAIL", $"Unhandled exception: {ex.Message}", "E-C999-UNHANDLED"));

    var summary = new CanaryStatus
    {
        Result = "FAIL",
        Message = "Unhandled exception occurred.",
        TimestampJst = DateTimeOffset.UtcNow.ToString("O"),
        Checks = checks
    };

    await WriteStatusAsync(statusPath, summary);
    return 2;
}

static TimeZoneInfo ResolveJapanTimeZone()
{
    var candidates = new[] { "Asia/Tokyo", "Tokyo Standard Time" };
    foreach (var id in candidates)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            // try next
        }
    }

    return TimeZoneInfo.Utc;
}

static HttpClient CreateHttpClient()
{
    var handler = new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                 System.Net.DecompressionMethods.Deflate |
                                 System.Net.DecompressionMethods.Brotli
    };

    var client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RadiKeep.Logic.Canary/0.1");
    return client;
}

static async Task<CheckResult> CheckRadikoDailyFetchAsync(LogicContext logicContext, string stationId, DateTime dateJst, string logPath)
{
    var log = new StringBuilder();
    log.AppendLine($"check=C001 station={stationId} date={dateJst:yyyy-MM-dd}");

    try
    {
        var programs = (await logicContext.RadikoApiClient.GetWeeklyProgramsAsync(stationId))
            .Where(p =>
            {
                var jstStart = TimeZoneInfo.ConvertTime(p.StartTime, ResolveJapanTimeZone());
                return jstStart.Date == dateJst.Date;
            })
            .ToList();
        var programDataPath = GetProgramDataLogPath(logPath);
        await WriteJsonLogAsync(programDataPath, programs);

        log.AppendLine($"program_count={programs.Count}");
        log.AppendLine($"program_data_log={programDataPath}");
        if (programs.Count == 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", "Program list is empty.", "E-C001-EMPTY");
        }

        var validation = ValidateRadikoPrograms(programs);
        AppendValidationLog(log, validation, programs.Count);

        if (validation.RequiredIssues.Count > 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", $"Invalid programs found: {validation.RequiredIssues.Count}", "E-C001-SCHEMA");
        }

        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C001_RADIKO_DAILY_FETCH", "PASS", $"Fetched {programs.Count} programs.", string.Empty);
    }
    catch (Exception ex)
    {
        log.AppendLine(ex.ToString());
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", $"Failed to fetch radiko daily programs: {ex.Message}", "E-C001-NETWORK");
    }
}

static async Task<CheckResult> CheckRadiruDailyFetchAsync(
    LogicContext logicContext,
    string areaId,
    string stationId,
    DateTime dateJst,
    string logPath)
{
    var log = new StringBuilder();
    var normalizedAreaKey = NormalizeRadiruAreaKey(areaId);
    log.AppendLine($"check=C002 area={areaId} normalized_area={normalizedAreaKey} station={stationId} date={dateJst:yyyy-MM-dd}");

    try
    {
        var areaKind = Enum.GetValues<RadiruAreaKind>()
            .FirstOrDefault(x => x.GetEnumCodeId() == normalizedAreaKey);
        var stationKind = Enumeration.GetAll<RadiruStationKind>()
            .FirstOrDefault(x => string.Equals(x.ServiceId, stationId, StringComparison.OrdinalIgnoreCase));
        if (stationKind is null)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "Unknown radiru station.", "E-C002-SCHEMA");
        }

        await logicContext.StationLobLogic.UpdateRadiruStationInformationAsync();

        var targetDate = new DateTimeOffset(dateJst, ResolveJapanTimeZone().GetUtcOffset(dateJst));
        var publications = await logicContext.RadiruApiClient.GetDailyProgramsAsync(areaKind, stationKind, targetDate);
        var programDataPath = GetProgramDataLogPath(logPath);
        await WriteJsonLogAsync(programDataPath, publications);

        log.AppendLine($"program_count={publications.Count}");
        log.AppendLine($"program_data_log={programDataPath}");
        if (publications.Count == 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "Program list is empty.", "E-C002-EMPTY");
        }

        var validation = ValidateRadiruPrograms(publications);
        AppendValidationLog(log, validation, publications.Count);

        if (validation.RequiredIssues.Count > 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", $"Invalid programs found: {validation.RequiredIssues.Count}", "E-C002-SCHEMA");
        }

        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C002_RADIRU_DAILY_FETCH", "PASS", $"Fetched {publications.Count} programs.", string.Empty);
    }
    catch (Exception ex)
    {
        log.AppendLine(ex.ToString());
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", $"Failed to fetch radiru daily programs: {ex.Message}", "E-C002-NETWORK");
    }
}

static ProgramSchemaValidationResult ValidateRadikoPrograms(
    IReadOnlyList<RadikoProgram> programs)
{
    var requiredIssues = new List<ProgramSchemaIssue>();
    var optionalMissing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var seenProgramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < programs.Count; i++)
    {
        var p = programs[i];
        var idForLog = string.IsNullOrWhiteSpace(p.ProgramId) ? $"index:{i}" : p.ProgramId;

        if (string.IsNullOrWhiteSpace(p.ProgramId))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "ProgramId", "missing"));
        }
        else if (!seenProgramIds.Add(p.ProgramId))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "ProgramId", "duplicate"));
        }

        if (string.IsNullOrWhiteSpace(p.StationId))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "StationId", "missing"));
        }

        if (string.IsNullOrWhiteSpace(p.Title))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Title", "missing"));
        }

        if (p.StartTime == default)
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "StartTime", "missing_or_invalid"));
        }

        if (p.EndTime == default)
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "EndTime", "missing_or_invalid"));
        }

        if (p.StartTime != default && p.EndTime != default && p.EndTime <= p.StartTime)
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Duration", "end_before_or_equal_start"));
        }

        CountOptionalIfMissing(optionalMissing, "Performer", p.Performer);
        CountOptionalIfMissing(optionalMissing, "Description", p.Description);
        CountOptionalIfMissing(optionalMissing, "ProgramUrl", p.ProgramUrl);
        CountOptionalIfMissing(optionalMissing, "ImageUrl", p.ImageUrl);
    }

    return new ProgramSchemaValidationResult(requiredIssues, optionalMissing);
}

static ProgramSchemaValidationResult ValidateRadiruPrograms(
    IReadOnlyList<RadiruProgramJsonEntity> programs)
{
    var requiredIssues = new List<ProgramSchemaIssue>();
    var optionalMissing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var seenProgramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < programs.Count; i++)
    {
        var p = programs[i];
        var idForLog = string.IsNullOrWhiteSpace(p.Id) ? $"index:{i}" : p.Id;

        if (string.IsNullOrWhiteSpace(p.Id))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Id", "missing"));
        }
        else if (!seenProgramIds.Add(p.Id))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Id", "duplicate"));
        }

        if (string.IsNullOrWhiteSpace(p.Name))
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Name", "missing"));
        }

        if (p.StartDate == default)
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "StartDate", "missing_or_invalid"));
        }

        if (p.EndDate == default)
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "EndDate", "missing_or_invalid"));
        }

        if (p.StartDate != default && p.EndDate != default && p.EndDate <= p.StartDate)
        {
            requiredIssues.Add(new ProgramSchemaIssue(idForLog, "Duration", "end_before_or_equal_start"));
        }

        CountOptionalIfMissing(optionalMissing, "Description", p.Description);
        CountOptionalIfMissing(optionalMissing, "Url", p.Url);
        CountOptionalIfMissing(optionalMissing, "IdentifierGroup.ServiceId", p.IdentifierGroup.ServiceId);
        CountOptionalIfMissing(optionalMissing, "IdentifierGroup.AreaId", p.IdentifierGroup.AreaId);
        CountOptionalIfMissing(optionalMissing, "IdentifierGroup.RadioEpisodeName", p.IdentifierGroup.RadioEpisodeName);
        CountOptionalIfMissing(optionalMissing, "About.Url", p.About.Url);
        CountOptionalIfMissing(optionalMissing, "About.PartOfSeries.Logo.Medium.Url", p.About.PartOfSeries.Logo.Medium.Url);
    }

    return new ProgramSchemaValidationResult(requiredIssues, optionalMissing);
}

static void AppendValidationLog(StringBuilder log, ProgramSchemaValidationResult validation, int totalCount)
{
    log.AppendLine($"required_issue_count={validation.RequiredIssues.Count}");

    foreach (var issue in validation.RequiredIssues.Take(20))
    {
        log.AppendLine($"required_issue program={issue.ProgramId} field={issue.Field} reason={issue.Reason}");
    }

    foreach (var missing in validation.OptionalMissingCounts.OrderBy(x => x.Key))
    {
        var ratio = totalCount == 0 ? 0 : (double)missing.Value / totalCount;
        log.AppendLine($"optional_missing field={missing.Key} count={missing.Value} ratio={ratio:F3}");
    }
}

static void CountOptionalIfMissing(Dictionary<string, int> counts, string fieldName, string? value)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        return;
    }

    counts.TryGetValue(fieldName, out var current);
    counts[fieldName] = current + 1;
}

static string NormalizeRadiruAreaKey(string areaId)
{
    if (string.IsNullOrWhiteSpace(areaId))
    {
        return areaId;
    }

    var trimmed = areaId.Trim();
    if (trimmed.StartsWith("JP", StringComparison.OrdinalIgnoreCase))
    {
        var suffix = trimmed[2..];
        if (int.TryParse(suffix, out var numeric))
        {
            return (numeric * 10).ToString("000", CultureInfo.InvariantCulture);
        }
    }

    return trimmed;
}

static LogicContext CreateLogicContext(string radikoUserId, string radikoPassword)
{
    var services = new ServiceCollection();
    services.AddHttpClient(HttpClientNames.Radiko).ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.Brotli |
                                     System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate
        });
    services.AddHttpClient(HttpClientNames.Radiru).ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.Brotli |
                                     System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate
        });
    var provider = services.BuildServiceProvider();
    var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    var workRoot = Path.Combine(Path.GetTempPath(), "radikeep-canary");
    var tempRoot = Path.Combine(workRoot, "temp");
    var logRoot = Path.Combine(workRoot, "logs");
    Directory.CreateDirectory(tempRoot);
    Directory.CreateDirectory(logRoot);

    var configMock = new Mock<IAppConfigurationService>();
    var stationDic = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    configMock.SetupGet(x => x.RadikoStationDic).Returns(stationDic);
    configMock.SetupGet(x => x.ExternalServiceUserAgent).Returns("RadiKeep.Logic.Canary/0.1");
    configMock.SetupGet(x => x.TemporaryFileSaveDir).Returns(tempRoot);
    configMock.SetupGet(x => x.FfmpegExecutablePath).Returns(string.Empty);
    configMock.SetupGet(x => x.EmbedProgramImageOnRecord).Returns(false);
    configMock.SetupGet(x => x.RadiruApiMinRequestIntervalMs).Returns(0);
    configMock.SetupGet(x => x.RadiruApiRequestJitterMs).Returns(0);
    configMock.SetupGet(x => x.IsRadikoAreaFree).Returns(false);
    configMock.Setup(x => x.UpdateRadikoPremiumUser(It.IsAny<bool>()));
    configMock.Setup(x => x.UpdateRadikoAreaFree(It.IsAny<bool>()));
    configMock.Setup(x => x.UpdateRadikoStationDic(It.IsAny<List<RadikoStation>>()))
        .Callback<List<RadikoStation>>(stations =>
        {
            foreach (var station in stations)
            {
                stationDic[station.StationId] = station.StationName;
            }
        });
    configMock.Setup(x => x.ChooseStationName(It.IsAny<RadioServiceKind>(), It.IsAny<string>()))
        .Returns<RadioServiceKind, string>((kind, stationId) =>
            stationDic.TryGetValue(stationId, out var name) ? name : stationId);
    configMock.Setup(x => x.TryGetRadikoCredentialsAsync())
        .Returns(ValueTask.FromResult(
            (!string.IsNullOrWhiteSpace(radikoUserId) && !string.IsNullOrWhiteSpace(radikoPassword),
             radikoUserId,
             radikoPassword)));

    var stationRepository = new InMemoryStationRepository();
    var radikoLogic = new RadikoUniqueProcessLogic(
        NullLogger<RadikoUniqueProcessLogic>.Instance,
        configMock.Object,
        httpClientFactory);
    var radikoApiClient = new RadikoApiClient(
        NullLogger<RadikoApiClient>.Instance,
        configMock.Object,
        httpClientFactory);
    var entryMapper = new EntryMapper(configMock.Object);
    var stationLobLogic = new StationLobLogic(
        NullLogger<StationLobLogic>.Instance,
        configMock.Object,
        radikoApiClient,
        stationRepository,
        radikoLogic,
        httpClientFactory,
        entryMapper);
    var radiruApiClient = new RadiruApiClient(
        NullLogger<RadiruApiClient>.Instance,
        stationLobLogic,
        configMock.Object,
        httpClientFactory);

    var dbOptions = new DbContextOptionsBuilder<RadioDbContext>()
        .UseSqlite($"Data Source={Path.Combine(workRoot, "canary.db")}")
        .Options;
    var dbContext = new RadioDbContext(dbOptions);
    dbContext.Database.EnsureCreated();

    var appContext = new RadioAppContext();
    var programScheduleRepository = new ProgramScheduleRepository(dbContext);
    var entryMapperForSchedule = new EntryMapper(configMock.Object);
    var programScheduleLobLogic = new ProgramScheduleLobLogic(
        NullLogger<ProgramScheduleLobLogic>.Instance,
        appContext,
        radikoApiClient,
        radiruApiClient,
        programScheduleRepository,
        null!,
        entryMapperForSchedule,
        null);

    var inMemoryConfig = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RadiKeep:LogDirectory"] = logRoot
        })
        .Build();
    var ffmpegService = new FfmpegService(
        NullLogger<IFfmpegService>.Instance,
        configMock.Object,
        inMemoryConfig);
    var mediaTranscodeService = new MediaTranscodeService(
        NullLogger<MediaTranscodeService>.Instance,
        ffmpegService,
        configMock.Object,
        httpClientFactory);

    return new LogicContext(
        provider,
        dbContext,
        stationDic,
        radikoLogic,
        radikoApiClient,
        stationLobLogic,
        radiruApiClient,
        programScheduleLobLogic,
        mediaTranscodeService);
}

static async Task<CheckResult> CheckRadikoLoginAsync(
    LogicContext logicContext,
    string userId,
    string password,
    string logPath)
{
    var log = new StringBuilder();
    log.AppendLine("check=C010");

    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
    {
        log.AppendLine("credentials_missing=true");
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C010_RADIKO_LOGIN", "FAIL", "RADIKO credentials are missing.", "E-C010-NO-CREDENTIALS");
    }

    try
    {
        var login = await logicContext.RadikoLogic.LoginRadikoAsync(forceRefresh: true);
        if (!login.IsSuccess)
        {
            log.AppendLine("login_success=false");
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C010_RADIKO_LOGIN", "FAIL", "radiko login failed.", "E-C010-LOGIN");
        }

        log.AppendLine($"login_success=true is_premium={login.IsPremiumUser} is_area_free={login.IsAreaFree}");
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C010_RADIKO_LOGIN", "PASS", "radiko login succeeded.", string.Empty);
    }
    catch (Exception ex)
    {
        log.AppendLine(ex.ToString());
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C010_RADIKO_LOGIN", "FAIL", $"radiko login check failed: {ex.Message}", "E-C010-EXCEPTION");
    }
}

static async Task<CheckResult> CheckRadikoRealtimeRecordingAsync(
    LogicContext logicContext,
    string preferredStationId,
    int recordSeconds,
    string recordOutputDir,
    string logPath)
{
    var log = new StringBuilder();
    log.AppendLine($"check=C003_RADIKO preferred_station={preferredStationId} seconds={recordSeconds}");

    try
    {
        var login = await logicContext.RadikoLogic.LoginRadikoAsync(forceRefresh: true);
        if (!login.IsSuccess || string.IsNullOrWhiteSpace(login.Session))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "radiko login failed.", "E-C003-RADIKO-LOGIN");
        }

        var areaResult = await logicContext.RadikoLogic.GetRadikoAreaAsync(forceRefresh: true);
        if (!areaResult.IsSuccess || string.IsNullOrWhiteSpace(areaResult.Area))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "radiko area detection failed.", "E-C003-RADIKO-AREA");
        }
        var area = areaResult.Area;

        var currentAreaStations = await logicContext.RadikoApiClient.GetStationsByAreaAsync(area);
        if (currentAreaStations.Count == 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "No stations resolved for current area.", "E-C003-RADIKO-STATIONS");
        }

        var stationId = preferredStationId;
        if (!login.IsAreaFree && !currentAreaStations.Contains(preferredStationId, StringComparer.OrdinalIgnoreCase))
        {
            stationId = currentAreaStations[0];
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone());
        var onAirProgram = await FindRadikoNowOnAirProgramAsync(logicContext, stationId, now);
        if (onAirProgram is null && !login.IsAreaFree)
        {
            foreach (var candidateStation in currentAreaStations.Take(5))
            {
                onAirProgram = await FindRadikoNowOnAirProgramAsync(logicContext, candidateStation, now);
                if (onAirProgram is not null)
                {
                    stationId = candidateStation;
                    break;
                }
            }
        }

        if (onAirProgram is null)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "No on-air program found for realtime record.", "E-C003-RADIKO-NO-ONAIR");
        }

        var seededProgramId = await SeedRadikoProgramForRealtimeRecordingAsync(logicContext, stationId, onAirProgram.Value.Title, area, recordSeconds);
        var command = new RecordingCommand(
            RadioServiceKind.Radiko,
            seededProgramId,
            onAirProgram.Value.Title,
            IsTimeFree: false,
            StartDelaySeconds: 0,
            EndDelaySeconds: 0);
        var source = new RadikoRecordingSource(
            NullLogger<RadikoRecordingSource>.Instance,
            logicContext.ProgramScheduleLobLogic,
            logicContext.StationLobLogic,
            logicContext.RadikoLogic,
            logicContext.RadikoApiClient,
            logicContext.DbContext);
        var sourceResult = await source.PrepareAsync(command);

        var outputPath = Path.Combine(recordOutputDir, $"radiko-realtime-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
        var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
        var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

        log.AppendLine($"selected_station={stationId}");
        log.AppendLine($"onair_program={onAirProgram.Value.Title}");
        log.AppendLine($"seed_program_id={seededProgramId}");
        log.AppendLine($"output={outputPath}");
        log.AppendLine($"logic_recorded={recorded}");

        if (!recorded || !File.Exists(outputPath))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", "radiko realtime recording failed.", "E-C003-RADIKO-RECORD");
        }

        var bytes = new FileInfo(outputPath).Length;
        if (bytes < 32 * 1024)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C003-RADIKO-SIZE");
        }

        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C003_RADIKO_REALTIME_RECORD", "PASS", $"radiko realtime recording succeeded. bytes={bytes}", string.Empty);
    }
    catch (Exception ex)
    {
        log.AppendLine(ex.ToString());
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C003_RADIKO_REALTIME_RECORD", "FAIL", $"radiko realtime check failed: {ex.Message}", "E-C003-RADIKO-EXCEPTION");
    }
}

static async Task<CheckResult> CheckRadikoTimeFreeRecordingAsync(
    LogicContext logicContext,
    string preferredStationId,
    int recordSeconds,
    string recordOutputDir,
    string logPath)
{
    var log = new StringBuilder();
    log.AppendLine($"check=C004_RADIKO_TIMEFREE preferred_station={preferredStationId} seconds={recordSeconds}");

    try
    {
        var login = await logicContext.RadikoLogic.LoginRadikoAsync(forceRefresh: true);
        if (!login.IsSuccess || string.IsNullOrWhiteSpace(login.Session))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "radiko login failed.", "E-C004-RADIKO-LOGIN");
        }

        var areaResult = await logicContext.RadikoLogic.GetRadikoAreaAsync(forceRefresh: true);
        if (!areaResult.IsSuccess || string.IsNullOrWhiteSpace(areaResult.Area))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "radiko area detection failed.", "E-C004-RADIKO-AREA");
        }
        var area = areaResult.Area;

        var currentAreaStations = await logicContext.RadikoApiClient.GetStationsByAreaAsync(area);
        if (currentAreaStations.Count == 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "No stations resolved for current area.", "E-C004-RADIKO-STATIONS");
        }

        var stationId = preferredStationId;
        if (!login.IsAreaFree && !currentAreaStations.Contains(preferredStationId, StringComparer.OrdinalIgnoreCase))
        {
            stationId = currentAreaStations[0];
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone());
        var candidate = await FindRadikoTimeFreeCandidateAsync(logicContext, stationId, now, recordSeconds);
        if (candidate is null && !login.IsAreaFree)
        {
            foreach (var candidateStation in currentAreaStations.Take(5))
            {
                candidate = await FindRadikoTimeFreeCandidateAsync(logicContext, candidateStation, now, recordSeconds);
                if (candidate is not null)
                {
                    stationId = candidateStation;
                    break;
                }
            }
        }

        if (candidate is null)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "No timefree candidate program found.", "E-C004-RADIKO-NO-CANDIDATE");
        }

        var seededProgramId = await SeedRadikoProgramForTimeFreeRecordingAsync(
            logicContext,
            stationId,
            candidate.Value.Title,
            area,
            candidate.Value.StartTime,
            candidate.Value.EndTime,
            recordSeconds);
        var command = new RecordingCommand(
            RadioServiceKind.Radiko,
            seededProgramId,
            candidate.Value.Title,
            IsTimeFree: true,
            StartDelaySeconds: 0,
            EndDelaySeconds: 0);
        var source = new RadikoRecordingSource(
            NullLogger<RadikoRecordingSource>.Instance,
            logicContext.ProgramScheduleLobLogic,
            logicContext.StationLobLogic,
            logicContext.RadikoLogic,
            logicContext.RadikoApiClient,
            logicContext.DbContext);
        var sourceResult = await source.PrepareAsync(command);

        var outputPath = Path.Combine(recordOutputDir, $"radiko-timefree-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
        var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
        var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

        log.AppendLine($"selected_station={stationId}");
        log.AppendLine($"timefree_program={candidate.Value.Title}");
        log.AppendLine($"timefree_program_start={candidate.Value.StartTime:O}");
        log.AppendLine($"timefree_program_end={candidate.Value.EndTime:O}");
        log.AppendLine($"seed_program_id={seededProgramId}");
        log.AppendLine($"output={outputPath}");
        log.AppendLine($"logic_recorded={recorded}");

        if (!recorded || !File.Exists(outputPath))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", "radiko timefree recording failed.", "E-C004-RADIKO-RECORD");
        }

        var bytes = new FileInfo(outputPath).Length;
        if (bytes < 32 * 1024)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C004-RADIKO-SIZE");
        }

        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "PASS", $"radiko timefree recording succeeded. bytes={bytes}", string.Empty);
    }
    catch (Exception ex)
    {
        log.AppendLine(ex.ToString());
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C004_RADIKO_TIMEFREE_RECORD", "FAIL", $"radiko timefree check failed: {ex.Message}", "E-C004-RADIKO-EXCEPTION");
    }
}

static async Task<CheckResult> CheckRadiruRealtimeRecordingAsync(
    LogicContext logicContext,
    string areaId,
    string stationId,
    int recordSeconds,
    string recordOutputDir,
    string logPath)
{
    var log = new StringBuilder();
    log.AppendLine($"check=C003_RADIRU area={areaId} station={stationId} seconds={recordSeconds}");

    try
    {
        var normalizedArea = NormalizeRadiruAreaKey(areaId);
        var areaKind = Enum.GetValues<RadiruAreaKind>().First(x => x.GetEnumCodeId() == normalizedArea);
        var stationKind = Enumeration.GetAll<RadiruStationKind>()
            .FirstOrDefault(x => string.Equals(x.ServiceId, stationId, StringComparison.OrdinalIgnoreCase));
        if (stationKind is null)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Radiru station kind not found.", "E-C003-RADIRU-STATION");
        }

        await logicContext.StationLobLogic.UpdateRadiruStationInformationAsync();
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone());
        var programs = await logicContext.RadiruApiClient.GetDailyProgramsAsync(areaKind, stationKind, now);
        var onAirProgram = programs.FirstOrDefault(p => now >= p.StartDate && now <= p.EndDate);

        if (onAirProgram is null)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "No on-air radiru program found.", "E-C003-RADIRU-NO-ONAIR");
        }

        var seededProgramId = await SeedRadiruProgramForRealtimeRecordingAsync(logicContext, normalizedArea, stationKind!, onAirProgram, recordSeconds);
        var command = new RecordingCommand(
            RadioServiceKind.Radiru,
            seededProgramId,
            onAirProgram.Name,
            IsTimeFree: false,
            StartDelaySeconds: 0,
            EndDelaySeconds: 0);
        var source = new RadiruRecordingSource(
            NullLogger<RadiruRecordingSource>.Instance,
            logicContext.ProgramScheduleLobLogic,
            logicContext.StationLobLogic);
        var sourceResult = await source.PrepareAsync(command);

        var outputPath = Path.Combine(recordOutputDir, $"radiru-realtime-{normalizedArea}-{stationId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.m4a");
        var mediaPath = new MediaPath(outputPath, outputPath, Path.GetFileName(outputPath));
        var recorded = await logicContext.MediaTranscodeService.RecordAsync(sourceResult, mediaPath);

        log.AppendLine($"onair_program={onAirProgram.Name}");
        log.AppendLine($"seed_program_id={seededProgramId}");
        log.AppendLine($"output={outputPath}");
        log.AppendLine($"logic_recorded={recorded}");

        if (!recorded || !File.Exists(outputPath))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", "Radiru realtime recording failed.", "E-C003-RADIRU-RECORD");
        }

        var bytes = new FileInfo(outputPath).Length;
        if (bytes < 32 * 1024)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", $"Recorded file too small: {bytes} bytes.", "E-C003-RADIRU-SIZE");
        }

        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C003_RADIRU_REALTIME_RECORD", "PASS", $"Radiru realtime recording succeeded. bytes={bytes}", string.Empty);
    }
    catch (Exception ex)
    {
        log.AppendLine(ex.ToString());
        await File.WriteAllTextAsync(logPath, log.ToString());
        return new CheckResult("C003_RADIRU_REALTIME_RECORD", "FAIL", $"Radiru realtime check failed: {ex.Message}", "E-C003-RADIRU-EXCEPTION");
    }
}

static async Task<(string StationId, string ProgramId, string Title)?> FindRadikoNowOnAirProgramAsync(LogicContext logicContext, string stationId, DateTimeOffset nowJst)
{
    var programs = await logicContext.RadikoApiClient.GetWeeklyProgramsAsync(stationId);
    foreach (var p in programs)
    {
        var startJst = TimeZoneInfo.ConvertTime(p.StartTime, ResolveJapanTimeZone());
        var endJst = TimeZoneInfo.ConvertTime(p.EndTime, ResolveJapanTimeZone());
        if (nowJst >= startJst && nowJst <= endJst)
        {
            return (stationId, p.ProgramId, p.Title);
        }
    }

    return null;
}

static async Task<(string ProgramId, string Title, DateTimeOffset StartTime, DateTimeOffset EndTime)?> FindRadikoTimeFreeCandidateAsync(
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
            StartJst = TimeZoneInfo.ConvertTime(p.StartTime, ResolveJapanTimeZone()),
            EndJst = TimeZoneInfo.ConvertTime(p.EndTime, ResolveJapanTimeZone())
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

static string GetProgramDataLogPath(string logPath)
{
    var logDirectory = Path.GetDirectoryName(logPath) ?? ".";
    var logFileNameWithoutExtension = Path.GetFileNameWithoutExtension(logPath);
    return Path.Combine(logDirectory, $"{logFileNameWithoutExtension}_programs.json");
}

static async Task WriteJsonLogAsync<T>(string path, T payload)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(path, json);
}

static async Task<string> SeedRadikoProgramForRealtimeRecordingAsync(
    LogicContext logicContext,
    string stationId,
    string title,
    string areaId,
    int recordSeconds)
{
    var nowJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone());
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

static async Task<string> SeedRadikoProgramForTimeFreeRecordingAsync(
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

static async Task<string> SeedRadiruProgramForRealtimeRecordingAsync(
    LogicContext logicContext,
    string normalizedAreaId,
    RadiruStationKind stationKind,
    RadiruProgramJsonEntity sourceProgram,
    int recordSeconds)
{
    var nowJst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ResolveJapanTimeZone());
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

static async Task<CheckResult> CheckFfmpegAsync(string logPath)
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

static async Task WriteStatusAsync(string path, CanaryStatus status)
{
    var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(path, json);
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var current = args[i];
        if (!current.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = current[2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[++i]
            : "true";
        map[key] = value;
    }

    return map;
}

static string GetArg(Dictionary<string, string> map, string key, string defaultValue)
    => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;

sealed class LogicContext(
    ServiceProvider serviceProvider,
    RadioDbContext dbContext,
    ConcurrentDictionary<string, string> radikoStationDic,
    RadikoUniqueProcessLogic radikoLogic,
    RadikoApiClient radikoApiClient,
    StationLobLogic stationLobLogic,
    RadiruApiClient radiruApiClient,
    ProgramScheduleLobLogic programScheduleLobLogic,
    MediaTranscodeService mediaTranscodeService) : IDisposable
{
    public RadioDbContext DbContext { get; } = dbContext;
    public ConcurrentDictionary<string, string> RadikoStationDic { get; } = radikoStationDic;
    public RadikoUniqueProcessLogic RadikoLogic { get; } = radikoLogic;
    public RadikoApiClient RadikoApiClient { get; } = radikoApiClient;
    public StationLobLogic StationLobLogic { get; } = stationLobLogic;
    public RadiruApiClient RadiruApiClient { get; } = radiruApiClient;
    public ProgramScheduleLobLogic ProgramScheduleLobLogic { get; } = programScheduleLobLogic;
    public MediaTranscodeService MediaTranscodeService { get; } = mediaTranscodeService;

    public void Dispose()
    {
        DbContext.Dispose();
        serviceProvider.Dispose();
    }
}

sealed class InMemoryStationRepository : IStationRepository
{
    private readonly Dictionary<string, NhkRadiruStation> _radiruStations = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<bool> HasAnyRadikoStationAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask<List<RadikoStation>> GetRadikoStationsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new List<RadikoStation>());

    public ValueTask AddRadikoStationsIfMissingAsync(IEnumerable<RadikoStation> stations, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<bool> HasAnyRadiruStationAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_radiruStations.Count > 0);

    public ValueTask UpsertRadiruStationsAsync(IEnumerable<NhkRadiruStation> stations, CancellationToken cancellationToken = default)
    {
        foreach (var station in stations)
        {
            _radiruStations[station.AreaId] = station;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<NhkRadiruStation> GetRadiruStationByAreaAsync(string areaId, CancellationToken cancellationToken = default)
    {
        if (_radiruStations.TryGetValue(areaId, out var station))
        {
            return ValueTask.FromResult(station);
        }

        throw new InvalidOperationException($"Radiru station not found. areaId={areaId}");
    }
}

file sealed class CanaryStatus
{
    public required string Result { get; init; }
    public required string Message { get; init; }
    public required string TimestampJst { get; init; }
    public required List<CheckResult> Checks { get; init; }
}

file sealed record ProgramSchemaIssue(string ProgramId, string Field, string Reason);
file sealed record ProgramSchemaValidationResult(
    IReadOnlyList<ProgramSchemaIssue> RequiredIssues,
    IReadOnlyDictionary<string, int> OptionalMissingCounts);

file sealed record CheckResult(string CheckId, string Result, string Message, string ErrorCode);
