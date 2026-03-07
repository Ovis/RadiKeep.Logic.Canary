using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var argsMap = ParseArgs(args);

var statusPath = GetArg(argsMap, "status-json", "results/status.json");
var logDir = GetArg(argsMap, "log-dir", "logs");
var recordOutputDir = GetArg(argsMap, "record-output-dir", "artifacts/recordings");
var radikoStationId = GetArg(argsMap, "radiko-station-id", "TBS");
var radiruAreaId = GetArg(argsMap, "radiru-area-id", "JP13");
var radiruStationId = GetArg(argsMap, "radiru-station-id", "r1");

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
    var c001 = await CheckRadikoDailyFetchAsync(httpClient, radikoStationId, todayJst, Path.Combine(logDir, "C001_RADIKO_DAILY_FETCH.log"));
    checks.Add(c001);

    var c002 = await CheckRadiruDailyFetchAsync(
        httpClient,
        radiruAreaId,
        radiruStationId,
        todayJst,
        Path.Combine(logDir, "C002_RADIRU_DAILY_FETCH.log"));
    checks.Add(c002);

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

static async Task<CheckResult> CheckRadikoDailyFetchAsync(HttpClient httpClient, string stationId, DateTime dateJst, string logPath)
{
    var log = new StringBuilder();
    var url = $"http://radiko.jp/v3/program/station/weekly/{stationId}.xml";
    log.AppendLine($"check=C001 station={stationId} date={dateJst:yyyy-MM-dd}");
    log.AppendLine($"url={url}");

    try
    {
        var response = await GetWithRetryAsync(httpClient, url);
        log.AppendLine($"status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var datePrefix = dateJst.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var programs = doc
            .Descendants("prog")
            .Where(p => (p.Attribute("ft")?.Value ?? string.Empty).StartsWith(datePrefix, StringComparison.Ordinal))
            .ToList();

        log.AppendLine($"program_count={programs.Count}");
        if (programs.Count == 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", "Program list is empty.", "E-C001-EMPTY");
        }

        var invalid = 0;
        foreach (var p in programs)
        {
            var ft = p.Attribute("ft")?.Value ?? string.Empty;
            var to = p.Attribute("to")?.Value ?? string.Empty;
            var title = p.Element("title")?.Value?.Trim() ?? string.Empty;
            var programId = $"{stationId}_{ft}{to}";

            if (string.IsNullOrWhiteSpace(ft) ||
                string.IsNullOrWhiteSpace(to) ||
                string.IsNullOrWhiteSpace(title) ||
                !TryParseRadikoDateTime(ft, out _) ||
                !TryParseRadikoDateTime(to, out _))
            {
                invalid++;
                log.AppendLine($"invalid_program id={programId} ft={ft} to={to} title_empty={string.IsNullOrWhiteSpace(title)}");
            }
        }

        if (invalid > 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C001_RADIKO_DAILY_FETCH", "FAIL", $"Invalid programs found: {invalid}", "E-C001-SCHEMA");
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
    HttpClient httpClient,
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
        const string configUrl = "https://www.nhk.or.jp/radio/config/config_web.xml";
        var configResponse = await GetWithRetryAsync(httpClient, configUrl);
        log.AppendLine($"config_status={(int)configResponse.StatusCode}");
        configResponse.EnsureSuccessStatusCode();

        var configXml = await configResponse.Content.ReadAsStringAsync();
        var configDoc = XDocument.Parse(configXml);
        var dailyTemplate = configDoc.Descendants("url_program_day").FirstOrDefault()?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dailyTemplate))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "url_program_day is missing in config.", "E-C002-SCHEMA");
        }

        var areaData = configDoc
            .Descendants("stream_url")
            .Descendants("data")
            .FirstOrDefault(x => string.Equals(x.Descendants("areakey").FirstOrDefault()?.Value, normalizedAreaKey, StringComparison.OrdinalIgnoreCase));
        var apiKey = areaData?.Descendants("apikey").FirstOrDefault()?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", $"apikey is missing for area {areaId}.", "E-C002-SCHEMA");
        }

        var dailyUrl = ReplaceFirst(dailyTemplate, "{area}", normalizedAreaKey)
            .Replace("{area}", apiKey, StringComparison.Ordinal)
            .Replace("{service}", stationId, StringComparison.Ordinal)
            .Replace("[YYYY-MM-DD]", dateJst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        log.AppendLine($"daily_template={dailyTemplate}");
        log.AppendLine($"resolved_api_key={apiKey}");
        if (dailyUrl.StartsWith("//", StringComparison.Ordinal))
        {
            dailyUrl = "https:" + dailyUrl;
        }
        else if (dailyUrl.StartsWith("/", StringComparison.Ordinal))
        {
            dailyUrl = "https://www.nhk.or.jp" + dailyUrl;
        }
        else if (dailyUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            dailyUrl = "https://" + dailyUrl["http://".Length..];
        }

        log.AppendLine($"daily_url={dailyUrl}");
        var dailyResponse = await GetWithRetryAsync(httpClient, dailyUrl);
        log.AppendLine($"daily_status={(int)dailyResponse.StatusCode}");
        dailyResponse.EnsureSuccessStatusCode();

        var json = await dailyResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var publications = new List<JsonElement>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (prop.Value.TryGetProperty("publication", out var publication) && publication.ValueKind == JsonValueKind.Array)
            {
                publications.AddRange(publication.EnumerateArray());
            }
        }

        log.AppendLine($"program_count={publications.Count}");
        if (publications.Count == 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", "Program list is empty.", "E-C002-EMPTY");
        }

        var invalid = 0;
        foreach (var item in publications)
        {
            var id = ReadString(item, "id");
            var title = ReadString(item, "name");
            var startDate = ReadString(item, "startDate");
            var endDate = ReadString(item, "endDate");

            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(title) ||
                !DateTimeOffset.TryParse(startDate, out _) ||
                !DateTimeOffset.TryParse(endDate, out _))
            {
                invalid++;
                log.AppendLine($"invalid_program id={id} title_empty={string.IsNullOrWhiteSpace(title)} start={startDate} end={endDate}");
            }
        }

        if (invalid > 0)
        {
            await File.WriteAllTextAsync(logPath, log.ToString());
            return new CheckResult("C002_RADIRU_DAILY_FETCH", "FAIL", $"Invalid programs found: {invalid}", "E-C002-SCHEMA");
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

static async Task<HttpResponseMessage> GetWithRetryAsync(HttpClient httpClient, string url)
{
    Exception? last = null;
    for (var i = 0; i < 3; i++)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            request.Headers.AcceptEncoding.ParseAdd("br");
            var response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500 && i < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (i + 1)));
                continue;
            }

            return response;
        }
        catch (Exception ex)
        {
            last = ex;
            if (i < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (i + 1)));
            }
        }
    }

    throw last ?? new InvalidOperationException("HTTP request failed.");
}

static bool TryParseRadikoDateTime(string value, out DateTimeOffset dateTimeOffset)
{
    dateTimeOffset = default;
    if (!DateTime.TryParseExact(
            value,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dateTime))
    {
        return false;
    }

    var jst = ResolveJapanTimeZone();
    var offset = jst.GetUtcOffset(dateTime);
    dateTimeOffset = new DateTimeOffset(dateTime, offset);
    return true;
}

static string ReadString(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var value))
    {
        return string.Empty;
    }

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.ToString(),
        _ => string.Empty
    };
}

static string ReplaceFirst(string text, string search, string replacement)
{
    var index = text.IndexOf(search, StringComparison.Ordinal);
    if (index < 0)
    {
        return text;
    }

    return string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + search.Length));
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

file sealed class CanaryStatus
{
    public required string Result { get; init; }
    public required string Message { get; init; }
    public required string TimestampJst { get; init; }
    public required List<CheckResult> Checks { get; init; }
}

file sealed record CheckResult(string CheckId, string Result, string Message, string ErrorCode);
