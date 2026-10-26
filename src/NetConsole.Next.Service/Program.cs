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
var webBind = settings.WebBindAddress == "::" ? "[::]" : settings.WebBindAddress;
builder.WebHost.UseUrls($"http://{webBind}:{settings.WebPort}");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<P2B3ReadRepository>();
builder.Services.AddSingleton<WebAuthStore>();
builder.Services.AddSingleton<IRawSegmentWriterFactory, RawSegmentWriterFactory>();
IH3cCredentialProvider credentialProvider = builder.Environment.IsDevelopment() && settings.H3cDevelopmentEnvironmentCredentialsEnabled
    ? new EnvironmentH3cCredentialProvider(enabled: true)
    : OperatingSystem.IsWindows()
        ? new WindowsDpapiH3cCredentialProvider(settings.DataRoot)
        : new UnavailableH3cCredentialProvider();
builder.Services.AddSingleton<IH3cCredentialProvider>(credentialProvider);
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddSingleton<TerminalTicketStore>();
builder.Services.AddSingleton<TerminalRecordingService>();
builder.Services.AddSingleton<TerminalExportService>();
builder.Services.AddSingleton<DesktopHelperIpc>();
builder.Services.AddSingleton<HostToolRegistry>();
builder.Services.AddSingleton<HostToolLaunchService>();
builder.Services.AddSingleton<FileManagerService>();
builder.Services.AddSingleton<IArchiveDiskSpaceProvider, DriveInfoArchiveDiskSpaceProvider>();
builder.Services.AddSingleton<FileArchiveService>();
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
    }, sp.GetRequiredService<ITaskResultSink>()));
builder.Services.AddSingleton<IngressStats>();
builder.Services.AddSingleton<RuntimeStatus>();
builder.Services.AddSingleton<RuntimeResources>();
builder.Services.AddSingleton<FileLogDiagnostics>();
builder.Services.AddHostedService<ServiceRunLifecycle>();
builder.Services.AddHostedService<ProcessResourceMonitor>();
builder.Services.AddHostedService<UdpSyslogWorker>();
builder.Services.AddHostedService<RawIntegrityAuditService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileArchiveService>());

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
    var validRemote = remote is not null && (System.Net.IPAddress.IsLoopback(remote) || settings.WebLanEnabled);
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
});
RollingFileLoggerProvider? fileLoggerProvider = null;
try
{
    catalog.Initialize();
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
app.MapGet("/api/system/devices", (HttpRequest request, Catalog rawCatalog) =>
{
    var page = int.TryParse(request.Query["page"], out var requestedPage) ? requestedPage : 1;
    var pageSize = int.TryParse(request.Query["pageSize"], out var requestedPageSize) ? requestedPageSize : 20;
    return Results.Ok(rawCatalog.QueryManagedDeviceInventory(page, pageSize,
        request.Query["search"].ToString(), request.Query["status"].ToString(),
        request.Query["siteId"].ToString(), request.Query["groupId"].ToString()));
});
app.MapPost("/api/system/devices/batch", (HttpContext context, DeviceBatchOperationRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED");
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.CreateDeviceBatchOperation(request, actor)); }
    catch (DeviceBatchResourceLimitException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_FAILED", "FAILED", "devices", null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_FAILED", "CONFLICT", "devices", null, Array.Empty<string>(), "IDEMPOTENCY_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/device-operations/{id}", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id);
    try { return rawCatalog.GetDeviceBatchOperation(id, CurrentIdentity(context).Username) is { } operation ? Results.Ok(operation) : Results.NotFound(); }
    catch (UnauthorizedAccessException) { return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id); }
});
app.MapPost("/api/system/devices/batch/{id}/confirm", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id);
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.ConfirmDeviceBatchOperation(id, actor)); }
    catch (DeviceBatchConflictException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_CONFLICT", "CONFLICT", id, id, Array.Empty<string>(), "DEVICE_VERSION_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message, operationId = ex.OperationId }); }
    catch (DeviceBatchResourceLimitException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (UnauthorizedAccessException) { return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices/batch/{id}/cancel", (HttpContext context, string id, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id);
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.CancelDeviceBatchOperation(id, actor)); }
    catch (UnauthorizedAccessException) { return DevicePermissionDenied(context, rawCatalog, "DEVICE_BATCH_PERMISSION_DENIED", id); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_BATCH_CANCEL_FAILED", "CONFLICT", id, id, Array.Empty<string>(), "CANCEL_NOT_ALLOWED"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/devices/{id}", (string id, Catalog rawCatalog) =>
    rawCatalog.GetDeviceInventoryDetail(id) is { } detail ? Results.Ok(detail) : Results.NotFound());
