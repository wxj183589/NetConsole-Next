namespace NetConsole.Next.Service;

internal static class ConfigurationRestoreReadinessRoutes
{
    public static void MapConfigurationRestoreReadiness(this WebApplication app)
    {
        app.MapGet("/api/system/simulator-config/restore-preflight", (HttpContext context, string profileId,
            SimulatorConfigurationService configurations, P2B5SiteScopeStore scopes) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Items[WebCookieNames.IdentityItem] is not WebUserIdentity actor || actor.Role != "SUPERADMIN")
                return Results.StatusCode(403);
            var scope = scopes.Resolve(actor);
            if (!scope.IsAuthorized) return Results.StatusCode(403);
            try { return Results.Ok(configurations.RestorePreflightHistory(profileId, actor, scope)); }
            catch (ConfigRestoreException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
            catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: 403); }
        });
        app.MapPost("/api/system/simulator-config/restore-preflight", async (HttpContext context,
            ConfigurationRestorePreflightRequest request, SimulatorConfigurationService configurations,
            P2B5SiteScopeStore scopes, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Items[WebCookieNames.IdentityItem] is not WebUserIdentity actor || actor.Role != "SUPERADMIN")
                return Results.StatusCode(403);
            var scope = scopes.Resolve(actor);
            if (!scope.IsAuthorized) return Results.StatusCode(403);
            try { return Results.Ok(await configurations.PreflightRestoresAsync(request, actor, scope, token)); }
            catch (ConfigRestoreException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
            catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: 403); }
        });
    }
}
