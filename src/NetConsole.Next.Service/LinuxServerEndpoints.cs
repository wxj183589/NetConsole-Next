namespace NetConsole.Next.Service;

internal static class LinuxServerEndpoints
{
    public static void MapLinuxServers(this WebApplication app)
    {
        var routes = app.MapGroup("/api/system/linux");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var scope = context.RequestServices.GetRequiredService<P2B5SiteScopeStore>().Resolve(Identity(context));
            if (!scope.IsAuthorized)
                return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = "当前用户没有可用的局点权限。" }, statusCode: 403);
            context.Items[P2B5SiteScopeHttpContextKeys.Decision] = scope;
            if (context.Request.RouteValues["id"] is string id &&
                !context.RequestServices.GetRequiredService<Catalog>().LinuxServerIsInScope(id, scope))
                return Results.NotFound();
            return await next(invocation);
        });
        routes.MapGet("/servers", (HttpContext context, Catalog catalog) => Results.Ok(catalog.QueryLinuxServers(Scope(context))));
        routes.MapGet("/groups", (HttpContext context, Catalog catalog) => Results.Ok(catalog.QueryLinuxServers(Scope(context))
            .Select(server => server.GroupName).Where(group => group.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        routes.MapPost("/servers", (HttpContext context, LinuxServerRequest request, Catalog catalog) =>
        {
            if (RequireWrite(context, catalog) is { } denied) return denied;
            try
            {
                DeviceSiteScope.Require(Scope(context), request.SiteId);
                var view = catalog.SaveLinuxServer(null, request);
                Audit(context, catalog, "LINUX_SERVER_CREATED", view.Id);
                return Results.Created($"/api/system/linux/servers/{view.Id}", view);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        routes.MapPut("/servers/{id}", async (HttpContext context, string id, LinuxServerRequest request,
            Catalog catalog, TerminalSessionManager manager) =>
        {
            if (RequireWrite(context, catalog) is { } denied) return denied;
            try
            {
                DeviceSiteScope.Require(Scope(context), request.SiteId);
                var view = await manager.SaveLinuxServerAsync(id, request).ConfigureAwait(false);
                Audit(context, catalog, "LINUX_SERVER_UPDATED", id);
                return Results.Ok(view);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        routes.MapDelete("/servers/{id}", async (HttpContext context, string id, Catalog catalog, TerminalSessionManager manager) =>
        {
            if (RequireWrite(context, catalog) is { } denied) return denied;
            if (!await manager.DeleteLinuxServerAsync(id).ConfigureAwait(false)) return Results.NotFound();
            Audit(context, catalog, "LINUX_SERVER_DELETED", id);
            return Results.Ok(new { deleted = true });
        });
        routes.MapPost("/servers/{id}/test", async (HttpContext context, string id, LinuxServerConnectionTests tests, Catalog catalog) =>
        {
            try
            {
                var result = await tests.TestAsync(id, Scope(context), context.RequestAborted).ConfigureAwait(false);
                Audit(context, catalog, "LINUX_SERVER_CONNECTION_TEST", id);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        routes.MapPost("/servers/{id}/sessions", (HttpContext context, string id, Catalog catalog, TerminalSessionManager manager) =>
        {
            try
            {
                var sessionId = manager.CreateLinux(id, Identity(context).Username, Scope(context));
                Audit(context, catalog, "LINUX_TERMINAL_CREATED", id);
                return Results.Created($"/api/system/terminal/sessions/{sessionId}", new { sessionId, state = "CONNECTING", mode = "LINUX_INTERACTIVE" });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    private static WebUserIdentity Identity(HttpContext context) =>
        context.Items[WebCookieNames.IdentityItem] as WebUserIdentity ?? throw new InvalidOperationException("Authenticated identity is unavailable.");
    private static P2B5SiteScopeDecision Scope(HttpContext context) =>
        (P2B5SiteScopeDecision)context.Items[P2B5SiteScopeHttpContextKeys.Decision]!;
    private static IResult? RequireWrite(HttpContext context, Catalog catalog)
    {
        if (Identity(context).Role == "SUPERADMIN") return null;
        Audit(context, catalog, "LINUX_SERVER_WRITE_DENIED", "linux", "DENIED");
        return Results.Json(new { error = "LINUX_SERVER_WRITE_PERMISSION_DENIED", detail = "需要全局管理员权限。" }, statusCode: 403);
    }
    private static void Audit(HttpContext context, Catalog catalog, string action, string subject, string state = "SUCCEEDED") =>
        catalog.TryRecordDeviceAuditEvent(Identity(context).Username, action, state, subject, null, Array.Empty<string>(), null);
}
