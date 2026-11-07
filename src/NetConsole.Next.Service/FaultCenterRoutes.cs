namespace NetConsole.Next.Service;

internal static class FaultCenterRoutes
{
    public static void MapFaultCenter(this WebApplication app)
    {
        var routes = app.MapGroup("/api/system/operations/faults");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            var scope = context.RequestServices.GetRequiredService<P2B5SiteScopeStore>().Resolve(Identity(context));
            if (!scope.IsAuthorized) return Results.Json(new { error = "FAULT_SCOPE_DENIED" }, statusCode: 403);
            context.Items[P2B5SiteScopeHttpContextKeys.Decision] = scope;
            try { return await next(invocation); }
            catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: 403); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex)
            { return Results.Json(new { error = ex.Message.StartsWith("FAULT_", StringComparison.Ordinal) ? ex.Message : "FAULT_OPERATION_FAILED" }, statusCode: ex.Message.Contains("STORE", StringComparison.Ordinal) ? 503 : 409); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return Results.Json(new { error = "FAULT_STORE_UNAVAILABLE" }, statusCode: 503); }
        });
        routes.MapGet("", (HttpContext ctx, FaultCenterService faults, int? page, string? state,
            string? deviceId, string? kind, long? fromMs, long? toMs) =>
            Results.Ok(faults.Query(Identity(ctx), Scope(ctx), page ?? 1, state, deviceId, kind, fromMs, toMs)));
        routes.MapPost("/sync", (HttpContext ctx, FaultCenterService faults, int? page) =>
            Results.Ok(faults.Sync(Identity(ctx), Scope(ctx), page ?? 1)));
        routes.MapPost("/{id}/ack", (string id, HttpContext ctx, FaultCenterService faults) =>
            Results.Ok(faults.Acknowledge(id, Identity(ctx), Scope(ctx))));
        routes.MapPost("/{id}/process", (string id, HttpContext ctx, FaultCenterService faults) =>
            Results.Ok(faults.StartProcessing(id, Identity(ctx), Scope(ctx))));
        routes.MapPost("/{id}/verify", (string id, HttpContext ctx, FaultCenterService faults) =>
            Results.Ok(faults.Verify(id, Identity(ctx), Scope(ctx))));
        routes.MapPut("/policies/{deviceId}", (string deviceId, FaultPolicyRequest request, HttpContext ctx, FaultCenterService faults) =>
            Results.Ok(faults.SetPolicy(deviceId, request, Identity(ctx), Scope(ctx))));
    }
    private static WebUserIdentity Identity(HttpContext context) =>
        context.Items[WebCookieNames.IdentityItem] as WebUserIdentity ?? throw new InvalidOperationException("Authenticated identity unavailable.");
    private static P2B5SiteScopeDecision Scope(HttpContext context) =>
        (P2B5SiteScopeDecision)context.Items[P2B5SiteScopeHttpContextKeys.Decision]!;
}

internal sealed partial class Catalog
{
    public WebUserIdentity? QueryFaultMonitorActor()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,username,role FROM web_users WHERE disabled=0 AND role='SUPERADMIN' ORDER BY id LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), "fault-monitor") : null;
    }
}
