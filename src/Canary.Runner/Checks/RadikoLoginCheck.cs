using Canary.Runner.Hosting;
using System.Text;

namespace Canary.Runner;

/// <summary>
/// 本体の認証処理を使ってradikoログインを確認する。
/// </summary>
internal static class RadikoLoginCheck
{
    internal static async Task<CheckResult> CheckRadikoLoginAsync(
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
}
