using Microsoft.Extensions.Hosting;
using NetConsole.Next.Service;
using NetConsole.Next.Credentials;
using System.Security.Cryptography;
using System.Text;
using System.Net.WebSockets;
using System.Text.Json;
using System.IO.Ports;

using var singleInstance = SingleInstanceGuard.Acquire();
var settings = RuntimeSettings.FromEnvironment();
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "NetConsoleNext");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
var webBind = settings.WebBindAddress.IndexOf(':') >= 0 ? $"[{settings.WebBindAddress}]" : settings.WebBindAddress;
builder.WebHost.UseUrls($"http://{webBind}:{settings.WebPort}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<P2B3ReadRepository>();
builder.Services.AddSingleton<P2B4WriteRepository>();
builder.Services.AddSingleton<P2B5SiteScopeStore>();
builder.Services.AddSingleton<TaskSiteScopeAuthorizer>();
builder.Services.AddSingleton<ITaskScopeAuthorizer>(sp => sp.GetRequiredService<TaskSiteScopeAuthorizer>());
builder.Services.AddSingleton<WirelessReadModelService>();
builder.Services.AddSingleton<H3cCapabilityService>();
builder.Services.AddSingleton<ZteCollectionService>();
builder.Services.AddSingleton<P2B5ExportService>();
builder.Services.AddSingleton<P2B5RailReferenceService>();
builder.Services.AddSingleton<WebAuthStore>();
builder.Services.AddSingleton<InteractiveScopeAuthorizer>();
builder.Services.AddSingleton<IRawSegmentWriterFactory, RawSegmentWriterFactory>();
builder.Services.AddSingleton<IH3cCredentialProvider>(sp =>
    builder.Environment.IsDevelopment() && settings.H3cDevelopmentEnvironmentCredentialsEnabled
        ? new EnvironmentH3cCredentialProvider(enabled: true)
        : new DeviceRecordH3cCredentialProvider(sp.GetRequiredService<Catalog>()));
builder.Services.AddSingleton<H3cConnectionTestService>();
builder.Services.AddSingleton<NetmikoJumpHostProbe>();
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddSingleton<LinuxServerConnectionTests>();
builder.Services.AddSingleton<TerminalTicketStore>();
builder.Services.AddSingleton<TerminalRecordingService>();
builder.Services.AddSingleton<TerminalExportService>();
builder.Services.AddSingleton<DesktopHelperIpc>();
builder.Services.AddSingleton<HostToolRegistry>();
builder.Services.AddSingleton<HostToolLaunchService>();
builder.Services.AddSingleton<FileManagerService>();
builder.Services.AddSingleton<IArchiveDiskSpaceProvider, DriveInfoArchiveDiskSpaceProvider>();
builder.Services.AddSingleton(sp => new FileArchiveService(sp.GetRequiredService<FileManagerService>(),
    sp.GetRequiredService<RuntimeSettings>(), sp.GetRequiredService<IArchiveDiskSpaceProvider>(),
    sp.GetRequiredService<InteractiveScopeAuthorizer>().CanUseGlobalFiles));
builder.Services.AddSingleton<IDeviceSessionFactory, H3cSshDeviceSessionFactory>();
builder.Services.AddSingleton<ITaskResultSink>(sp => new H3cTaskRawResultSink(
    sp.GetRequiredService<Catalog>(), ex =>
    {
        sp.GetRequiredService<Catalog>().MarkUnavailable(ex);
        sp.GetRequiredService<RuntimeStatus>().SetFaulted($"H3C_RAW_PERSISTENCE_FAILED: {ex.GetType().Name}");
    }));
builder.Services.AddSingleton(sp => new BoundedCollectionScheduler(
    sp.GetRequiredService<IDeviceSessionFactory>(), maxConcurrency: 2, queueCapacity: 128,
    sp.GetRequiredService<Catalog>(), ex =>
    {
        sp.GetRequiredService<Catalog>().MarkUnavailable(ex);
        sp.GetRequiredService<RuntimeStatus>().SetFaulted($"TASK_STATE_PERSISTENCE_FAILED: {ex.GetType().Name}");
    }, sp.GetRequiredService<ITaskResultSink>(), sp.GetRequiredService<ITaskScopeAuthorizer>()));
builder.Services.AddSingleton<H3cBatchTaskService>();
builder.Services.AddSingleton<DeviceConnectionTestService>();
builder.Services.AddSingleton<ConfigBackupService>();
builder.Services.AddSingleton<ConfigBackupScheduleService>();
builder.Services.AddSingleton<NetworkDiagnosticsService>();
builder.Services.AddSingleton<IngressStats>();
builder.Services.AddSingleton<RuntimeStatus>();
builder.Services.AddSingleton<RuntimeResources>();
builder.Services.AddSingleton<FileLogDiagnostics>();
builder.Services.AddHostedService<ServiceRunLifecycle>();
builder.Services.AddHostedService<ProcessResourceMonitor>();
builder.Services.AddHostedService<UdpSyslogWorker>();
builder.Services.AddHostedService<RawIntegrityAuditService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileArchiveService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfigBackupScheduleService>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (context, next) =>
{
    var isSystemApi = context.Request.Path.StartsWithSegments("/api/system");
    var isAuthApi = context.Request.Path.StartsWithSegments("/api/auth");
    if (!isSystemApi && !isAuthApi)
    {
        await next();
        return;
    }

    var remote = context.Connection.RemoteIpAddress;
    var requestHost = context.Request.Host;
    var validHost = IsAllowedWebHost(requestHost, settings);
    var validRemote = remote is not null && System.Net.IPAddress.IsLoopback(remote);
    if (!validRemote || !validHost)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (string.Equals(context.Request.Path.Value, "/api/system/health", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(context.Request.Path.Value, "/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(context.Request.Path.Value, "/api/auth/csrf", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(context.Request.Path.Value, "/api/auth/me", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var authStore = context.RequestServices.GetRequiredService<WebAuthStore>();
    var bearer = context.Request.Headers.Authorization.ToString();
    WebUserIdentity? identity = null;
    if (bearer.StartsWith("Bearer ", StringComparison.Ordinal))
    {
        string expectedToken;
        try { expectedToken = LocalApiTokenStore.ReadExisting(settings.DataRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        var presented = bearer[7..];
        var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        if (CryptographicOperations.FixedTimeEquals(expectedDigest, presentedDigest))
            identity = new WebUserIdentity("local-api-token", "local-api-token", "SUPERADMIN", "local-api-token");
    }
    else identity = authStore.ValidateSession(context.Request.Cookies[WebCookieNames.Session]);
    if (identity is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (isSystemApi) context.Response.Headers["WWW-Authenticate"] = "Bearer";
        return;
    }
    context.Items[WebCookieNames.IdentityItem] = identity;
    var moduleKey = ModuleRegistryRouting.ResolveApiModule(context.Request.Path);
    if (moduleKey is not null)
    {
        try
        {
            if (!context.RequestServices.GetRequiredService<Catalog>().IsModuleEnabled(moduleKey))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
    }
    if (!bearer.StartsWith("Bearer ", StringComparison.Ordinal) && IsWriteRequest(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var csrfHeader = context.Request.Headers[WebCookieNames.CsrfHeader].ToString();
        var csrfCookie = context.Request.Cookies[WebCookieNames.Csrf];
        if (!string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(csrfCookie) || !string.Equals(csrfHeader, csrfCookie, StringComparison.Ordinal) ||
            !authStore.ValidateCsrf(context.Request.Cookies[WebCookieNames.Session] ?? string.Empty, csrfHeader))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next();
});
var catalog = app.Services.GetRequiredService<Catalog>();
var runtimeStatus = app.Services.GetRequiredService<RuntimeStatus>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { app.Services.GetRequiredService<TerminalSessionManager>().DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    catch { }
    try { app.Services.GetRequiredService<ZteCollectionService>().DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    catch { }
    try { app.Services.GetRequiredService<NetworkDiagnosticsService>().Dispose(); }
    catch { }
});
RollingFileLoggerProvider? fileLoggerProvider = null;
try
{
    catalog.Initialize();
    if (P2B4WriteFeatureGate.EnvironmentEnabled())
    {
        try { app.Services.GetRequiredService<P2B4WriteRepository>().RecoverInterruptedOperations(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or P2B4WriteException)
        { app.Logger.LogWarning(ex, "P2B4 operation recovery was skipped; the isolated write boundary remains unavailable."); }
    }
    catalog.BeginRawRecovery();
    try { catalog.ProbeRawWritable(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        runtimeStatus.SetFaulted("RAW_WRITE_UNAVAILABLE: new Raw data cannot be safely persisted.");
        app.Logger.LogCritical(ex, "Raw data directory is not writable; collection will not start.");
    }

    fileLoggerProvider = new RollingFileLoggerProvider(settings.DataRoot,
        app.Services.GetRequiredService<FileLogDiagnostics>());
    app.Services.GetRequiredService<ILoggerFactory>().AddProvider(fileLoggerProvider);
    app.Lifetime.ApplicationStopped.Register(fileLoggerProvider.Dispose);

    var recovery = catalog.RecoverRawSegments();
    runtimeStatus.AssessRecovery(recovery, catalog.GetUnresolvedIntegrityIssueCount());
    runtimeStatus.SetInterruptedTaskRuns(catalog.InterruptedTaskRuns);
    if (!recovery.Issues.Any(issue => issue.Critical)) catalog.CompleteRawRecovery();
    if (recovery.FailedFiles > 0)
        app.Logger.LogWarning("Raw recovery left {FailedFiles} file(s) untouched; inspect recovery.issues and raw_integrity_audits.", recovery.FailedFiles);
    app.Logger.LogInformation("Raw recovery scanned {ScannedFiles} file(s), skipped {TrustedFiles} trusted file(s), validated {BytesRead} bytes and hashed {BytesHashed} bytes, recovered {OpenFiles} open file(s), repaired {RepairedFiles} tail(s), found {MissingFiles} missing file(s), in {ElapsedMs} ms.",
        recovery.ScannedFiles, recovery.TrustedFilesSkipped, recovery.BytesRead, recovery.BytesHashed,
        recovery.RecoveredOpenFiles, recovery.RepairedTailFiles, recovery.MissingFiles, recovery.ElapsedMs);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
{
    catalog.MarkUnavailable(ex);
    runtimeStatus.SetFaulted($"STORAGE_STARTUP_FAILED: {ex.GetType().Name}");
    app.Logger.LogCritical(ex, "Isolated storage could not be initialized or reconciled; collection is disabled.");
}

var processStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
app.MapPost("/api/auth/login", (HttpContext context, WebAuthStore auth, WebLoginRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        return Results.BadRequest(new { error = "Username and password are required." });
    var result = auth.TryLogin(request.Username, request.Password, context.Connection.RemoteIpAddress?.ToString(), out var rateLimited);
    if (rateLimited) return Results.Problem("Too many failed login attempts.", statusCode: StatusCodes.Status429TooManyRequests);
    if (result is null) return Results.Unauthorized();
    SetWebCookies(context, result.SessionToken, result.CsrfToken);
    return Results.Ok(new { user = new { username = result.User.Username, role = result.User.Role }, csrfToken = result.CsrfToken });
});
app.MapGet("/api/auth/me", (HttpContext context, WebAuthStore auth) =>
{
    var identity = auth.ValidateSession(context.Request.Cookies[WebCookieNames.Session]);
    return identity is null ? Results.Unauthorized() : Results.Ok(new { username = identity.Username, role = identity.Role });
});
app.MapGet("/api/auth/csrf", (HttpContext context, WebAuthStore auth) =>
{
    var sessionToken = context.Request.Cookies[WebCookieNames.Session];
    var csrf = auth.GetOrCreateCsrf(sessionToken, context.Request.Cookies[WebCookieNames.Csrf]);
    if (csrf is null) return Results.Unauthorized();
    context.Response.Cookies.Append(WebCookieNames.Csrf, csrf, WebCookieOptions(httpOnly: false));
    return Results.Ok(new { csrfToken = csrf });
});
app.MapPost("/api/auth/logout", (HttpContext context, WebAuthStore auth) =>
{
    auth.RevokeSession(context.Request.Cookies[WebCookieNames.Session]);
    context.Response.Cookies.Delete(WebCookieNames.Session, WebCookieOptions(httpOnly: true));
    context.Response.Cookies.Delete(WebCookieNames.Csrf, WebCookieOptions(httpOnly: false));
    return Results.Ok(new { loggedOut = true });
});
app.MapPost("/api/auth/password", (HttpContext context, WebAuthStore auth, WebPasswordRequest request) =>
{
    var sessionToken = context.Request.Cookies[WebCookieNames.Session];
    var identity = auth.ValidateSession(sessionToken);
    if (identity is null) return Results.Unauthorized();
    if (!IsValidCookieWrite(context, auth, sessionToken)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (request.NewPassword.Length < 8) return Results.BadRequest(new { error = "New password must contain at least 8 characters." });
    var target = string.IsNullOrWhiteSpace(request.Username) ? identity.Username : request.Username.Trim();
    if (!auth.ChangePassword(identity, target, request.CurrentPassword, request.NewPassword))
        return Results.BadRequest(new { error = "Current password is invalid or the user is not permitted." });
    return Results.Ok(new { updated = true });
});
app.MapGet("/api/system/health", (RuntimeSettings runtime, IngressStats ingress, Catalog rawCatalog,
    RuntimeStatus status, RuntimeResources resources, FileLogDiagnostics logDiagnostics, WebAuthStore auth) =>
{
    var resource = resources.Snapshot;
    CatalogHealthSnapshot? storage = null;
    if (rawCatalog.Available)
    {
        try { storage = rawCatalog.GetHealthSnapshot(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            status.SetFaulted($"SQLITE_UNAVAILABLE: {ex.GetType().Name}");
        }
    }
    var healthState = logDiagnostics.WriteFailures > 0 && status.HealthState == "HEALTHY" ? "DEGRADED" : status.HealthState;
    return Results.Ok(new
    {
        status = status.ServiceState,
        serviceState = status.ServiceState,
        healthState,
        healthError = status.HealthError ?? rawCatalog.StorageFailure,
        processId = Environment.ProcessId,
        startedAtMs = processStartedAt,
        syslogEnabled = runtime.SyslogPort > 0,
        h3cCollectionEnabled = runtime.H3cEnabled,
        collectionState = status.CollectionState,
        collectionError = status.CollectionError,
        h3cMetrics = H3cCollectionMetrics.Snapshot(),
        udpReceived = ingress.Received,
        udpAccepted = ingress.Accepted,
        udpDropped = ingress.Dropped,
        udpQueueDepth = ingress.QueueDepth,
        udpQueueHighWatermark = ingress.QueueHighWatermark,
        udpQueueCapacity = UdpSyslogWorker.QueueCapacity,
        udpQueueBytes = ingress.QueueBytes,
        udpQueueBytesHighWatermark = ingress.QueueBytesHighWatermark,
        udpQueueByteCapacity = UdpSyslogWorker.QueueByteCapacity,
        udpSocketReceiveBufferRequestedBytes = ingress.RequestedSocketReceiveBufferBytes,
        udpSocketReceiveBufferEffectiveBytes = ingress.EffectiveSocketReceiveBufferBytes,
        rawFlushedRecords = ingress.Flushed,
        rawWriteLatency = ingress.RawFlushLatency,
        rawFileSyncLatency = ingress.RawFlushLatency,
        rawSegmentSealLatency = ingress.SegmentSealLatency,
        sqliteSegmentRegistrationLatency = ingress.CatalogWriteLatency,
        lastReceivedMs = ingress.LastReceivedMs == 0 ? (long?)null : ingress.LastReceivedMs,
        lastPersistedMs = ingress.LastPersistedMs == 0 ? (long?)null : ingress.LastPersistedMs,
        openSegmentsOnDisk = rawCatalog.OpenSegmentCount(),
        rawSegments = storage?.RawSegments,
        sqliteIntegrityAtStartup = rawCatalog.IntegrityStatus,
        rawWriteAvailable = rawCatalog.RawWriteAvailable,
        logWriteFailures = logDiagnostics.WriteFailures,
        recovery = rawCatalog.RecoverySummary,
        interruptedTaskRunsAtStartup = rawCatalog.InterruptedTaskRuns,
        integrityAuditCount = storage?.IntegrityAuditCount,
        unresolvedIntegrityIssues = storage?.UnresolvedIntegrityIssues,
        recentIntegrityAudits = storage?.RecentIntegrityAudits,
        resources = new
        {
            processCpuPercent = resource.ProcessCpuPercent,
            resource.WorkingSetBytes,
            resource.ThreadCount,
            resource.HandleCount,
            resource.DataVolumeFreeBytes,
            resource.SampledAtMs
        }
        ,instanceId = auth.InstanceIdOrUnknown
    });
});
app.MapGet("/api/system/modules", (Catalog catalog) => Results.Ok(catalog.ListModuleStates()));
app.MapPut("/api/system/modules/{key}", (HttpContext context, string key, ModuleStateWriteRequest request, Catalog catalog) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(catalog.SetModuleEnabled(key, request.Enabled, CurrentIdentity(context).Username)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/device-sites", (HttpContext context, Catalog rawCatalog) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    return Results.Ok(rawCatalog.QueryDeviceSites(CurrentSiteScope(context)));
});
app.MapGet("/api/system/devices", (HttpContext context, HttpRequest request, Catalog rawCatalog) =>
{
    if (DeviceSiteScopeGuard(context, QueryValue(request, "siteId")) is { } denied) return denied;
    var page = int.TryParse(request.Query["page"], out var requestedPage) ? requestedPage : 1;
    var pageSize = int.TryParse(request.Query["pageSize"], out var requestedPageSize) ? requestedPageSize : 20;
    return Results.Ok(rawCatalog.QueryManagedDeviceInventory(page, pageSize,
        request.Query["search"].ToString(), request.Query["status"].ToString(),
        request.Query["siteId"].ToString(), request.Query["groupId"].ToString(),
        request.Query["vendor"].ToString(), request.Query["deviceType"].ToString(), CurrentSiteScope(context)));
});
app.MapPost("/api/system/devices/batch", (HttpContext context, DeviceBatchOperationRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED");
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.CreateDeviceBatchOperation(request, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_BATCH_SCOPE_DENIED", ex.Code); }
    catch (DeviceBatchResourceLimitException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_FAILED", "FAILED", "devices", null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_FAILED", "CONFLICT", "devices", null, Array.Empty<string>(), "IDEMPOTENCY_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/device-operations/{id}", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try { return rawCatalog.GetDeviceBatchOperation(id, CurrentIdentity(context).Username, CurrentSiteScope(context)) is { } operation ? Results.Ok(operation) : Results.NotFound(); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_BATCH_SCOPE_DENIED", ex.Code); }
    catch (UnauthorizedAccessException) { return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id); }
});
app.MapPost("/api/system/devices/batch/{id}/confirm", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.ConfirmDeviceBatchOperation(id, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_BATCH_SCOPE_DENIED", ex.Code); }
    catch (DeviceBatchConflictException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_CONFLICT", "CONFLICT", id, id, Array.Empty<string>(), "DEVICE_VERSION_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message, operationId = ex.OperationId }); }
    catch (DeviceBatchResourceLimitException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (UnauthorizedAccessException) { return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices/batch/{id}/cancel", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.CancelDeviceBatchOperation(id, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_BATCH_SCOPE_DENIED", ex.Code); }
    catch (UnauthorizedAccessException) { return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_CANCEL_FAILED", "CONFLICT", id, id, Array.Empty<string>(), "CANCEL_NOT_ALLOWED"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/devices/{id}", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    return rawCatalog.GetDeviceInventoryDetail(id, CurrentSiteScope(context)) is { } detail ? Results.Ok(detail) : Results.NotFound();
});
app.MapGet("/api/system/devices/{id}/addresses", (HttpContext context, HttpRequest request, string id, P2B3ReadRepository repository) =>
{
    if (P2B3ReadGuard(context) is { } denied) return denied;
    try
    {
        if (!repository.DeviceExists(id)) return Results.NotFound();
        var page = P2B3PageOptions.From(request);
        var result = repository.ReadAddresses(id, page, QueryValue(request, "domainId"),
            OptionalEnum(request, "sourceKind", "PLANNED", "OBSERVED", "IMPORTED", "MANUAL"),
            OptionalEnum(request, "addressFamily", "IPV4", "IPV6"), QueryValue(request, "lifecycle", "ACTIVE"),
            CurrentSiteScope(context));
        return Results.Ok(result);
    }
    catch (P2B3QueryException ex) { return P2B3BadQuery(ex.Message); }
    catch (P2B3SchemaUnavailableException ex) { return P2B3SchemaUnavailable(ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    { return P2B3SchemaUnavailable("P2B_SCHEMA_UNAVAILABLE"); }
});
app.MapGet("/api/system/devices/{id}/network-memberships", (HttpContext context, HttpRequest request, string id, P2B3ReadRepository repository) =>
{
    if (P2B3ReadGuard(context) is { } denied) return denied;
    try
    {
        if (!repository.DeviceExists(id)) return Results.NotFound();
        var page = P2B3PageOptions.From(request);
        return Results.Ok(repository.ReadMemberships(id, page, QueryValue(request, "siteId"),
            QueryValue(request, "domainId"), QueryValue(request, "lifecycle", "ACTIVE"), CurrentSiteScope(context)));
    }
    catch (P2B3QueryException ex) { return P2B3BadQuery(ex.Message); }
    catch (P2B3SchemaUnavailableException ex) { return P2B3SchemaUnavailable(ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    { return P2B3SchemaUnavailable("P2B_SCHEMA_UNAVAILABLE"); }
});
app.MapGet("/api/system/network-domains", (HttpContext context, HttpRequest request, P2B3ReadRepository repository) =>
{
    if (P2B3ReadGuard(context) is { } denied) return denied;
    try
    {
        var page = P2B3PageOptions.From(request);
        return Results.Ok(repository.ReadDomains(page, QueryValue(request, "siteId"), QueryValue(request, "stationId"),
            QueryValue(request, "lineId"), QueryValue(request, "sectionId"),
            OptionalEnum(request, "domainKind", "MANAGEMENT", "BUSINESS", "OUT_OF_BAND", "TRAIN_GROUND", "SHARED_SERVICE", "OTHER"),
            OptionalEnum(request, "scopeResolution", "RESOLVED", "UNRESOLVED", "CONFLICT"),
            QueryValue(request, "lifecycle", "ACTIVE"), QueryValue(request, "search"), CurrentSiteScope(context)));
    }
    catch (P2B3QueryException ex) { return P2B3BadQuery(ex.Message); }
    catch (P2B3SchemaUnavailableException ex) { return P2B3SchemaUnavailable(ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    { return P2B3SchemaUnavailable("P2B_SCHEMA_UNAVAILABLE"); }
});
app.MapGet("/api/system/vlan-plans", (HttpContext context, HttpRequest request, P2B3ReadRepository repository) =>
{
    if (P2B3ReadGuard(context) is { } denied) return denied;
    try
    {
        int? vlanId = null;
        if (!string.IsNullOrWhiteSpace(request.Query["vlanId"]))
        {
            if (!int.TryParse(request.Query["vlanId"], out var parsed) || parsed is < 1 or > 4094)
                throw new P2B3QueryException("vlanId must be between 1 and 4094.");
            vlanId = parsed;
        }
        return Results.Ok(repository.ReadVlanPlans(P2B3PageOptions.From(request), QueryValue(request, "domainId"),
            QueryValue(request, "siteId"), OptionalEnum(request, "sourceKind", "PLANNED", "OBSERVED", "IMPORTED", "MANUAL"),
            OptionalEnum(request, "scopeResolution", "RESOLVED", "UNRESOLVED", "CONFLICT"),
            QueryValue(request, "lifecycle", "ACTIVE"), vlanId, AsOf(request), CurrentSiteScope(context)));
    }
    catch (P2B3QueryException ex) { return P2B3BadQuery(ex.Message); }
    catch (P2B3SchemaUnavailableException ex) { return P2B3SchemaUnavailable(ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    { return P2B3SchemaUnavailable("P2B_SCHEMA_UNAVAILABLE"); }
});
app.MapGet("/api/system/devices/{id}/network-diff", (HttpContext context, HttpRequest request, string id, P2B3ReadRepository repository) =>
{
    if (P2B3ReadGuard(context) is { } denied) return denied;
    try
    {
        if (!repository.DeviceExists(id)) return Results.NotFound();
        return Results.Ok(repository.ReadNetworkDiff(id, QueryValue(request, "domainId"),
            OptionalEnum(request, "resolutionState", "RESOLVED", "PENDING_DOMAIN", "CONFLICT"), AsOf(request),
            CurrentSiteScope(context)));
    }
    catch (P2B3QueryException ex) { return P2B3BadQuery(ex.Message); }
    catch (P2B3SchemaUnavailableException ex) { return P2B3SchemaUnavailable(ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    { return P2B3SchemaUnavailable("P2B_SCHEMA_UNAVAILABLE"); }
});
app.MapPost("/api/system/devices/{id}/addresses", (HttpContext context, string id, P2B4AddressWriteRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Created($"/api/system/devices/{id}/addresses", repository.CreateAddress(CurrentIdentity(context).Username, id, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPut("/api/system/devices/{id}/addresses/{addressId}", (HttpContext context, string id, string addressId, P2B4AddressWriteRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.UpdateAddress(CurrentIdentity(context).Username, id, addressId, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/devices/{id}/network-memberships", (HttpContext context, string id, P2B4MembershipWriteRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Created($"/api/system/devices/{id}/network-memberships", repository.CreateMembership(CurrentIdentity(context).Username, id, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/vlan-plans", (HttpContext context, P2B4VlanPlanWriteRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Created("/api/system/vlan-plans", repository.CreateVlanPlan(CurrentIdentity(context).Username, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPut("/api/system/vlan-plans/{id}", (HttpContext context, string id, P2B4VlanPlanWriteRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.UpdateVlanPlan(CurrentIdentity(context).Username, id, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/network-reviews", (HttpContext context, P2B4ReviewRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Created("/api/system/network-reviews", repository.CreateReview(CurrentIdentity(context).Username, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/import-runs", (HttpContext context, P2B4ImportRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Created("/api/system/import-runs", repository.CreateImport(CurrentIdentity(context).Username, request)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
    catch (Exception exception) { app.Logger.LogError(exception, "P2B4 import preview failed unexpectedly."); return Results.Json(new { error = "P2B4_WRITE_FAILED", detail = "Unexpected isolated write failure." }, statusCode: 500); }
});
app.MapGet("/api/system/import-runs/{id}", (HttpContext context, string id, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.GetImport(CurrentIdentity(context).Username, id)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/import-runs/{id}/dry-run", (HttpContext context, string id, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.DryRunImport(CurrentIdentity(context).Username, id)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/import-runs/{id}/confirm", (HttpContext context, string id, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.ConfirmImport(CurrentIdentity(context).Username, id)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
    catch (Exception exception) { app.Logger.LogError(exception, "P2B4 import confirm failed unexpectedly."); return Results.Json(new { error = "P2B4_WRITE_FAILED", detail = "Unexpected isolated write failure." }, statusCode: 500); }
});
app.MapPost("/api/system/import-runs/{id}/cancel", (HttpContext context, string id, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.CancelImport(CurrentIdentity(context).Username, id)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapGet("/api/system/p2b4-operations/{id}", (HttpContext context, string id, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return repository.GetOperation(CurrentIdentity(context).Username, id) is { } operation ? Results.Ok(operation) : Results.NotFound(); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapGet("/api/system/p2b4-operations", (HttpContext context, HttpRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.ListOperations(CurrentIdentity(context).Username, P2B4QueryLimit(request))); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapGet("/api/system/network-reviews", (HttpContext context, HttpRequest request, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.ListReviews(CurrentIdentity(context).Username, QueryValue(request, "targetType"), QueryValue(request, "targetId"), P2B4QueryLimit(request))); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapGet("/api/system/devices/{id}/addresses/{addressId}/history", (HttpContext context, string id, string addressId, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.ReadAddressHistory(CurrentIdentity(context).Username, id, addressId)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapGet("/api/system/vlan-plans/{id}/history", (HttpContext context, string id, P2B4WriteRepository repository) =>
{
    if (P2B4WriteGuard(context, repository) is { } denied) return denied;
    try { return Results.Ok(repository.ReadVlanHistory(CurrentIdentity(context).Username, id)); }
    catch (P2B4WriteException exception) { return P2B4WriteError(exception); }
});
app.MapPost("/api/system/network-planning/exports", (HttpContext context, P2B5ExportRequest request, P2B5ExportService exports) =>
{
    if (P2B5ExportGuard(context, exports) is { } denied) return denied;
    try { var status = exports.Create(CurrentIdentity(context), request); return Results.Created($"/api/system/network-planning/exports/{status.Id}", status); }
    catch (P2B5ExportException exception) { return P2B5ExportError(exception); }
});
app.MapGet("/api/system/network-planning/exports/{id}", (HttpContext context, string id, P2B5ExportService exports) =>
{
    if (P2B5ExportGuard(context, exports) is { } denied) return denied;
    try { return exports.Get(CurrentIdentity(context), id) is { } status ? Results.Ok(status) : Results.NotFound(); }
    catch (P2B5ExportException exception) { return P2B5ExportError(exception); }
});
app.MapGet("/api/system/network-planning/exports/{id}/download", (HttpContext context, string id, P2B5ExportService exports) =>
{
    if (P2B5ExportGuard(context, exports) is { } denied) return denied;
    try
    {
        var download = exports.OpenDownload(CurrentIdentity(context), id);
        return download is null ? Results.NotFound() : Results.File(download.Stream, download.ContentType, download.Name);
    }
    catch (P2B5ExportException exception) { return P2B5ExportError(exception); }
});
app.MapGet("/api/system/rail-reference/entities", (HttpContext context, HttpRequest request, P2B5RailReferenceService rail) =>
{
    if (P2B5RailReferenceGuard(context, rail) is { } denied) return denied;
    try { return Results.Ok(rail.ListEntities(QueryValue(request, "scope"), QueryValue(request, "type"))); }
    catch (P2B5RailReferenceException exception) { return P2B5RailReferenceError(exception); }
});
app.MapPost("/api/system/rail-reference/imports", (HttpContext context, P2B5RailReferenceImportRequest request, P2B5RailReferenceService rail) =>
{
    if (P2B5RailReferenceGuard(context, rail) is { } denied) return denied;
    try { var status = rail.Preview(CurrentIdentity(context), request); return Results.Created($"/api/system/rail-reference/imports/{status.Id}", status); }
    catch (P2B5RailReferenceException exception) { return P2B5RailReferenceError(exception); }
});
app.MapGet("/api/system/rail-reference/imports/{id}", (HttpContext context, string id, P2B5RailReferenceService rail) =>
{
    if (P2B5RailReferenceGuard(context, rail) is { } denied) return denied;
    try { return rail.Get(CurrentIdentity(context), id) is { } status ? Results.Ok(status) : Results.NotFound(); }
    catch (P2B5RailReferenceException exception) { return P2B5RailReferenceError(exception); }
});
app.MapPost("/api/system/rail-reference/imports/{id}/confirm", (HttpContext context, string id, P2B5RailReferenceService rail) =>
{
    if (P2B5RailReferenceGuard(context, rail) is { } denied) return denied;
    try { return Results.Ok(rail.Confirm(CurrentIdentity(context), id)); }
    catch (P2B5RailReferenceException exception) { return P2B5RailReferenceError(exception); }
});
app.MapGet("/api/system/device-groups", (HttpContext context, HttpRequest request, Catalog rawCatalog) =>
{
    if (DeviceSiteScopeGuard(context, QueryValue(request, "siteId")) is { } denied) return denied;
    return Results.Ok(rawCatalog.QueryDeviceGroups(request.Query["siteId"].ToString(), CurrentSiteScope(context)));
});
app.MapPost("/api/system/device-groups", (HttpContext context, DeviceGroupRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_GROUP_PERMISSION_DENIED", request.SiteId);
    if (DeviceSiteScopeGuard(context, request.SiteId) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Created("/api/system/device-groups", rawCatalog.CreateDeviceGroup(request.SiteId, request.Name, request.Description, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_GROUP_SCOPE_DENIED", ex.Code); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_GROUP_FAILED", "FAILED", request.SiteId, null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_GROUP_FAILED", "FAILED", request.SiteId, null, Array.Empty<string>(), "SITE_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_GROUP_FAILED", "CONFLICT", request.SiteId, null, Array.Empty<string>(), "GROUP_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices", (HttpContext context, DeviceInventoryWriteRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_WRITE_PERMISSION_DENIED", request.Id ?? "devices");
    if (DeviceSiteScopeGuard(context, request.SiteId) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try
    {
        var detail = rawCatalog.CreateDeviceInventory(request, actor, CurrentSiteScope(context));
        return Results.Created($"/api/system/devices/{detail.Item.Id}", detail);
    }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_WRITE_SCOPE_DENIED", ex.Code); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_CREATE_FAILED", "FAILED", request.Id ?? "devices", null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_CREATE_FAILED", "FAILED", request.Id ?? "devices", null, Array.Empty<string>(), "SITE_OR_GROUP_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_CREATE_FAILED", "CONFLICT", request.Id ?? "devices", null, Array.Empty<string>(), "DEVICE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapPut("/api/system/devices/{id}", (HttpContext context, string id, DeviceInventoryWriteRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_WRITE_PERMISSION_DENIED", id);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.UpdateDeviceInventory(id, request, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_UPDATE_SCOPE_DENIED", ex.Code); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_UPDATE_FAILED", "FAILED", id, null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_UPDATE_FAILED", "FAILED", id, null, Array.Empty<string>(), "DEVICE_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_UPDATE_FAILED", "CONFLICT", id, null, Array.Empty<string>(), "DEVICE_VERSION_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices/import/preview", (HttpContext context, DeviceImportPreviewRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_IMPORT_PERMISSION_DENIED", "devices");
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.PreviewDeviceImport(request.Csv, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_IMPORT_SCOPE_DENIED", ex.Code); }
    catch (DeviceImportResourceLimitException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_PREVIEW_FAILED", "FAILED", "devices", null, Array.Empty<string>(), "RESOURCE_LIMIT"); return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_PREVIEW_FAILED", "FAILED", "devices", null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices/import/commit", (HttpContext context, DeviceImportCommitRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_IMPORT_PERMISSION_DENIED", request.PreviewId);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.CommitDeviceImport(request.PreviewId, request.Confirm, actor, CurrentSiteScope(context))); }
    catch (DeviceSiteScopeException ex) { return DeviceScopeDenied(context, rawCatalog, "DEVICE_IMPORT_SCOPE_DENIED", ex.Code); }
    catch (UnauthorizedAccessException) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_COMMIT_FAILED", "FAILED", request.PreviewId, null, Array.Empty<string>(), "PREVIEW_OWNER_MISMATCH"); return Results.StatusCode(StatusCodes.Status403Forbidden); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_COMMIT_FAILED", "FAILED", request.PreviewId, null, Array.Empty<string>(), "PREVIEW_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_COMMIT_FAILED", "CONFLICT", request.PreviewId, null, Array.Empty<string>(), "IMPORT_VERSION_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/tasks", (HttpContext context, Catalog rawCatalog) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    return Results.Ok(rawCatalog.RecentTaskRuns(scope: CurrentSiteScope(context)));
});
app.MapGet("/api/system/tasks/{runId}/commands", (HttpContext context, string runId, Catalog rawCatalog) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    // Resolve the request's Site decision before reading it; no pre-guard
    // global fallback may authorize command metadata for a scoped operator.
    if (rawCatalog.GetTaskRun(runId, CurrentSiteScope(context)) is null) return Results.NotFound();
    return Results.Ok(rawCatalog.GetTaskRunCommands(runId));
});
app.MapGet("/api/system/tasks/{runId}/diagnostics", (HttpContext context, string runId, Catalog rawCatalog,
    P2B5SiteScopeStore scopes, InteractiveScopeAuthorizer principals) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var diagnostics = new TaskDiagnosticEvidenceService(rawCatalog, scopes, principals);
    try
    {
        var manifest = diagnostics.Manifest(CurrentIdentity(context), runId, context.RequestAborted);
        return manifest is null ? Results.NotFound(new { error = "RESOURCE_NOT_VISIBLE", detail = "任务不可见或设备归属已变化。" }) : Results.Ok(manifest);
    }
    catch (TaskDiagnosticException ex) { return ex.Result(); }
});
app.MapGet("/api/system/tasks/{runId}/diagnostics/download", (HttpContext context, string runId, Catalog rawCatalog,
    P2B5SiteScopeStore scopes, InteractiveScopeAuthorizer principals) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    int? commandIndex = null;
    if (context.Request.Query.TryGetValue("commandIndex", out var requested))
    {
        if (requested.Count != 1 || !int.TryParse(requested[0], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            return new TaskDiagnosticException("INVALID_COMMAND_INDEX", "命令序号无效。", 400).Result();
        commandIndex = parsed;
    }
    var diagnostics = new TaskDiagnosticEvidenceService(rawCatalog, scopes, principals);
    var original = CurrentIdentity(context);
    try
    {
        var download = diagnostics.Download(original, runId, commandIndex, context.RequestAborted);
        if (download is null) return Results.NotFound(new { error = "RESOURCE_NOT_VISIBLE", detail = "任务不可见或设备归属已变化。" });
        var access = download.Access;
        return (IResult)new TaskDiagnosticDownloadResult(download, () => diagnostics.CanRead(original, runId, access));
    }
    catch (TaskDiagnosticException ex) { return ex.Result(); }
});
app.MapPost("/api/system/tasks/{runId}/cancel", (HttpContext context, string runId, Catalog rawCatalog,
    BoundedCollectionScheduler scheduler) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "TASK_CANCEL_PERMISSION_DENIED", runId);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    if (rawCatalog.GetTaskRun(runId, CurrentSiteScope(context)) is null) return Results.NotFound();
    return scheduler.Cancel(runId)
        ? Results.Ok(new { cancelled = true, runId })
        : Results.Conflict(new { error = "TASK_NOT_CANCELLABLE" });
});
app.MapPost("/api/system/tasks/{runId}/retry", (HttpContext context, string runId, Catalog rawCatalog,
    BoundedCollectionScheduler scheduler) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "TASK_RETRY_PERMISSION_DENIED", runId);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    if (rawCatalog.GetTaskRun(runId, CurrentSiteScope(context)) is null) return Results.NotFound();
    return scheduler.TryRetryTaskRun(runId, out var retryId, CurrentIdentity(context).UserId)
        ? Results.Accepted("/api/system/tasks", new { runId = retryId, previousRunId = runId, action = "RETRY" })
        : Results.Conflict(new { error = "TASK_NOT_RETRYABLE_OR_SCOPE_REVOKED" });
});
app.MapPost("/api/system/tasks/{runId}/resume", (HttpContext context, string runId, Catalog rawCatalog,
    BoundedCollectionScheduler scheduler) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "TASK_RESUME_PERMISSION_DENIED", runId);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    if (rawCatalog.GetTaskRun(runId, CurrentSiteScope(context)) is null) return Results.NotFound();
    return scheduler.TryResumeInterruptedTaskRun(runId, out var resumedId, CurrentIdentity(context).UserId)
        ? Results.Accepted("/api/system/tasks", new { runId = resumedId, previousRunId = runId, action = "RESUME" })
        : Results.Conflict(new { error = "TASK_NOT_INTERRUPTED_OR_SCOPE_REVOKED" });
});
app.MapPost("/api/system/diagnostics/runs", (HttpContext context, NetworkDiagnosticBatchRequest request,
    NetworkDiagnosticsService diagnostics) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try
    {
        var run = diagnostics.Start(request, CurrentIdentity(context).UserId, CurrentSiteScope(context));
        return Results.Accepted($"/api/system/diagnostics/runs/{run.Id}", run);
    }
    catch (DeviceSiteScopeException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
});
app.MapGet("/api/system/diagnostics/runs/{id}", (HttpContext context, string id,
    NetworkDiagnosticsService diagnostics) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    return diagnostics.Get(id, CurrentSiteScope(context)) is { } run ? Results.Ok(run) : Results.NotFound();
});
app.MapPost("/api/system/diagnostics/runs/{id}/cancel", (HttpContext context, string id,
    NetworkDiagnosticsService diagnostics) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var identity = CurrentIdentity(context);
    return diagnostics.Cancel(id, identity.UserId, identity.Role, CurrentSiteScope(context))
        ? Results.Ok(new { cancelled = true })
        : Results.NotFound(new { error = "DIAGNOSTIC_RUN_NOT_VISIBLE_OR_TERMINAL" });
});
app.MapPost("/api/system/config-backups", (HttpContext context, ConfigBackupRequest request,
    ConfigBackupService backups) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try
    {
        var batch = backups.Start(request.ProfileIds ?? Array.Empty<string>(), CurrentIdentity(context), CurrentSiteScope(context), request.ConfigurationKind);
        return Results.Accepted($"/api/system/config-backups/batches/{batch.Id}", batch);
    }
    catch (ConfigBackupException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
    catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
});
app.MapGet("/api/system/config-backups/batches/{id}", (HttpContext context, string id, ConfigBackupService backups) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    return backups.GetBatch(id, CurrentIdentity(context), CurrentSiteScope(context)) is { } batch
        ? Results.Ok(batch) : Results.NotFound();
});
app.MapGet("/api/system/config-backups", (HttpContext context, string profileId, string? configurationKind,
    ConfigBackupService backups) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try { return Results.Ok(backups.List(profileId, CurrentSiteScope(context), configurationKind)); }
    catch (ConfigBackupException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapGet("/api/system/config-backups/{id}/download", (HttpContext context, string id,
    ConfigBackupService backups) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    context.Response.Headers.CacheControl = "no-store";
    var result = backups.OpenDownload(id, CurrentSiteScope(context));
    return result is null ? Results.NotFound() : Results.File(result.Value.Stream, "text/plain; charset=utf-8",
        result.Value.Name, enableRangeProcessing: true);
});
app.MapGet("/api/system/config-backups/{baseId}/diff/{targetId}", (HttpContext context, string baseId,
    string targetId, ConfigBackupService backups) =>
{
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    var diff = backups.Diff(baseId, targetId, CurrentSiteScope(context));
    return diff is null ? Results.NotFound() : Results.Ok(diff);
});
app.MapGet("/api/system/config-backup-schedules", (HttpContext context, string? profileId, ConfigBackupScheduleService schedules) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try { return Results.Ok(schedules.List(CurrentIdentity(context), CurrentSiteScope(context), profileId)); }
    catch (ConfigBackupException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapPost("/api/system/config-backup-schedules", (HttpContext context, ConfigBackupScheduleRequest request, ConfigBackupScheduleService schedules) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try { return Results.Created("/api/system/config-backup-schedules", schedules.Create(request, CurrentIdentity(context), CurrentSiteScope(context))); }
    catch (ConfigBackupException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
    catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
});
app.MapPatch("/api/system/config-backup-schedules/{id}", (HttpContext context, string id, ConfigBackupScheduleEnabledRequest request, ConfigBackupScheduleService schedules) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try { return schedules.SetEnabled(id, request.Enabled, CurrentIdentity(context), CurrentSiteScope(context)) is { } schedule ? Results.Ok(schedule) : Results.NotFound(); }
    catch (ConfigBackupException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
    catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
});
app.MapDelete("/api/system/config-backup-schedules/{id}", (HttpContext context, string id, ConfigBackupScheduleService schedules) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (DeviceSiteScopeGuard(context) is { } denied) return denied;
    try { return schedules.Delete(id, CurrentIdentity(context), CurrentSiteScope(context)) ? Results.Ok(new { deleted = true }) : Results.NotFound(); }
    catch (ConfigBackupException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
});
app.MapGet("/api/system/host-tools", (HostToolRegistry registry) => Results.Ok(registry.List()));
app.MapPut("/api/system/host-tools/{toolId}", (HttpContext context, string toolId, HostToolRegistry registry, HostToolConfiguration request) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(registry.Configure(toolId, request.ExecutablePath, CurrentIdentity(context).Username)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/system/host-tools/{toolId}/launch", async (HttpContext context, string toolId, HostToolLaunchRequest request, HostToolLaunchService launcher) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var result = await launcher.LaunchAsync(toolId, request.DeviceId, context.RequestAborted);
    return Results.Ok(result);
});
app.MapPost("/api/system/host-tools/open-directory", async (HttpContext context, HostDirectoryRequest request, FileManagerService files, DesktopHelperIpc helper) =>
{
    if (!IsSuperAdmin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        var resolved = files.Resolve(request.RootId, request.RelativePath, true);
        if (!resolved.IsDirectory) return Results.BadRequest(new { error = "A directory is required." });
        var response = await helper.SendAsync(new { operation = "OPEN_DIRECTORY", rootId = request.RootId, relativePath = resolved.RelativePath, directoryPath = resolved.FullPath }, context.RequestAborted);
        return Results.Ok(response);
    }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or FileNotFoundException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
var globalFileRoutes = app.MapGroup("/api/system/files");
globalFileRoutes.AddEndpointFilter(async (invocation, next) =>
    invocation.HttpContext.RequestServices.GetRequiredService<InteractiveScopeAuthorizer>().RequireGlobalFiles(invocation.HttpContext)
        is { } denied ? denied : await next(invocation));
globalFileRoutes.MapGet("/roots", (FileManagerService files) => Results.Ok(files.Roots()));
globalFileRoutes.MapGet("/list", (HttpRequest request, FileManagerService files) =>
{
    try
    {
        var result = files.List(request.Query["rootId"].ToString(), request.Query["path"].ToString(),
            request.Query["search"].ToString(), request.Query["sort"].ToString(), request.Query["direction"].ToString(),
            ParseQueryInt(request.Query["page"], 1), ParseQueryInt(request.Query["pageSize"], 100));
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or FileNotFoundException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
globalFileRoutes.MapGet("/download", (HttpRequest request, FileManagerService files) =>
{
    try
    {
        var result = files.OpenDownload(request.Query["rootId"].ToString(), request.Query["path"].ToString());
        return Results.File(result.Stream, result.ContentType, result.DownloadName, enableRangeProcessing: true);
    }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or FileNotFoundException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
globalFileRoutes.MapPost("/archive-jobs", (HttpContext context, ArchiveRequest request, FileArchiveService archives) =>
{
    try
    {
        var identity = CurrentIdentity(context);
        var job = archives.Create(request, identity, context.RequestAborted);
        return Results.Accepted($"/api/system/files/archive-jobs/{job.Id}", job);
    }
    catch (ArchiveRejectedException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or FileNotFoundException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
globalFileRoutes.MapGet("/archive-jobs/{id}", (HttpContext context, string id, FileArchiveService archives) =>
    archives.Get(id, CurrentIdentity(context)) is { } job ? Results.Ok(job) : Results.NotFound());
globalFileRoutes.MapPost("/archive-jobs/{id}/cancel", (HttpContext context, string id, FileArchiveService archives) =>
    archives.Cancel(id, CurrentIdentity(context)) ? Results.Ok(new { cancelled = true }) : Results.NotFound());
globalFileRoutes.MapGet("/archive-jobs/{id}/download", (HttpContext context, string id, FileArchiveService archives) =>
{
    var result = archives.OpenDownload(id, CurrentIdentity(context));
    return result is null ? Results.NotFound() : Results.File(result.Stream, "application/zip", result.Name, enableRangeProcessing: true);
});
if (settings.TerminalEnabled)
{
    app.MapLinuxServers();
    var sessionRoutes = app.MapGroup("/api/system/terminal/sessions");
    sessionRoutes.AddEndpointFilter(async (invocation, next) =>
    {
        var context = invocation.HttpContext;
        if (DeviceSiteScopeGuard(context) is { } denied) return denied;
        if (context.Request.RouteValues["sessionId"] is string sessionId &&
            !context.RequestServices.GetRequiredService<Catalog>().TerminalSessionIsInScope(sessionId, CurrentSiteScope(context)))
            return Results.NotFound();
        return await next(invocation);
    });
    app.MapGet("/api/system/terminal/devices", (HttpContext context, Catalog rawCatalog, TerminalSessionManager manager) =>
    {
        if (DeviceSiteScopeGuard(context) is { } denied) return denied;
        var sessions = manager.Recent();
        var devices = rawCatalog.RecentH3cDeviceProfiles(100, CurrentSiteScope(context)).Select(profile =>
        {
            var latest = sessions.FirstOrDefault(session => session.TargetId == profile.Id);
            return new TerminalDeviceView(profile.Id, profile.Host, profile.Port, profile.Username, profile.Enabled,
                new[] { "AUTO", "SSH", "TELNET" }, profile.HostKeyUpdatePolicy, profile.AllowLegacyAlgorithms,
                profile.TerminalEncoding, profile.TerminalProtocol, profile.AllowTelnetFallback,
                latest?.State, latest?.StartedMs, latest?.DisconnectReason);
        });
        return Results.Ok(devices);
    });
    app.MapGet("/api/system/terminal/serial-ports", (RuntimeSettings runtime) =>
    {
        var ports = SerialPort.GetPortNames().AsEnumerable();
        if (runtime.TerminalDevelopmentSimulationEnabled) ports = ports.Append("SIMULATED-COM1");
        return Results.Ok(ports.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    });
    sessionRoutes.MapGet("", (HttpContext context, HttpRequest request, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.QueryTerminalSessions(TerminalSessionQuery.FromQuery(request.Query), CurrentSiteScope(context))));
    sessionRoutes.MapGet("/{sessionId}", (string sessionId, Catalog rawCatalog) =>
    {
        var session = rawCatalog.GetTerminalSession(sessionId);
        return session is null ? Results.NotFound() : Results.Ok(session);
    });
    sessionRoutes.MapGet("/{sessionId}/events", (string sessionId, Catalog rawCatalog, int? limit) =>
        Results.Ok(rawCatalog.TerminalEvents(sessionId, Math.Clamp(limit ?? 500, 1, 5000))));
    sessionRoutes.MapGet("/{sessionId}/recording/segments", (string sessionId, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.TerminalRecordingSegments(sessionId)));
    sessionRoutes.MapGet("/{sessionId}/recording/integrity", (string sessionId,
        TerminalRecordingService recordings) =>
    {
        try { return Results.Ok(recordings.Verify(sessionId)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    sessionRoutes.MapPost("/{sessionId}/recording/verify", (string sessionId,
        TerminalRecordingService recordings) =>
    {
        try { return Results.Ok(recordings.Verify(sessionId)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    sessionRoutes.MapGet("/{sessionId}/exports", (string sessionId, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.TerminalExports(sessionId)));
    sessionRoutes.MapGet("/{sessionId}/export", async (HttpContext context, string sessionId,
        string? format, Catalog rawCatalog, TerminalRecordingService recordings, TerminalExportService exports) =>
    {
        var session = rawCatalog.GetTerminalSession(sessionId);
        if (session is null) return Results.NotFound();
        try
        {
            var integrity = recordings.Verify(sessionId);
            await exports.WriteAsync(context.Response, session, format ?? "TXT", integrity, context.RequestAborted);
            return Results.Empty;
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    sessionRoutes.MapGet("/{sessionId}/recording/replay", (string sessionId,
        long? offset, int? limit, long? fromMs, long? toMs, TerminalRecordingService recordings) =>
    {
        try
        {
            var safeOffset = Math.Max(0, offset ?? 0);
            var safeLimit = Math.Clamp(limit ?? 500, 1, 2000);
            var page = recordings.ReadReplay(sessionId, safeOffset, safeLimit, fromMs, toMs);
            return Results.Ok(new
            {
                page.SessionId, page.Offset, page.Limit, page.HasMore,
                entries = page.Entries.Select(entry => new
                {
                    entry.Position, entry.SegmentNumber, entry.AtMs, entry.Direction,
                    dataB64 = Convert.ToBase64String(entry.Data)
                })
            });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (FileNotFoundException) { return Results.NotFound(); }
    });
    sessionRoutes.MapPost("", (HttpContext context, TerminalSessionRequest request, TerminalSessionManager manager, Catalog rawCatalog) =>
    {
        var scope = CurrentSiteScope(context);
        if (!scope.IsGlobal && (string.Equals(request.Protocol, "SERIAL", StringComparison.OrdinalIgnoreCase) ||
            !rawCatalog.H3cProfileIsInScope(request.TargetId, scope))) return Results.NotFound();
        try
        {
            var sessionId = manager.Create(request);
            return Results.Created($"/api/system/terminal/sessions/{sessionId}",
                new { sessionId, state = "CONNECTING", mode = "RESTRICTED_READONLY" });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (IOException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    sessionRoutes.MapPost("/{sessionId}/ticket", (string sessionId,
        TerminalSessionManager manager, TerminalTicketStore tickets) =>
    {
        if (manager.Get(sessionId) is null) return Results.NotFound();
        return Results.Ok(tickets.Issue(sessionId));
    });
    sessionRoutes.MapPost("/{sessionId}/close", async (string sessionId,
        TerminalSessionManager manager) =>
    {
        if (!await manager.CloseAsync(sessionId)) return Results.NotFound();
        return Results.Ok(new { sessionId, state = "CLOSED" });
    });
    sessionRoutes.MapGet("/{sessionId}/stream", async (HttpContext context,
        string sessionId, TerminalSessionManager manager, TerminalTicketStore tickets) =>
    {
        var expectedOrigins = new[] { $"http://127.0.0.1:{settings.WebPort}", $"http://localhost:{settings.WebPort}", $"http://[::1]:{settings.WebPort}" };
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !expectedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var ticket = context.Request.Query["ticket"].ToString();
        if (string.IsNullOrWhiteSpace(ticket) || !tickets.TryConsume(ticket, sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var session = manager.Get(sessionId);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        bool StillAuthorized()
        {
            return context.RequestServices.GetRequiredService<InteractiveScopeAuthorizer>().CanUseTerminal(CurrentIdentity(context), sessionId);
        }
        var send = SendTerminalOutputAsync(socket, session, StillAuthorized, cancellation.Token);
        var receive = ReceiveTerminalInputAsync(socket, session, StillAuthorized, cancellation.Token);
        var scopeMonitor = MonitorTerminalScopeAsync(session, StillAuthorized, cancellation.Token);
        await Task.WhenAny(send, receive, scopeMonitor).ConfigureAwait(false);
        cancellation.Cancel();
        try { await Task.WhenAll(send, receive, scopeMonitor).ConfigureAwait(false); } catch { }
        if (!StillAuthorized()) await session.CloseForScopeRevocationAsync().ConfigureAwait(false);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        }
    });
}
if (settings.H3cEnabled)
{
    app.MapGet("/api/system/h3c/profiles", (HttpContext context, Catalog rawCatalog) =>
    {
        if (DeviceSiteScopeGuard(context) is { } denied) return denied;
        return Results.Ok(rawCatalog.RecentH3cDeviceProfiles(scope: CurrentSiteScope(context)).Select(profile => profile.ToView()));
    });
    app.MapPost("/api/system/h3c/profiles/{id}/test-connection", async (HttpContext context, string id,
        Catalog rawCatalog, H3cConnectionTestService tests, P2B5SiteScopeStore scopes) =>
    {
        if (DeviceSiteScopeGuard(context) is { } denied) return denied;
        var scope = scopes.Resolve(CurrentIdentity(context));
        if (!scope.IsAuthorized || !rawCatalog.H3cProfileIsInScope(id, scope)) return Results.NotFound();
        try
        {
            var result = await tests.TestAsync(id, context.RequestAborted).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { return Results.StatusCode(StatusCodes.Status499ClientClosedRequest); }
    });
    app.MapPost("/api/system/h3c/profiles/onboard", (HttpContext context, H3cOnboardRequest request, Catalog rawCatalog) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_PROFILE_WRITE_PERMISSION_DENIED", request.Host);
        var siteId = string.IsNullOrWhiteSpace(request.SiteId) ? "local" : request.SiteId.Trim();
        if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
        if (string.IsNullOrWhiteSpace(request.Host) || string.IsNullOrWhiteSpace(request.Username))
            return Results.BadRequest(new { error = "host and username are required." });
        var protocol = request.Protocol?.Trim().ToUpperInvariant() switch
        {
            "SSH" => "SSH",
            "TELNET" => "TELNET",
            "AUTO" or null or "" => "AUTO",
            _ => null
        };
        if (protocol is null) return Results.BadRequest(new { error = "protocol must be SSH, TELNET or AUTO." });
        var port = request.Port ?? (protocol == "TELNET" ? 23 : 22);
        if (port is < 1 or > 65535) return Results.BadRequest(new { error = "port must be between 1 and 65535." });
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var inventory = request.Inventory ?? new DeviceInventoryWriteRequest(null, request.Host.Trim(), request.Host.Trim(),
                "H3C", "", "NETWORK_DEVICE", siteId, null, "", true, null);
            rawCatalog.CreateH3cDeviceProfile(new(id, request.Host.Trim(), port, request.Username.Trim(),
                string.Empty, string.Empty, true, 0, 0,
                H3cHostKeyPolicy.Automatic, false, "AUTO", protocol, request.AllowTelnetFallback,
                request.Password, SiteId: siteId), inventory, CurrentIdentity(context).Username);
            return rawCatalog.GetH3cDeviceProfile(id) is { } profile
                ? Results.Created($"/api/system/h3c/profiles/{id}", profile.ToView())
                : Results.NotFound();
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        { return Results.Conflict(new { error = $"Profile could not be saved ({ex.GetType().Name})." }); }
    });
    app.MapPost("/api/system/h3c/profiles", (HttpContext context, H3cProfileRequest request, Catalog rawCatalog) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_PROFILE_WRITE_PERMISSION_DENIED", request.Id);
        try
        {
            rawCatalog.CreateH3cDeviceProfile(new(request.Id, request.Host, request.Port, request.Username,
                request.CredentialReference, request.HostKeySha256, request.Enabled, 0, 0,
                request.HostKeyUpdatePolicy, request.AllowLegacyAlgorithms, request.TerminalEncoding,
                request.TerminalProtocol, request.AllowTelnetFallback, request.Password, request.ClearPassword));
            return rawCatalog.GetH3cDeviceProfile(request.Id) is { } profile
                ? Results.Created($"/api/system/h3c/profiles/{request.Id}", profile.ToView())
                : Results.NotFound();
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Profile could not be saved ({ex.GetType().Name})." });
        }
    });
    app.MapPut("/api/system/h3c/profiles/{id}/compatibility", (HttpContext context, string id, H3cCompatibilityRequest request,
        Catalog rawCatalog) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_PROFILE_COMPATIBILITY_PERMISSION_DENIED", id);
        try
        {
            rawCatalog.SetH3cCompatibility(id, request.HostKeyUpdatePolicy, request.AllowLegacyAlgorithms,
                request.TerminalEncoding, request.TerminalProtocol, request.AllowTelnetFallback);
            return rawCatalog.GetH3cDeviceProfile(id) is { } profile
                ? Results.Ok(profile.ToView())
                : Results.NotFound();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    });
    app.MapPut("/api/system/h3c/profiles/{id}/credential", (HttpContext context, string id,
        H3cCredentialRequest request, Catalog rawCatalog, P2B5SiteScopeStore scopes) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_PROFILE_CREDENTIAL_PERMISSION_DENIED", id);
        var scope = scopes.Resolve(CurrentIdentity(context));
        if (!scope.IsAuthorized || !rawCatalog.H3cProfileIsInScope(id, scope)) return Results.NotFound();
        try
        {
            rawCatalog.UpdateH3cCredential(id, request.Password, request.ClearPassword);
            return rawCatalog.GetH3cDeviceProfile(id) is { } profile
                ? Results.Ok(profile.ToView())
                : Results.NotFound();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/h3c/profiles/{id}/host-key-history", (HttpContext context, string id, Catalog rawCatalog, int? limit) =>
        !IsSuperAdmin(context)
            ? DevicePermissionDenied(context, rawCatalog, "H3C_HOST_KEY_HISTORY_PERMISSION_DENIED", id)
            : Results.Ok(rawCatalog.SshHostKeyHistory(id, Math.Clamp(limit ?? 100, 1, 500))));
    app.MapGet("/api/system/h3c/profiles/{id}/facts", (HttpContext context, string id, HttpRequest request,
        Catalog rawCatalog, P2B5SiteScopeStore scopes) =>
    {
        var scope = scopes.Resolve(CurrentIdentity(context));
        if (!scope.IsAuthorized)
            return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = scope.Detail }, statusCode: StatusCodes.Status403Forbidden);
        if (!rawCatalog.H3cProfileIsInScope(id, scope)) return Results.NotFound();
        var kind = request.Query["kind"].ToString();
        var history = string.Equals(request.Query["history"], "true", StringComparison.OrdinalIgnoreCase);
        var limit = int.TryParse(request.Query["limit"], out var value) ? value : 100;
        return Results.Ok(rawCatalog.QueryH3cFacts(id, kind, history, limit, scope));
    });
    app.MapGet("/api/system/h3c/profiles/{id}/lldp-relations", (HttpContext context, string id,
        HttpRequest request, Catalog rawCatalog, P2B5SiteScopeStore scopes) =>
    {
        var scope = scopes.Resolve(CurrentIdentity(context));
        if (!scope.IsAuthorized)
            return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = scope.Detail },
                statusCode: StatusCodes.Status403Forbidden);
        if (!rawCatalog.H3cProfileIsInScope(id, scope)) return Results.NotFound();
        var history = string.Equals(request.Query["history"], "true", StringComparison.OrdinalIgnoreCase);
        var limit = int.TryParse(request.Query["limit"], out var value) ? value : 100;
        return Results.Ok(rawCatalog.QueryH3cLldpRelations(id, history, limit, scope));
    });
    app.MapGet("/api/system/h3c/profiles/{id}/lldp-claims", (HttpContext context, string id,
        HttpRequest request, Catalog rawCatalog, P2B5SiteScopeStore scopes) =>
    {
        var scope = scopes.Resolve(CurrentIdentity(context));
        if (!scope.IsAuthorized)
            return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = scope.Detail },
                statusCode: StatusCodes.Status403Forbidden);
        if (!rawCatalog.H3cProfileIsInScope(id, scope)) return Results.NotFound();
        var history = string.Equals(request.Query["history"], "true", StringComparison.OrdinalIgnoreCase);
        var limit = int.TryParse(request.Query["limit"], out var value) ? value : 100;
        var facts = rawCatalog.QueryH3cFacts(id, "lldp.neighbor", history, Math.Clamp(limit, 1, 1000), scope,
            requireCapturedScope: true);
        var relations = H3cLldpTopologyProjection.Project(id, facts);
        return Results.Ok(new H3cLldpClaimsView(id, scope.State, scope.IsGlobal, scope.ClaimsRevision,
            scope.AllowedSiteReferences.Order(StringComparer.Ordinal).ToArray(), facts.Count, relations.Count, relations));
    });
    app.MapPost("/api/system/h3c/profiles/{id}/offline-replay", (HttpContext context, string id,
        H3cOfflineReplayRequest request, Catalog rawCatalog) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_OFFLINE_REPLAY_PERMISSION_DENIED", id);
        if (request.CommandOutputs is null || request.CommandOutputs.Count == 0 || request.CommandOutputs.Count > 32)
            return Results.BadRequest(new { error = "At least one command output is required." });
        if (request.CommandOutputs.Keys.Any(command => !H3cCommandPolicy.IsAllowed(command)))
            return Results.BadRequest(new { error = "Offline replay contains a command outside the read-only allowlist." });
        if (request.CommandOutputs.Any(item => item.Key.Length > 128 || item.Value is null || item.Value.Length > 262144) ||
            request.CommandOutputs.Sum(item => (long)item.Value.Length) > 2_097_152)
            return Results.BadRequest(new { error = "Offline replay output exceeds the bounded fixture size." });
        if (request.CapturedMs <= 0)
            return Results.BadRequest(new { error = "capturedMs must be a positive Unix millisecond timestamp." });
        try
        {
            var result = rawCatalog.ImportH3cOfflineReplay(id, request.CommandOutputs, request.CapturedMs,
                Catalog.H3cOfflineFixtureSource);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidDataException ex) { return Results.UnprocessableEntity(new { error = ex.Message }); }
        catch (H3cFactRevisionConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapPost("/api/system/h3c/task-definitions", (HttpContext context, H3cTaskDefinitionRequest request, Catalog rawCatalog,
        TaskSiteScopeAuthorizer taskScopes) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_TASK_DEFINITION_PERMISSION_DENIED", request.Id);
        if (request.TimeoutMs is < 100 or > 600000)
            return Results.BadRequest(new { error = "timeoutMs must be between 100 and 600000." });
        try
        {
            var snapshot = taskScopes.CaptureDefinition(request.ProfileId, CurrentIdentity(context).UserId);
            rawCatalog.CreateTaskDefinition(request.Id, "H3C_SSH_READONLY", request.ProfileId,
                request.Command, TimeSpan.FromMilliseconds(request.TimeoutMs), request.Enabled, scopeSnapshot: snapshot,
                commands: request.Commands);
            return Results.Created($"/api/system/tasks/definitions/{request.Id}",
                rawCatalog.GetTaskDefinition(request.Id));
        }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Task definition could not be saved ({ex.GetType().Name})." });
        }
    });
    app.MapPost("/api/system/tasks/{definitionId}/runs", (HttpContext context, string definitionId, Catalog rawCatalog,
        RuntimeStatus status, BoundedCollectionScheduler scheduler) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_COLLECTION_PERMISSION_DENIED", definitionId);
        if (!rawCatalog.Available || !rawCatalog.RawWriteAvailable || !status.CollectionAllowed)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            if (rawCatalog.GetTaskDefinition(definitionId)?.TaskType != "H3C_SSH_READONLY")
                return Results.NotFound();
            if (!scheduler.TryScheduleDefinition(definitionId, out var runId, CurrentIdentity(context).UserId))
                return Results.Problem("The bounded collection queue is full or shutting down.", statusCode: StatusCodes.Status429TooManyRequests);
            return Results.Accepted("/api/system/tasks", new { runId });
        }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapPost("/api/system/devices/connection-tests", (HttpContext context, DeviceConnectionTestRequest request,
        Catalog rawCatalog, DeviceConnectionTestService tests) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_CONNECTION_TEST_PERMISSION_DENIED");
        if (!rawCatalog.IsModuleEnabled("h3c")) return Results.NotFound();
        try
        {
            var plan = tests.Start(request, CurrentIdentity(context), CurrentSiteScope(context));
            return Results.Created($"/api/system/h3c/task-plans/{plan.Id}", plan);
        }
        catch (DeviceConnectionTestException ex) { return Results.Json(new { error = ex.Code, detail = ex.Message }, statusCode: ex.StatusCode); }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapPost("/api/system/h3c/task-plans", (HttpContext context, H3cTaskPlanCreateRequest request,
        Catalog rawCatalog, H3cBatchTaskService plans) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_TASK_PLAN_PERMISSION_DENIED", request.Id ?? "new");
        try
        {
            var plan = plans.CreateAndSchedule(request, CurrentIdentity(context).UserId);
            return Results.Created($"/api/system/h3c/task-plans/{plan.Id}", plan);
        }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/h3c/task-plans/{id}", (HttpContext context, string id,
        Catalog rawCatalog, H3cBatchTaskService plans) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_TASK_PLAN_READ_PERMISSION_DENIED", id);
        try { return Results.Ok(plans.GetVisible(id, CurrentIdentity(context).UserId)); }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    });
    app.MapGet("/api/system/h3c/task-plans/{id}/report", (HttpContext context, string id,
        Catalog rawCatalog, H3cBatchTaskService plans) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_TASK_PLAN_REPORT_PERMISSION_DENIED", id);
        try { return Results.Ok(plans.GetReport(id, CurrentIdentity(context).UserId)); }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    });
    app.MapPost("/api/system/h3c/task-plans/{id}/cancel", (HttpContext context, string id,
        Catalog rawCatalog, H3cBatchTaskService plans) =>
    {
        if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "H3C_TASK_PLAN_CANCEL_PERMISSION_DENIED", id);
        try { return Results.Ok(plans.Cancel(id, CurrentIdentity(context).UserId)); }
        catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    });
}
app.MapGet("/api/system/zte/profiles", (HttpContext context, ZteCollectionService zte, P2B5SiteScopeStore scopes) =>
{
    var scope = scopes.Resolve(CurrentIdentity(context));
    if (!scope.IsAuthorized) return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = scope.Detail }, statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(zte.Profiles(scope));
});
app.MapPost("/api/system/zte/profiles", (HttpContext context, ZteProfileRequest request, Catalog catalog, ZteCollectionService zte) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "ZTE_PROFILE_WRITE_PERMISSION_DENIED", request.Id);
    try
    {
        catalog.CreateZteDeviceProfile(new(request.Id, request.Host, request.Port, request.Username,
            string.IsNullOrWhiteSpace(request.CredentialReference) ? "ZTE_" + request.Id.ToUpperInvariant() : request.CredentialReference,
            request.HostKeySha256, request.Enabled, 0, 0, request.HostKeyUpdatePolicy,
            request.AllowLegacyAlgorithms, request.TerminalEncoding, request.SiteId), request.Password, request.ClearPassword);
        return Results.Created($"/api/system/zte/profiles/{request.Id}", zte.Profile(request.Id));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Microsoft.Data.Sqlite.SqliteException ex) { return Results.Conflict(new { error = $"Profile could not be saved ({ex.GetType().Name})." }); }
});

app.MapPut("/api/system/zte/profiles/{id}/credential", (HttpContext context, string id,
    ZteCredentialRequest request, Catalog catalog, ZteCollectionService zte, P2B5SiteScopeStore scopes) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "ZTE_PROFILE_CREDENTIAL_PERMISSION_DENIED", id);
    var scope = scopes.Resolve(CurrentIdentity(context));
    if (!scope.IsAuthorized || !zte.ProfileIsInScope(id, scope)) return Results.NotFound();
    try
    {
        catalog.UpdateZteCredential(id, request.Password, request.ClearPassword);
        return zte.Profile(id) is { } profile ? Results.Ok(profile) : Results.NotFound();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/system/sites/{siteId}/ssh-jump-host", (HttpContext context, string siteId,
    Catalog catalog, P2B5SiteScopeStore scopes) =>
{
    if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
    return catalog.GetSiteSshJumpHost(siteId, includePassword: false) is { } jump
        ? Results.Ok(jump.ToView())
        : Results.NotFound();
});
app.MapPut("/api/system/sites/{siteId}/ssh-jump-host", (HttpContext context, string siteId,
    SiteSshJumpHostRequest request, Catalog catalog, P2B5SiteScopeStore scopes) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "SITE_JUMP_HOST_WRITE_PERMISSION_DENIED", siteId);
    if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
    try
    {
        catalog.UpsertSiteSshJumpHost(new(siteId, request.Host, request.Port, request.Username,
            string.IsNullOrWhiteSpace(request.CredentialReference)
                ? "JUMP_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(siteId)))[..32]
                : request.CredentialReference, request.Password, request.HostKeySha256 ?? string.Empty,
            request.Enabled, 0, request.ClearPassword));
        return catalog.GetSiteSshJumpHost(siteId, includePassword: false) is { } saved
            ? Results.Ok(saved.ToView()) : Results.NotFound();
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/api/system/sites/{siteId}/ssh-jump-host", (HttpContext context, string siteId,
    Catalog catalog, P2B5SiteScopeStore scopes) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "SITE_JUMP_HOST_DELETE_PERMISSION_DENIED", siteId);
    if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
    return catalog.DeleteSiteSshJumpHost(siteId) ? Results.NoContent() : Results.NotFound();
});
app.MapPost("/api/system/sites/{siteId}/ssh-jump-host/test", async (HttpContext context, string siteId,
    Catalog catalog, NetmikoJumpHostProbe probe) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "SITE_JUMP_HOST_TEST_PERMISSION_DENIED", siteId);
    if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
    var jump = catalog.GetSiteSshJumpHost(siteId, includePassword: true);
    if (jump is null) return Results.NotFound();
    try
    {
        var result = await probe.ProbeAsync(jump, context.RequestAborted).ConfigureAwait(false);
        if (!catalog.UpdateSiteSshJumpHostFingerprint(siteId, result.HostKeySha256, jump))
            return Results.Conflict(new { error = "JUMP_HOST_CONFIGURATION_CHANGED" });
        return catalog.GetSiteSshJumpHost(siteId, includePassword: false) is { } saved
            ? Results.Ok(saved.ToView()) : Results.NotFound();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    { return Results.StatusCode(StatusCodes.Status499ClientClosedRequest); }
    catch (NetmikoJumpHostProbeException ex)
    { return Results.Conflict(new { error = ex.Code, detail = ex.Message }); }
});
app.MapDelete("/api/system/sites/{siteId}/ssh-jump-host/fingerprint", (HttpContext context, string siteId,
    Catalog catalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "SITE_JUMP_HOST_FINGERPRINT_DELETE_PERMISSION_DENIED", siteId);
    if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
    if (!catalog.ClearSiteSshJumpHostFingerprint(siteId)) return Results.NotFound();
    return catalog.GetSiteSshJumpHost(siteId, includePassword: false) is { } saved
        ? Results.Ok(saved.ToView()) : Results.NotFound();
});
app.MapPost("/api/system/sites/{siteId}/ssh-jump-host/refresh-fingerprint", async (HttpContext context, string siteId,
    Catalog catalog, NetmikoJumpHostProbe probe) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "SITE_JUMP_HOST_REFRESH_PERMISSION_DENIED", siteId);
    if (DeviceSiteScopeGuard(context, siteId) is { } denied) return denied;
    var jump = catalog.GetSiteSshJumpHost(siteId, includePassword: true);
    if (jump is null) return Results.NotFound();
    try
    {
        var result = await probe.ProbeAsync(jump, context.RequestAborted).ConfigureAwait(false);
        if (!catalog.UpdateSiteSshJumpHostFingerprint(siteId, result.HostKeySha256, jump))
            return Results.Conflict(new { error = "JUMP_HOST_CONFIGURATION_CHANGED" });
        return catalog.GetSiteSshJumpHost(siteId, includePassword: false) is { } saved
            ? Results.Ok(saved.ToView()) : Results.NotFound();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    { return Results.StatusCode(StatusCodes.Status499ClientClosedRequest); }
    catch (NetmikoJumpHostProbeException ex)
    { return Results.Conflict(new { error = ex.Code, detail = ex.Message }); }
});
app.MapPost("/api/system/zte/profiles/{id}/collect", (HttpContext context, string id, ZteCollectionService zte,
    Catalog catalog, P2B5SiteScopeStore scopes) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, catalog, "ZTE_COLLECTION_PERMISSION_DENIED", id);
    var profile = zte.Profile(id);
    if (profile is null) return Results.NotFound();
    if (DeviceSiteScopeGuard(context, profile.SiteId) is { } denied) return denied;
    var actorUserId = CurrentIdentity(context).UserId;
    var siteId = profile.SiteId;
    void RequireActorScope()
    {
        var actor = catalog.GetActiveTaskActor(actorUserId);
        if (actor is null || actor.Role != "SUPERADMIN" || !DeviceSiteScope.Allows(scopes.Resolve(actor), siteId) ||
            catalog.GetZteDeviceProfile(id)?.SiteId != siteId)
            throw new TaskScopeRevokedException("ZTE_ACTOR_SCOPE_REVOKED");
    }
    try { return Results.Accepted($"/api/system/zte/runs", new { runId = zte.Start(id, RequireActorScope) }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (TaskScopeRevokedException ex) { return Results.Json(new { error = ex.Reason }, statusCode: StatusCodes.Status403Forbidden); }
    catch (ZteCollectionRejectedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/zte/runs/{id}", (HttpContext context, string id, ZteCollectionService zte, Catalog catalog) =>
    !IsSuperAdmin(context) ? DevicePermissionDenied(context, catalog, "ZTE_RUN_READ_PERMISSION_DENIED", id) :
    zte.GetRun(id) is { } run ? Results.Ok(run) : Results.NotFound());
app.MapGet("/api/system/zte/profiles/{id}/facts", (HttpContext context, string id, HttpRequest request, Catalog catalog, P2B5SiteScopeStore scopes, ZteCollectionService zte) =>
{
    var scope = scopes.Resolve(CurrentIdentity(context));
    if (!scope.IsAuthorized) return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = scope.Detail }, statusCode: StatusCodes.Status403Forbidden);
    if (!zte.ProfileIsInScope(id, scope)) return Results.NotFound();
    var kind = request.Query["kind"].ToString();
    var history = string.Equals(request.Query["history"], "true", StringComparison.OrdinalIgnoreCase);
    var limit = int.TryParse(request.Query["limit"], out var value) ? value : 100;
    return Results.Ok(catalog.QueryZteFacts(id, kind, history, limit));
});
app.MapPost("/api/system/raw-integrity-audits/{auditId}/quarantine",
    (HttpContext context, string auditId, RawIssueQuarantineRequest request, Catalog rawCatalog, RuntimeStatus status) =>
    {
        if (context.Items[WebCookieNames.IdentityItem] is not WebUserIdentity { Role: "SUPERADMIN" } identity)
            return Results.Forbid();
        try
        {
            rawCatalog.QuarantineIntegrityAudit(auditId, identity.Username, request.Detail);
            var unresolved = rawCatalog.GetUnresolvedIntegrityIssueCount();
            status.ReassessIntegrityIssues(unresolved);
            return Results.Ok(new { auditId, disposition = "QUARANTINED", unresolvedIntegrityIssues = unresolved,
                note = "Catalog quarantine is recorded; Raw and tail evidence remain on disk." });
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });

app.MapGet("/api/system/wireless/ac-domains", (HttpContext context, HttpRequest request,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var sourceAcId = QueryValue(request, "sourceAcId");
        var items = snapshot.AcDomains.Where(item => sourceAcId is null || item.MemberDeviceIds.Contains(sourceAcId, StringComparer.Ordinal)).ToArray();
        return Results.Ok(ToWirelessPage(items, request, snapshot));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ac-domains/{id}", (HttpContext context, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var item = snapshot.AcDomains.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return item is null ? WirelessResourceNotVisible() : Results.Ok(item);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ap-resources", (HttpContext context, HttpRequest request,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var serial = WirelessProjectionEngine.NormalizeSerial(QueryValue(request, "serial"));
        var mac = QueryValue(request, "mac") is { } rawMac ? WirelessProjectionEngine.NormalizeMac(rawMac) : null;
        if (mac is { Length: 0 }) return Results.BadRequest(new { error = "mac must be a complete normalized MAC address." });
        var domainId = QueryValue(request, "domainId");
        var sourceAcId = QueryValue(request, "sourceAcId");
        var search = QueryValue(request, "search");
        var items = snapshot.ApResources.Where(item =>
                (serial is null || string.Equals(item.SerialEvidence, serial, StringComparison.Ordinal)) &&
                (mac is null || item.NormalizedMacSet.Contains(mac, StringComparer.Ordinal)) &&
                (domainId is null || item.SourceAcDomainIds.Contains(domainId, StringComparer.Ordinal)) &&
                (sourceAcId is null || item.Observations.Any(observation => string.Equals(observation.SourceAcDeviceId, sourceAcId, StringComparison.Ordinal))) &&
                (search is null || string.Equals(item.Id, search, StringComparison.Ordinal) || string.Equals(item.DisplayName, search, StringComparison.Ordinal)))
            .ToArray();
        return Results.Ok(ToWirelessPage(items, request, snapshot));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ap-resources/{id}", (HttpContext context, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var item = snapshot.ApResources.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return item is null ? WirelessResourceNotVisible() : Results.Ok(item);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ap-resources/{id}/runtime-state", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var state = wireless.GetRuntimeState(id, CurrentSiteScope(context), context.RequestAborted,
            sourceAcId: QueryValue(request, "sourceAcId"), domainId: QueryValue(request, "domainId"));
        return state is null ? WirelessResourceNotVisible() : Results.Ok(state);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ap-resources/{id}/observations", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var resource = snapshot.ApResources.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (resource is null) return WirelessResourceNotVisible();
        var sourceAcId = QueryValue(request, "sourceAcId");
        var domainId = QueryValue(request, "domainId");
        var items = resource.Observations.Where(item =>
            (sourceAcId is null || string.Equals(item.SourceAcDeviceId, sourceAcId, StringComparison.Ordinal)) &&
            (domainId is null || string.Equals(item.SourceAcDomainId, domainId, StringComparison.Ordinal))).ToArray();
        return Results.Ok(ToWirelessPage(items, request, snapshot));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ac-domains/{id}/topology", (HttpContext context, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var domain = snapshot.AcDomains.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return domain is null ? WirelessResourceNotVisible() : Results.Ok(new WirelessTopologyView(
            domain.Id, domain.DomainKind, domain.TopologyStatus, domain.MemberDeviceIds, domain.SourceEvidenceRefs,
            string.Equals(domain.TopologyStatus, "CONFIRMED", StringComparison.Ordinal), domain.Revision, domain.AsOf));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ac-domains/{id}/ap-associations", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetSnapshot(CurrentSiteScope(context));
        var domain = snapshot.AcDomains.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (domain is null) return WirelessResourceNotVisible();
        var sourceAcId = QueryValue(request, "sourceAcId");
        var associations = snapshot.ApResources
            .Where(resource => resource.SourceAcDomainIds.Contains(id, StringComparer.Ordinal))
            .SelectMany(resource => resource.Observations
                .Where(observation => string.Equals(observation.SourceAcDomainId, id, StringComparison.Ordinal) &&
                    (sourceAcId is null || string.Equals(observation.SourceAcDeviceId, sourceAcId, StringComparison.Ordinal)))
                .Select(observation => new
                {
                    resourceId = resource.Id,
                    displayName = resource.DisplayName,
                    identityStatus = resource.IdentityStatus,
                    observationId = observation.Id,
                    sourceAcDeviceId = observation.SourceAcDeviceId,
                    sourceAcDomainId = observation.SourceAcDomainId,
                    sourceSiteId = observation.SourceSiteId,
                    observedApName = observation.ObservedApName,
                    serialEvidence = observation.SerialEvidence,
                    normalizedMacSet = observation.NormalizedMacSet,
                    apGroup = observation.ApGroup,
                    rawState = observation.RawState,
                    normalizedState = observation.NormalizedState,
                    capturedMs = observation.CapturedMs,
                    rawManifestRef = observation.RawManifestRef,
                    sourceRevision = observation.SourceRevision,
                    relationStatus = observation.RelationStatus
                }))
            .OrderBy(item => item.observedApName, StringComparer.Ordinal)
            .ThenBy(item => item.sourceAcDeviceId, StringComparer.Ordinal)
            .ToArray();
        return Results.Ok(new
        {
            items = associations,
            total = associations.Length,
            domainId = id,
            asOf = snapshot.AsOf,
            scope = snapshot.Scope,
            sourceRevision = snapshot.SourceRevision,
            warnings = snapshot.Warnings
        });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ac-domains/{id}/collection-manifests", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        if (string.IsNullOrWhiteSpace(id)) return WirelessResourceNotVisible();
        var page = ParseQueryInt(QueryValue(request, "page"), 1);
        var pageSize = ParseQueryInt(QueryValue(request, "pageSize") ?? QueryValue(request, "limit"), 20);
        if (page < 1 || pageSize is < 1 or > 100)
            return Results.BadRequest(new { error = "page must be positive and pageSize must be between 1 and 100." });
        return Results.Ok(wireless.GetCollectionManifests(CurrentSiteScope(context), id,
            QueryValue(request, "sourceAcId"), page, pageSize));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/collection-manifests/{id}", (HttpContext context, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        return wireless.GetCollectionManifest(id, CurrentSiteScope(context), QueryValue(context.Request, "sourceAcId")) is { } detail
            ? Results.Ok(detail)
            : WirelessResourceNotVisible();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ac-domains/{id}/ap-groups", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetGroupSnapshot(CurrentSiteScope(context), QueryValue(request, "sourceAcId"));
        if (!snapshot.AcDomains.Any(domain => string.Equals(domain.Id, id, StringComparison.Ordinal))) return WirelessResourceNotVisible();
        var items = snapshot.ApGroups.Where(group => string.Equals(group.SourceAcDomainId, id, StringComparison.Ordinal)).ToArray();
        return Results.Ok(ToWirelessPage(items, request, snapshot));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ap-groups/{id}", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetGroupSnapshot(CurrentSiteScope(context), QueryValue(request, "sourceAcId"));
        var group = snapshot.ApGroups.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (group is null) return WirelessResourceNotVisible();
        var members = WirelessProjectionEngine.GroupMembers(snapshot, group);
        var observations = members.SelectMany(item => item.Observations).ToArray();
        return Results.Ok(new
        {
            group,
            members,
            observations,
            total = members.Count,
            asOf = group.ObservedAtMs,
            scope = snapshot.Scope,
            sourceRevision = group.SourceRevision,
            warnings = snapshot.Warnings,
            provenance = new
            {
                group.SourceAcDeviceId,
                group.SourceAcDeviceIds,
                group.SourceAcDomainId,
                group.SourceGroupKey,
                group.GroupIdentityKey,
                group.GroupIdentityStatus,
                group.SourceRevision,
                group.MembershipRevision,
                group.SourceObservationIds,
                group.SourceEvidenceRefs,
                group.ObservedAtMs,
                group.ConflictReason,
                group.ScopeStatus,
                group.GroupScope
            }
        });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

app.MapGet("/api/system/wireless/ap-groups/{id}/members", (HttpContext context, HttpRequest request, string id,
    WirelessReadModelService wireless, P2B5SiteScopeStore scopes) =>
{
    if (WirelessScopeGuard(context, scopes) is { } denied) return denied;
    try
    {
        var snapshot = wireless.GetGroupSnapshot(CurrentSiteScope(context), QueryValue(request, "sourceAcId"));
        var group = snapshot.ApGroups.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (group is null) return WirelessResourceNotVisible();
        var items = WirelessProjectionEngine.GroupMembers(snapshot, group);
        return Results.Ok(new { items, total = items.Count, asOf = group.ObservedAtMs, scope = snapshot.Scope, sourceRevision = group.SourceRevision, membershipRevision = group.MembershipRevision, warnings = snapshot.Warnings });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
    { return WirelessAuthorityUnavailable(ex); }
});

// Vue Router uses history mode. API and WebSocket routes are mapped above; all other
// application paths must return the same offline entry so browser refresh keeps working.
app.MapFallbackToFile("index.html");

app.Run();

static bool IsAllowedWebHost(HostString host, RuntimeSettings settings)
{
    if (host.Port != settings.WebPort) return false;
    if (string.Equals(host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host.Host, "[::1]", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host.Host, "::1", StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

static IResult? WirelessScopeGuard(HttpContext context, P2B5SiteScopeStore scopes)
{
    var scope = scopes.Resolve(CurrentIdentity(context));
    if (!scope.IsAuthorized)
    {
        var status = scope.State == "UNAVAILABLE" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden;
        var error = scope.State switch
        {
            "CONFLICT" => "P2B_SITE_SCOPE_CONFLICT",
            "DENIED" => "P2B_SITE_SCOPE_DENIED",
            "UNAVAILABLE" => "WIRELESS_AUTHORITY_UNAVAILABLE",
            _ => "P2B_SITE_SCOPE_DENIED"
        };
        return Results.Json(new { error, detail = scope.Detail }, statusCode: status);
    }
    context.Items[P2B5SiteScopeHttpContextKeys.Decision] = scope;
    return null;
}

static IResult WirelessResourceNotVisible() =>
    Results.Json(new { error = "RESOURCE_NOT_VISIBLE" }, statusCode: StatusCodes.Status404NotFound);

static IResult WirelessAuthorityUnavailable(Exception exception) =>
    Results.Json(new { error = "WIRELESS_AUTHORITY_UNAVAILABLE", detail = "Wireless source facts are not safely readable." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

static WirelessPage<T> ToWirelessPage<T>(IReadOnlyList<T> items, HttpRequest request, WirelessProjectionSnapshot snapshot)
{
    var page = ParseQueryInt(QueryValue(request, "page"), 1);
    var pageSize = ParseQueryInt(QueryValue(request, "pageSize") ?? QueryValue(request, "limit"), 50);
    if (page < 1 || pageSize is < 1 or > 100)
        throw new InvalidDataException("page must be positive and pageSize must be between 1 and 100.");
    var total = items.Count;
    var pageItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
    return new(pageItems, page, pageSize, total, page * (long)pageSize < total, snapshot.AsOf,
        snapshot.Scope, snapshot.SourceRevision, snapshot.Warnings);
}

static int ParseQueryInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
static IResult? DeviceSiteScopeGuard(HttpContext context, string? requestedSite = null)
{
    var scope = context.RequestServices.GetRequiredService<P2B5SiteScopeStore>().Resolve(CurrentIdentity(context));
    if (!scope.IsAuthorized)
    {
        var error = scope.State switch
        {
            "CONFLICT" => "P2B_SITE_SCOPE_CONFLICT",
            "DENIED" => "P2B_SITE_SCOPE_DENIED",
            _ => "P2B_SITE_SCOPE_UNAVAILABLE"
        };
        return Results.Json(new { error, detail = scope.Detail }, statusCode: StatusCodes.Status403Forbidden);
    }

    if (!scope.IsGlobal && !string.IsNullOrWhiteSpace(requestedSite) &&
        !scope.AllowedSiteReferences.Contains(requestedSite.Trim()))
        return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = "The requested Site is outside the current user scope." },
            statusCode: StatusCodes.Status403Forbidden);

    context.Items[P2B5SiteScopeHttpContextKeys.Decision] = scope;
    return null;
}

static IResult DeviceScopeDenied(HttpContext context, Catalog catalog, string action, string detail)
{
    try
    {
        var identity = CurrentIdentity(context);
        catalog.TryRecordDeviceAuditEvent(identity.Username, action, "DENIED", "devices", null,
            Array.Empty<string>(), detail);
    }
    catch (Exception ex) { Console.Error.WriteLine($"DEVICE_SCOPE_AUDIT_FAILED:{action}:{ex.GetType().Name}"); }
    return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = "The requested Site is outside the current user scope." },
        statusCode: StatusCodes.Status403Forbidden);
}

static IResult? P2B3ReadGuard(HttpContext context)
{
    if (!P2B3ReadApiFeatureGate.IsEnabled()) return P2B3SchemaUnavailable("FEATURE_DISABLED");
    var identity = CurrentIdentity(context);
    var scope = context.RequestServices.GetRequiredService<P2B5SiteScopeStore>().Resolve(identity);
    if (!scope.IsAuthorized)
    {
        var error = scope.State switch
        {
            "CONFLICT" => "P2B_SITE_SCOPE_CONFLICT",
            "DENIED" => "P2B_SITE_SCOPE_DENIED",
            _ => "P2B_SITE_SCOPE_UNAVAILABLE"
        };
        return Results.Json(new { error, detail = scope.Detail }, statusCode: StatusCodes.Status403Forbidden);
    }

    var requestedSite = QueryValue(context.Request, "siteId");
    if (!scope.IsGlobal && requestedSite is not null && !scope.AllowedSiteReferences.Contains(requestedSite))
        return Results.Json(new { error = "P2B_SITE_SCOPE_DENIED", detail = "The requested Site is outside the current user scope." }, statusCode: StatusCodes.Status403Forbidden);

    context.Items[P2B5SiteScopeHttpContextKeys.Decision] = scope;
    return null;
}
static IResult? P2B4WriteGuard(HttpContext context, P2B4WriteRepository repository)
{
    var identity = CurrentIdentity(context);
    if (!string.Equals(identity.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "P2B4_ADMIN_WRITE_DENIED", detail = "The current Auth identity has no reliable site claims for P2B-4 writes." }, statusCode: StatusCodes.Status403Forbidden);
    if (!P2BFeatureGate.FromEnvironment().Enabled)
        return Results.Json(new { error = "P2B4_WRITE_UNAVAILABLE", reason = "READ_FEATURE_DISABLED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!P2B4WriteFeatureGate.EnvironmentEnabled())
        return Results.Json(new { error = "P2B4_WRITE_UNAVAILABLE", reason = "WRITE_GATE_DISABLED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!repository.IsAuthorityAvailable())
        return Results.Json(new { error = "P2B4_WRITE_UNAVAILABLE", reason = "ISOLATED_AUTHORITY_OR_V14_REQUIRED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    return null;
}
static IResult? P2B5ExportGuard(HttpContext context, P2B5ExportService exports)
{
    var identity = CurrentIdentity(context);
    if (!string.Equals(identity.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "P2B5_EXPORT_SCOPE_DENIED", detail = "Local export requires an explicit isolated SUPERADMIN export scope." }, statusCode: StatusCodes.Status403Forbidden);
    if (!P2BFeatureGate.FromEnvironment().Enabled || !P2B4WriteFeatureGate.EnvironmentEnabled())
        return Results.Json(new { error = "P2B5_EXPORT_UNAVAILABLE", reason = "ISOLATED_EXPORT_GATE_DISABLED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!exports.IsAuthorityAvailable())
        return Results.Json(new { error = "P2B5_EXPORT_UNAVAILABLE", reason = "ISOLATED_V14_OR_EXPORT_SCHEMA_REQUIRED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    return null;
}
static IResult P2B5ExportError(P2B5ExportException exception) =>
    Results.Json(new { error = exception.Code, detail = exception.Message }, statusCode: exception.StatusCode);
static IResult? P2B5RailReferenceGuard(HttpContext context, P2B5RailReferenceService rail)
{
    if (!IsSuperAdmin(context))
        return Results.Json(new { error = "RAIL_REFERENCE_SCOPE_DENIED", detail = "Rail reference import requires an explicit isolated SUPERADMIN scope." }, statusCode: StatusCodes.Status403Forbidden);
    if (!P2B4WriteFeatureGate.EnvironmentEnabled())
        return Results.Json(new { error = "RAIL_REFERENCE_UNAVAILABLE", reason = "ISOLATED_RAIL_GATE_DISABLED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (!rail.IsAuthorityAvailable())
        return Results.Json(new { error = "RAIL_REFERENCE_UNAVAILABLE", reason = "ISOLATED_V14_OR_RAIL_SCHEMA_REQUIRED" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    return null;
}
static IResult P2B5RailReferenceError(P2B5RailReferenceException exception) =>
    Results.Json(new { error = exception.Code, detail = exception.Message }, statusCode: exception.StatusCode);
static IResult P2B4WriteError(P2B4WriteException exception) =>
    Results.Json(new { error = exception.Code, detail = exception.Message, operationId = exception.OperationId }, statusCode: exception.StatusCode);
static IResult P2B3SchemaUnavailable(string reason) =>
    Results.Json(new { error = "P2B_SCHEMA_UNAVAILABLE", reason }, statusCode: StatusCodes.Status503ServiceUnavailable);
static IResult P2B3BadQuery(string detail) =>
    Results.Json(new { error = "INVALID_QUERY", detail }, statusCode: StatusCodes.Status400BadRequest);
static string? QueryValue(HttpRequest request, string name, string? fallback = null)
{
    var value = request.Query[name].ToString();
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
static int P2B4QueryLimit(HttpRequest request)
{
    var raw = QueryValue(request, "limit");
    if (raw is null) return 50;
    if (!int.TryParse(raw, out var limit) || limit is < 1 or > 100)
        throw new P2B4WriteException("INVALID_QUERY", "limit must be between 1 and 100.", 400);
    return limit;
}
static string? OptionalEnum(HttpRequest request, string name, params string[] allowed)
{
    var value = QueryValue(request, name);
    if (value is null) return null;
    if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
        throw new P2B3QueryException($"{name} is invalid.");
    return allowed.First(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
}
static long? AsOf(HttpRequest request)
{
    var value = QueryValue(request, "asOf");
    if (value is null) return null;
    if (!long.TryParse(value, out var parsed) || parsed < 0)
        throw new P2B3QueryException("asOf must be a non-negative Unix millisecond timestamp.");
    return parsed;
}
static WebUserIdentity CurrentIdentity(HttpContext context) =>
    context.Items[WebCookieNames.IdentityItem] as WebUserIdentity ?? throw new UnauthorizedAccessException("Web identity is missing.");
static P2B5SiteScopeDecision CurrentSiteScope(HttpContext context) =>
    context.Items[P2B5SiteScopeHttpContextKeys.Decision] as P2B5SiteScopeDecision ?? P2B5SiteScopeDecision.Global;
static bool IsSuperAdmin(HttpContext context) => string.Equals(CurrentIdentity(context).Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase);
static IResult DevicePermissionDenied(HttpContext context, Catalog catalog, string action, string subjectId = "devices")
{
    try
    {
        var identity = CurrentIdentity(context);
        catalog.TryRecordDeviceAuditEvent(identity.Username, action, "DENIED", subjectId, null, Array.Empty<string>(), "SUPERADMIN_REQUIRED");
    }
    catch (Exception ex) { Console.Error.WriteLine($"DEVICE_PERMISSION_AUDIT_FAILED:{action}:{ex.GetType().Name}"); }
    return Results.StatusCode(StatusCodes.Status403Forbidden);
}

static bool IsWriteRequest(string method) => method is "POST" or "PUT" or "PATCH" or "DELETE";

static bool IsValidCookieWrite(HttpContext context, WebAuthStore auth, string? sessionToken)
{
    var origin = context.Request.Headers.Origin.ToString();
    var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
    var csrfHeader = context.Request.Headers[WebCookieNames.CsrfHeader].ToString();
    var csrfCookie = context.Request.Cookies[WebCookieNames.Csrf];
    return !string.IsNullOrWhiteSpace(sessionToken) && string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(csrfCookie) && string.Equals(csrfHeader, csrfCookie, StringComparison.Ordinal) &&
        auth.ValidateCsrf(sessionToken, csrfHeader);
}

static void SetWebCookies(HttpContext context, string sessionToken, string csrfToken)
{
    context.Response.Cookies.Append(WebCookieNames.Session, sessionToken, WebCookieOptions(httpOnly: true));
    context.Response.Cookies.Append(WebCookieNames.Csrf, csrfToken, WebCookieOptions(httpOnly: false));
}

static CookieOptions WebCookieOptions(bool httpOnly) => new()
{
    HttpOnly = httpOnly,
    SameSite = SameSiteMode.Lax,
    Secure = false,
    IsEssential = true,
    Path = "/",
    MaxAge = TimeSpan.FromHours(12),
};

static async Task MonitorTerminalScopeAsync(TerminalSessionRuntime session, Func<bool> isAuthorized, CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
    {
        if (isAuthorized()) continue;
        await session.CloseForScopeRevocationAsync().ConfigureAwait(false);
        return;
    }
}

static async Task SendTerminalOutputAsync(WebSocket socket, TerminalSessionRuntime session, Func<bool> isAuthorized, CancellationToken cancellationToken)
{
    if (!isAuthorized()) return;
    var status = JsonSerializer.SerializeToUtf8Bytes(new { type = "status", session = session.Snapshot });
    await socket.SendAsync(status, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    await foreach (var output in session.Output.ReadAllAsync(cancellationToken))
    {
        if (!isAuthorized()) return;
        await socket.SendAsync(output, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }
}

static async Task ReceiveTerminalInputAsync(WebSocket socket, TerminalSessionRuntime session, Func<bool> isAuthorized, CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];
    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close || !isAuthorized()) return;
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage || result.Count > buffer.Length)
        {
            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "structured text messages only", cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "input":
                    if (root.TryGetProperty("data", out var keyboardData))
                        await session.SendRawInputAsync(keyboardData.GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    else
                        await session.SendCommandAsync(root.GetProperty("text").GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case "control":
                    await session.SendControlAsync(root.GetProperty("signal").GetString() ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case "resize":
                    await session.ResizeAsync(root.GetProperty("columns").GetInt32(), root.GetProperty("rows").GetInt32(), cancellationToken).ConfigureAwait(false);
                    break;
                case "ping":
                    break;
                default:
                    throw new InvalidOperationException("Unsupported terminal message type.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            try
            {
                var error = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = ex.Message });
                await socket.SendAsync(error, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            catch { return; }
        }
    }
}

internal static class WebCookieNames
{
    public const string Session = "netconsole_session";
    public const string Csrf = "netconsole_csrf";
    public const string CsrfHeader = "X-NC-CSRF";
    public const string IdentityItem = "netconsole_identity";
}

internal sealed record WebLoginRequest(string Username, string Password);
internal sealed record ModuleStateWriteRequest(bool Enabled);
internal sealed record WebPasswordRequest(string NewPassword, string? CurrentPassword = null, string? Username = null);
internal sealed record DeviceGroupRequest(string SiteId, string Name, string? Description = null);
internal sealed record RawIssueQuarantineRequest(string Detail);
internal sealed record H3cProfileRequest(string Id, string Host, int Port, string Username,
    string CredentialReference, string HostKeySha256, bool Enabled = true,
    string HostKeyUpdatePolicy = H3cHostKeyPolicy.Automatic, bool AllowLegacyAlgorithms = false, string TerminalEncoding = "AUTO",
    string TerminalProtocol = "AUTO", bool AllowTelnetFallback = false, string? Password = null, bool ClearPassword = false);
internal sealed record H3cOnboardRequest(string Host, string Username, string? CredentialReference = null,
    string? Protocol = "SSH", int? Port = null, bool AllowTelnetFallback = false, string? Password = null, string? SiteId = null,
    DeviceInventoryWriteRequest? Inventory = null);
internal sealed record H3cCredentialRequest(string? Password = null, bool ClearPassword = false);
internal sealed record H3cCompatibilityRequest(string HostKeyUpdatePolicy = H3cHostKeyPolicy.Automatic, bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO", string TerminalProtocol = "AUTO", bool AllowTelnetFallback = false);
internal sealed record H3cTaskDefinitionRequest(string Id, string ProfileId, string Command,
    long TimeoutMs = 30000, bool Enabled = true, IReadOnlyList<string>? Commands = null);
internal sealed record ConfigBackupRequest(IReadOnlyList<string>? ProfileIds, string? ConfigurationKind = null);
  internal sealed record H3cOfflineReplayRequest(IReadOnlyDictionary<string, string> CommandOutputs, long CapturedMs);
internal sealed record ZteProfileRequest(string Id, string Host, int Port, string Username,
    string CredentialReference = "", string HostKeySha256 = "", bool Enabled = true,
    string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO", string SiteId = "local", string? Password = null, bool ClearPassword = false);
internal sealed record ZteCredentialRequest(string? Password = null, bool ClearPassword = false);
internal sealed record SiteSshJumpHostRequest(string Host, int Port, string Username,
    string CredentialReference = "", string? Password = null, string? HostKeySha256 = null,
    bool Enabled = true, bool ClearPassword = false);
internal sealed record TerminalDeviceView(string Id, string Host, int Port, string Username, bool Enabled,
    IReadOnlyList<string> SupportedProtocols, string HostKeyUpdatePolicy, bool AllowLegacyAlgorithms,
    string TerminalEncoding, string TerminalProtocol, bool AllowTelnetFallback,
    string? LastSessionState, long? LastSessionStartedMs, string? LastDisconnectReason);
