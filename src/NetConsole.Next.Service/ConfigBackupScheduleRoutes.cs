namespace NetConsole.Next.Service;

internal static class ConfigBackupScheduleRoutes
{
    public static void MapConfigBackupScheduleActions(this WebApplication app)
    {
        app.MapPost("/api/system/config-backup-schedules/{id}/run", (HttpContext context, string id,
            ConfigBackupScheduleService schedules, P2B5SiteScopeStore scopes) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Items[WebCookieNames.IdentityItem] is not WebUserIdentity actor || actor.Role != "SUPERADMIN")
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var scope = scopes.Resolve(actor);
            if (!scope.IsAuthorized)
                return Results.Json(new { error = "CONFIG_BACKUP_SCOPE_DENIED" }, statusCode: 403);
            try
            {
                return schedules.RunNow(id, actor, scope) is { } schedule
                    ? Results.Accepted("/api/system/config-backup-schedules", schedule) : Results.NotFound();
            }
            catch (ConfigBackupException ex)
            { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
            catch (TaskScopeRevokedException ex)
            { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        });
    }
}
