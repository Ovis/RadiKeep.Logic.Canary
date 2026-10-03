namespace Canary.Runner;

/// <summary>
/// 既存のFAILと一時的通信障害によるWARNの判定をまとめる。
/// </summary>
internal static class CheckFailures
{
    internal static CheckResult CreateFailureResult(string checkId, string errorCode, string message, Exception? exception = null)
    {
        var result = IsTransientNetworkFailure(exception, message) ? "WARN" : "FAIL";
        return new CheckResult(checkId, result, message, errorCode);
    }

    internal static bool IsTransientNetworkFailure(Exception? exception, string? message = null)
    {
        var text = $"{message} {exception}".ToLowerInvariant();

        if (text.Contains("timeout") ||
            text.Contains("timed out") ||
            text.Contains("dns") ||
            text.Contains("name or service not known") ||
            text.Contains("temporary failure in name resolution") ||
            text.Contains("no such host") ||
            text.Contains("connection refused") ||
            text.Contains("connection reset") ||
            text.Contains("network is unreachable"))
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException ||
                current is HttpRequestException ||
                current is TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}