app.MapGet("/api/system/devices/{id}/addresses", (HttpContext context, HttpRequest request, string id, P2B3ReadRepository repository) =>
{
    if (P2B3ReadGuard(context) is { } denied) return denied;
    try
    {
        if (!repository.DeviceExists(id)) return Results.NotFound();
        var page = P2B3PageOptions.From(request);
        var result = repository.ReadAddresses(id, page, QueryValue(request, "domainId"),
            OptionalEnum(request, "sourceKind", "PLANNED", "OBSERVED", "IMPORTED", "MANUAL"),
            OptionalEnum(request, "addressFamily", "IPV4", "IPV6"), QueryValue(request, "lifecycle", "ACTIVE"));
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
            QueryValue(request, "domainId"), QueryValue(request, "lifecycle", "ACTIVE")));
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
            QueryValue(request, "lifecycle", "ACTIVE"), QueryValue(request, "search")));
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
            QueryValue(request, "lifecycle", "ACTIVE"), vlanId, AsOf(request)));
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
            OptionalEnum(request, "resolutionState", "RESOLVED", "PENDING_DOMAIN", "CONFLICT"), AsOf(request)));
    }
    catch (P2B3QueryException ex) { return P2B3BadQuery(ex.Message); }
    catch (P2B3SchemaUnavailableException ex) { return P2B3SchemaUnavailable(ex.Message); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
    { return P2B3SchemaUnavailable("P2B_SCHEMA_UNAVAILABLE"); }
});
app.MapGet("/api/system/device-groups", (HttpRequest request, Catalog rawCatalog) =>
    Results.Ok(rawCatalog.QueryDeviceGroups(request.Query["siteId"].ToString())));
