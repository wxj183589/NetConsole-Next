using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed class InteractiveScopeAuthorizer(WebAuthStore auth, P2B5SiteScopeStore scopes, Catalog catalog)
{
    public WebUserIdentity? Current(WebUserIdentity original)
    {
        try
        {
            // This principal is constructed only by the local bearer middleware.
            // No bearer token or Cookie is retained by a background job.
            if (original.UserId == "local-api-token" && original.SessionId == "local-api-token")
                return new(original.UserId, original.Username, "SUPERADMIN", original.SessionId);
            return auth.ReadActiveSessionIdentity(original.UserId, original.SessionId);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    public bool CanUseGlobalFiles(string userId, string sessionId) =>
        Current(new(userId, "", "", sessionId)) is { Role: "SUPERADMIN" };

    public bool CanUseTerminal(WebUserIdentity original, string sessionId)
    {
        var current = Current(original);
        if (current is null) return false;
        try { return catalog.TerminalSessionIsInScope(sessionId, scopes.Resolve(current)); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    public IResult? RequireGlobalFiles(HttpContext context)
    {
        var original = context.Items[WebCookieNames.IdentityItem] as WebUserIdentity;
        return original is not null && CanUseGlobalFiles(original.UserId, original.SessionId) ? null :
            Results.Json(new { error = "GLOBAL_FILE_SCOPE_REQUIRED", detail = "当前文件管理包含跨局点数据，需要全局管理员权限。局点用户请从设备或终端业务入口导出。" },
                statusCode: StatusCodes.Status403Forbidden);
    }
}
