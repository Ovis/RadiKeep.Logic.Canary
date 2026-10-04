using System.Text;
using Canary.Runner.Hosting;

namespace Canary.Runner;

/// <summary>
/// 本体の認証処理で確認専用セッションを取得し、ログアウトを確認する。
/// </summary>
internal static class RadikoLogoutCheck
{
    internal static async Task<CheckResult> CheckRadikoLogoutAsync(LogicContext logicContext, string logPath)
    {
        var log = new StringBuilder();
        log.AppendLine("check=C011");
        CheckResult result;

        try
        {
            var (hasCredentials, userId, password) = await logicContext.Config.TryGetRadikoCredentialsAsync();
            if (!hasCredentials)
            {
                log.AppendLine("credentials_missing=true");
                result = new CheckResult("C011_RADIKO_LOGOUT", "FAIL", "RADIKO credentials are missing.", "E-C011-NO-CREDENTIALS");
            }
            else
            {
                // 録音用の認証キャッシュを使わず、最後のチェックで専用セッションだけを破棄する。
                var login = await logicContext.RadikoLogic.TryLoginWithCredentialsAsync(userId, password);
                log.AppendLine($"login_success={login.IsSuccess}");
                if (!login.IsSuccess)
                {
                    result = new CheckResult("C011_RADIKO_LOGOUT", "FAIL", "Failed to create a session for logout.", "E-C011-LOGIN");
                }
                else
                {
                    var loggedOut = await logicContext.RadikoLogic.LogoutRadikoAsync(login.Session);
                    log.AppendLine($"logout_success={loggedOut}");
                    result = loggedOut
                        ? new CheckResult("C011_RADIKO_LOGOUT", "PASS", "radiko logout succeeded.", string.Empty)
                        : new CheckResult("C011_RADIKO_LOGOUT", "FAIL", "radiko logout failed.", "E-C011-LOGOUT");
                }
            }
        }
        catch (Exception ex)
        {
            // 認証の確認では資格情報やセッションを成果物へ書き出さない。
            log.AppendLine($"exception_type={ex.GetType().Name}");
            result = CheckFailures.CreateFailureResult("C011_RADIKO_LOGOUT", "E-C011-EXCEPTION", "radiko logout check failed.", ex);
        }

        await File.WriteAllTextAsync(logPath, log.ToString());
        return result;
    }
}