app.MapPost("/api/system/device-groups", (HttpContext context, DeviceGroupRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_GROUP_PERMISSION_DENIED", request.SiteId);
    var actor = CurrentIdentity(context).Username;
    try { return Results.Created("/api/system/device-groups", rawCatalog.CreateDeviceGroup(request.SiteId, request.Name, request.Description, actor)); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_GROUP_FAILED", "FAILED", request.SiteId, null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_GROUP_FAILED", "FAILED", request.SiteId, null, Array.Empty<string>(), "SITE_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_GROUP_FAILED", "CONFLICT", request.SiteId, null, Array.Empty<string>(), "GROUP_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices", (HttpContext context, DeviceInventoryWriteRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_WRITE_PERMISSION_DENIED", request.Id ?? "devices");
    var actor = CurrentIdentity(context).Username;
    try
    {
        var detail = rawCatalog.CreateDeviceInventory(request, actor);
        return Results.Created($"/api/system/devices/{detail.Item.Id}", detail);
    }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_CREATE_FAILED", "FAILED", request.Id ?? "devices", null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_CREATE_FAILED", "FAILED", request.Id ?? "devices", null, Array.Empty<string>(), "SITE_OR_GROUP_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_CREATE_FAILED", "CONFLICT", request.Id ?? "devices", null, Array.Empty<string>(), "DEVICE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapPut("/api/system/devices/{id}", (HttpContext context, string id, DeviceInventoryWriteRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_WRITE_PERMISSION_DENIED", id);
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.UpdateDeviceInventory(id, request, actor)); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_UPDATE_FAILED", "FAILED", id, null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_UPDATE_FAILED", "FAILED", id, null, Array.Empty<string>(), "DEVICE_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_UPDATE_FAILED", "CONFLICT", id, null, Array.Empty<string>(), "DEVICE_VERSION_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices/import/preview", (HttpContext context, DeviceImportPreviewRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_IMPORT_PERMISSION_DENIED", "devices");
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.PreviewDeviceImport(request.Csv, actor)); }
    catch (DeviceImportResourceLimitException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_PREVIEW_FAILED", "FAILED", "devices", null, Array.Empty<string>(), "RESOURCE_LIMIT"); return Results.Problem(ex.Message, statusCode: StatusCodes.Status429TooManyRequests); }
    catch (ArgumentException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_PREVIEW_FAILED", "FAILED", "devices", null, Array.Empty<string>(), "REQUEST_VALIDATION_FAILED"); return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/system/devices/import/commit", (HttpContext context, DeviceImportCommitRequest request, Catalog rawCatalog) =>
{
    if (!IsSuperAdmin(context)) return DevicePermissionDenied(context, rawCatalog, "DEVICE_IMPORT_PERMISSION_DENIED", request.PreviewId);
    var actor = CurrentIdentity(context).Username;
    try { return Results.Ok(rawCatalog.CommitDeviceImport(request.PreviewId, request.Confirm, actor)); }
    catch (UnauthorizedAccessException) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_COMMIT_FAILED", "FAILED", request.PreviewId, null, Array.Empty<string>(), "PREVIEW_OWNER_MISMATCH"); return Results.StatusCode(StatusCodes.Status403Forbidden); }
    catch (KeyNotFoundException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_COMMIT_FAILED", "FAILED", request.PreviewId, null, Array.Empty<string>(), "PREVIEW_NOT_FOUND"); return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { rawCatalog.TryRecordDeviceAuditEvent(actor, "DEVICE_IMPORT_COMMIT_FAILED", "CONFLICT", request.PreviewId, null, Array.Empty<string>(), "IMPORT_VERSION_OR_SCOPE_CONFLICT"); return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/system/tasks", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentTaskRuns()));
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
app.MapGet("/api/system/files/roots", (FileManagerService files) => Results.Ok(files.Roots()));
app.MapGet("/api/system/files/list", (HttpRequest request, FileManagerService files) =>
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
app.MapGet("/api/system/files/download", (HttpRequest request, FileManagerService files) =>
{
    try
    {
        var result = files.OpenDownload(request.Query["rootId"].ToString(), request.Query["path"].ToString());
        return Results.File(result.Stream, result.ContentType, result.DownloadName, enableRangeProcessing: true);
    }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or KeyNotFoundException or FileNotFoundException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/system/files/archive-jobs", (HttpContext context, ArchiveRequest request, FileArchiveService archives) =>
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
app.MapGet("/api/system/files/archive-jobs/{id}", (HttpContext context, string id, FileArchiveService archives) =>
    archives.Get(id, CurrentIdentity(context)) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapPost("/api/system/files/archive-jobs/{id}/cancel", (HttpContext context, string id, FileArchiveService archives) =>
    archives.Cancel(id, CurrentIdentity(context)) ? Results.Ok(new { cancelled = true }) : Results.NotFound());
app.MapGet("/api/system/files/archive-jobs/{id}/download", (HttpContext context, string id, FileArchiveService archives) =>
{
    var result = archives.OpenDownload(id, CurrentIdentity(context));
    return result is null ? Results.NotFound() : Results.File(result.Stream, "application/zip", result.Name, enableRangeProcessing: true);
});
if (settings.TerminalEnabled)
{
    app.MapGet("/api/system/terminal/devices", (Catalog rawCatalog, TerminalSessionManager manager) =>
    {
        var sessions = manager.Recent();
        var devices = rawCatalog.RecentH3cDeviceProfiles(100).Select(profile =>
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
    app.MapGet("/api/system/terminal/sessions", (HttpRequest request, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.QueryTerminalSessions(TerminalSessionQuery.FromQuery(request.Query))));
    app.MapGet("/api/system/terminal/sessions/{sessionId}", (string sessionId, Catalog rawCatalog) =>
    {
        var session = rawCatalog.GetTerminalSession(sessionId);
        return session is null ? Results.NotFound() : Results.Ok(session);
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/events", (string sessionId, Catalog rawCatalog, int? limit) =>
        Results.Ok(rawCatalog.TerminalEvents(sessionId, Math.Clamp(limit ?? 500, 1, 5000))));
    app.MapGet("/api/system/terminal/sessions/{sessionId}/recording/segments", (string sessionId, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.TerminalRecordingSegments(sessionId)));
    app.MapGet("/api/system/terminal/sessions/{sessionId}/recording/integrity", (string sessionId,
        TerminalRecordingService recordings) =>
    {
        try { return Results.Ok(recordings.Verify(sessionId)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapPost("/api/system/terminal/sessions/{sessionId}/recording/verify", (string sessionId,
        TerminalRecordingService recordings) =>
    {
        try { return Results.Ok(recordings.Verify(sessionId)); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidDataException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/exports", (string sessionId, Catalog rawCatalog) =>
        Results.Ok(rawCatalog.TerminalExports(sessionId)));
    app.MapGet("/api/system/terminal/sessions/{sessionId}/export", async (HttpContext context, string sessionId,
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
    app.MapGet("/api/system/terminal/sessions/{sessionId}/recording/replay", (string sessionId,
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
    app.MapPost("/api/system/terminal/sessions", (TerminalSessionRequest request, TerminalSessionManager manager) =>
    {
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
    app.MapPost("/api/system/terminal/sessions/{sessionId}/ticket", (string sessionId,
        TerminalSessionManager manager, TerminalTicketStore tickets) =>
    {
        if (manager.Get(sessionId) is null) return Results.NotFound();
        return Results.Ok(tickets.Issue(sessionId));
    });
    app.MapPost("/api/system/terminal/sessions/{sessionId}/close", async (string sessionId,
        TerminalSessionManager manager) =>
    {
        if (!await manager.CloseAsync(sessionId)) return Results.NotFound();
        return Results.Ok(new { sessionId, state = "CLOSED" });
    });
    app.MapGet("/api/system/terminal/sessions/{sessionId}/stream", async (HttpContext context,
        string sessionId, TerminalSessionManager manager, TerminalTicketStore tickets) =>
    {
        var expectedOrigins = new[] { $"http://127.0.0.1:{settings.WebPort}", $"http://localhost:{settings.WebPort}", $"http://[::1]:{settings.WebPort}" };
        var origin = context.Request.Headers.Origin.ToString();
        var lanOrigin = settings.WebLanEnabled && origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin) && parsedOrigin.Port == settings.WebPort;
        if (origin.Length > 0 && !expectedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) && !lanOrigin)
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
        var send = SendTerminalOutputAsync(socket, session, cancellation.Token);
        var receive = ReceiveTerminalInputAsync(socket, session, cancellation.Token);
        await Task.WhenAny(send, receive).ConfigureAwait(false);
        cancellation.Cancel();
        try { await Task.WhenAll(send, receive).ConfigureAwait(false); } catch { }
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        }
    });
}
if (settings.H3cEnabled)
{
    app.MapGet("/api/system/h3c/profiles", (Catalog rawCatalog) => Results.Ok(rawCatalog.RecentH3cDeviceProfiles()));
    app.MapPost("/api/system/h3c/profiles", (H3cProfileRequest request, Catalog rawCatalog) =>
    {
        try
        {
            rawCatalog.CreateH3cDeviceProfile(new(request.Id, request.Host, request.Port, request.Username,
                request.CredentialReference, request.HostKeySha256, request.Enabled, 0, 0,
                request.HostKeyUpdatePolicy, request.AllowLegacyAlgorithms, request.TerminalEncoding,
                request.TerminalProtocol, request.AllowTelnetFallback));
            return Results.Created($"/api/system/h3c/profiles/{request.Id}", rawCatalog.GetH3cDeviceProfile(request.Id));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Profile could not be saved ({ex.GetType().Name})." });
        }
    });
    app.MapPut("/api/system/h3c/profiles/{id}/compatibility", (string id, H3cCompatibilityRequest request,
        Catalog rawCatalog) =>
    {
        try
        {
            rawCatalog.SetH3cCompatibility(id, request.HostKeyUpdatePolicy, request.AllowLegacyAlgorithms,
                request.TerminalEncoding, request.TerminalProtocol, request.AllowTelnetFallback);
            return Results.Ok(rawCatalog.GetH3cDeviceProfile(id));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    });
    app.MapGet("/api/system/h3c/profiles/{id}/host-key-history", (string id, Catalog rawCatalog, int? limit) =>
        Results.Ok(rawCatalog.SshHostKeyHistory(id, Math.Clamp(limit ?? 100, 1, 500))));
    app.MapPost("/api/system/h3c/task-definitions", (H3cTaskDefinitionRequest request, Catalog rawCatalog) =>
    {
        if (request.TimeoutMs is < 100 or > 600000)
            return Results.BadRequest(new { error = "timeoutMs must be between 100 and 600000." });
        try
        {
            rawCatalog.CreateTaskDefinition(request.Id, "H3C_SSH_READONLY", request.ProfileId,
                request.Command, TimeSpan.FromMilliseconds(request.TimeoutMs), request.Enabled);
            return Results.Created($"/api/system/tasks/definitions/{request.Id}",
                rawCatalog.GetTaskDefinition(request.Id));
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Results.Conflict(new { error = $"Task definition could not be saved ({ex.GetType().Name})." });
        }
    });
    app.MapPost("/api/system/tasks/{definitionId}/runs", (string definitionId, Catalog rawCatalog,
        RuntimeStatus status, BoundedCollectionScheduler scheduler) =>
    {
        if (!rawCatalog.Available || !rawCatalog.RawWriteAvailable || !status.CollectionAllowed)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            if (rawCatalog.GetTaskDefinition(definitionId)?.TaskType != "H3C_SSH_READONLY")
                return Results.NotFound();
            if (!scheduler.TryScheduleDefinition(definitionId, out var runId))
                return Results.Problem("The bounded collection queue is full or shutting down.", statusCode: StatusCodes.Status429TooManyRequests);
            return Results.Accepted("/api/system/tasks", new { runId });
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    });
}
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
    return settings.WebLanEnabled && !string.IsNullOrWhiteSpace(host.Host);
}

static int ParseQueryInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
static IResult? P2B3ReadGuard(HttpContext context)
{
    if (!P2B3ReadApiFeatureGate.IsEnabled()) return P2B3SchemaUnavailable("FEATURE_DISABLED");
    var identity = CurrentIdentity(context);
    // Existing Auth has ADMIN/SUPERADMIN roles but no per-user site claims. Do not
    // invent a site scope for ADMIN; expose the new scoped reads only to the role
    // whose current contract is explicitly global.
    if (!string.Equals(identity.Role, "SUPERADMIN", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "P2B_SITE_SCOPE_UNAVAILABLE", detail = "The current Auth identity has no reliable site scope for P2B3 reads." }, statusCode: StatusCodes.Status403Forbidden);
    return null;
}
static IResult P2B3SchemaUnavailable(string reason) =>
    Results.Json(new { error = "P2B_SCHEMA_UNAVAILABLE", reason }, statusCode: StatusCodes.Status503ServiceUnavailable);
static IResult P2B3BadQuery(string detail) =>
    Results.Json(new { error = "INVALID_QUERY", detail }, statusCode: StatusCodes.Status400BadRequest);
static string? QueryValue(HttpRequest request, string name, string? fallback = null)
{
    var value = request.Query[name].ToString();
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
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

static async Task SendTerminalOutputAsync(WebSocket socket, TerminalSessionRuntime session, CancellationToken cancellationToken)
{
    var status = JsonSerializer.SerializeToUtf8Bytes(new { type = "status", session = session.Snapshot });
    await socket.SendAsync(status, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    await foreach (var output in session.Output.ReadAllAsync(cancellationToken))
        await socket.SendAsync(output, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
}

static async Task ReceiveTerminalInputAsync(WebSocket socket, TerminalSessionRuntime session, CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];
    while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close) return;
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
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
internal sealed record WebPasswordRequest(string NewPassword, string? CurrentPassword = null, string? Username = null);
internal sealed record DeviceGroupRequest(string SiteId, string Name, string? Description = null);
internal sealed record RawIssueQuarantineRequest(string Detail);
internal sealed record H3cProfileRequest(string Id, string Host, int Port, string Username,
    string CredentialReference, string HostKeySha256, bool Enabled = true,
    string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false, string TerminalEncoding = "AUTO",
    string TerminalProtocol = "AUTO", bool AllowTelnetFallback = false);
internal sealed record H3cCompatibilityRequest(string HostKeyUpdatePolicy = "STRICT", bool AllowLegacyAlgorithms = false,
    string TerminalEncoding = "AUTO", string TerminalProtocol = "AUTO", bool AllowTelnetFallback = false);
internal sealed record H3cTaskDefinitionRequest(string Id, string ProfileId, string Command,
    long TimeoutMs = 30000, bool Enabled = true);
internal sealed record TerminalDeviceView(string Id, string Host, int Port, string Username, bool Enabled,
    IReadOnlyList<string> SupportedProtocols, string HostKeyUpdatePolicy, bool AllowLegacyAlgorithms,
    string TerminalEncoding, string TerminalProtocol, bool AllowTelnetFallback,
    string? LastSessionState, long? LastSessionStartedMs, string? LastDisconnectReason);
