using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetConsole.Next.Service;

internal static class ConfigBackupScheduleTests
{
    public static async Task RunAsync()
    {
        PersistsOwnerTargetsAndDueTime();
        BoundsAndValidatesScheduleInput();
        RejectsUnsupportedOrHiddenTargets();
        await OnlyDueEnabledPlansStart();
        await RunsAllThreeReadOnlyKinds();
        await DoesNotOverlapOrReplayMissedIntervals();
        await RemovedProfilesStillReleaseCompletedBatchSlots();
        await DeletedRunningPlansReleaseSlotsAndRetentionPins();
        await CompletedMetadataBoundPreservesUnobservedScheduledRuns();
        RevalidatesRoleAndDisabledOwner();
        RevalidatesTargetAndSiteAssociation();
        await ReadsCurrentEndpointAndCredentialEachRun();
        await RetainsSafeFailureAndBatchReference();
        RestoresFutureDueAndClassifiesInterruptedRun();
        DisabledModulePreventsAutomaticAdmission();
        StorageFailureDoesNotClaimSuccess();
        await ManualRunPreservesCadenceAndPausedState();
        await ManualRunSharesOverlapAndCapacityLimits();
        ManualRunRevalidatesAuthorityBeforeAdmission();
        Console.WriteLine("CONFIG_BACKUP_SCHEDULE_TESTS=19/19 PASS");
    }

    private static void PersistsOwnerTargetsAndDueTime()
    {
        using var f = new Fixture();
        var item = f.Create();
        Assert(item is { Enabled: true, IntervalMinutes: 60, LastState: "NEVER_RUN", LastBatchId: null } &&
            item.NextRunMs == item.CreatedMs + 3_600_000 && item.OwnerUserId == f.Actor.UserId,
            "a new plan must wait a complete interval and have a durable owner");
        var json = File.ReadAllText(f.StorePath);
        Assert(!json.Contains("password", StringComparison.OrdinalIgnoreCase) && !json.Contains("session", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("REFERENCE", StringComparison.Ordinal) && !json.Contains("192.0.2.", StringComparison.Ordinal) &&
            !json.Contains("fixture-secret", StringComparison.Ordinal), "plan JSON must contain no password, endpoint, reference or session");
        var restored = f.Restart();
        Assert(restored.List(f.Actor, f.Scope, f.Profile).Single().NextRunMs == item.NextRunMs &&
            restored.List(f.Actor, f.Scope, "unrelated").Count == 0, "restart must preserve next due and exact profile filtering");
        var other = new WebUserIdentity("local-api-token", "local-api-token", "SUPERADMIN", "local-api-token");
        Assert(restored.List(other, f.Scope).Count == 0 && restored.SetEnabled(item.Id, false, other, f.Scope) is null &&
            !restored.Delete(item.Id, other, f.Scope), "a global role must not assume ownership of another user's plan");
        var hidden = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "another-site" }, "fixture");
        Assert(restored.List(f.Actor, hidden).Count == 0 && !restored.Delete(item.Id, f.Actor, hidden),
            "current Site scope must also filter plan metadata and mutations");
        var disabled = restored.SetEnabled(item.Id, false, f.Actor, f.Scope)!;
        Assert(!disabled.Enabled && disabled.NextRunMs is null && !f.Restart().List(f.Actor, f.Scope).Single().Enabled,
            "disabled state must survive restart");
        Assert(restored.Delete(item.Id, f.Actor, f.Scope) && f.Restart().List(f.Actor, f.Scope).Count == 0 &&
            f.TaskCount == 0, "delete must persist without starting a backup");
    }

    private static void BoundsAndValidatesScheduleInput()
    {
        using var f = new Fixture();
        foreach (var interval in new[] { 0, 59, 43201, int.MaxValue })
            Expect("CONFIG_BACKUP_SCHEDULE_INTERVAL_INVALID", () => f.Service.Create(new([f.Profile], "RUNNING", interval), f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_KIND_UNSUPPORTED", () => f.Service.Create(new([f.Profile], "SAVE_FORCE", 60), f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_SCHEDULE_TARGET_LIMIT", () => f.Service.Create(new([], "RUNNING", 60), f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_SCHEDULE_TARGET_LIMIT", () => f.Service.Create(new(Enumerable.Range(0, 33).Select(_ => Guid.NewGuid().ToString("N")).ToArray(), "RUNNING", 60), f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_PERMISSION_DENIED", () => f.Service.Create(new([f.Profile], "RUNNING", 60), f.Actor with { Role = "ADMIN" }, f.Scope));
        Expect("CONFIG_BACKUP_SCOPE_DENIED", () => f.Service.Create(new([f.Profile], "RUNNING", 60), f.Actor, P2B5SiteScopeDecision.Denied("fixture")));
        for (var index = 0; index < ConfigBackupScheduleService.MaximumSchedules; index++)
            f.Create(index == 0 ? 43200 : 60);
        Expect("CONFIG_BACKUP_SCHEDULE_LIMIT", () => f.Create());
        Assert(f.Restart().List(f.Actor, f.Scope).Count == 64 && f.TaskCount == 0,
            "both interval endpoints and the durable global plan limit must apply without a collection");
    }

    private static void RejectsUnsupportedOrHiddenTargets()
    {
        using var f = new Fixture();
        f.Execute("UPDATE device_inventory SET vendor='Huawei' WHERE terminal_profile_id=$id", f.Profile);
        Expect("CONFIG_BACKUP_TARGET_UNSUPPORTED", () => f.Create());
        f.Execute("UPDATE device_inventory SET vendor='H3C',device_type='FiT_aP' WHERE terminal_profile_id=$id", f.Profile);
        Expect("CONFIG_BACKUP_TARGET_UNSUPPORTED", () => f.Create());
        f.Execute("UPDATE device_inventory SET device_type='SWITCH',enabled=0 WHERE terminal_profile_id=$id", f.Profile);
        Expect("CONFIG_BACKUP_TARGET_NOT_VISIBLE", () => f.Create());
        Assert(f.Service.List(f.Actor, f.Scope).Count == 0 && f.TaskCount == 0,
            "unsupported, managed FIT-AP and disabled targets must not persist a plan or create a task");
    }

    private static async Task OnlyDueEnabledPlansStart()
    {
        using var f = new Fixture();
        var item = f.Create();
        f.Service.RunDueSchedules(item.NextRunMs!.Value - 1);
        Assert(f.TaskCount == 0 && f.Factory.Connections.Count == 0, "future plans must not connect");
        f.Service.SetEnabled(item.Id, false, f.Actor, f.Scope);
        f.Service.RunDueSchedules(item.NextRunMs.Value + 1000);
        Assert(f.TaskCount == 0, "disabled plans must not run even after the original due time");
        item = f.Service.SetEnabled(item.Id, true, f.Actor, f.Scope)!;
        var due = item.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        var result = await f.Finish(item.Id, due);
        Assert(result is { LastState: "SUCCEEDED", LastBatchId: not null, LastError: null } &&
            result.NextRunMs == due + 3_600_000 && f.Factory.Connections.Count == 1 && f.TaskCount == 1,
            "an enabled due plan must use one existing backup batch and retain its next due/result");
    }

    private static async Task RunsAllThreeReadOnlyKinds()
    {
        using var f = new Fixture();
        var items = new[] { ConfigBackupKinds.Running, ConfigBackupKinds.Saved, ConfigBackupKinds.StartupInfo }
            .Select(kind => f.Create(kind: kind)).ToArray();
        var due = items.Max(item => item.NextRunMs!.Value);
        f.Service.RunDueSchedules(due);
        foreach (var item in items)
        {
            Assert((await f.Finish(item.Id, due)).LastState == "SUCCEEDED", "each supported category must complete");
            Assert(f.Backups.List(f.Profile, f.Scope, item.ConfigurationKind).Count == 1,
                "scheduled batches must keep the existing category-specific version storage");
        }
        Assert(f.Factory.Commands.Order().SequenceEqual(new[] { "display current-configuration", "display saved-configuration", "display startup" }.Order()) &&
            f.TaskCount == 3, "only the three allowlisted read commands may execute; no save/system-view command");
    }

    private static async Task DoesNotOverlapOrReplayMissedIntervals()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = f.Create();
        var actual = item.NextRunMs!.Value + (long)TimeSpan.FromDays(5).TotalMilliseconds;
        f.Service.RunDueSchedules(actual);
        await WaitUntil(() => f.Factory.Commands.Count == 1);
        for (var index = 0; index < 6; index++) f.Service.RunDueSchedules(actual + index * 3_600_000L);
        Assert(f.Factory.Connections.Count == 1 && f.TaskCount == 1 &&
            f.Service.List(f.Actor, f.Scope).Single().NextRunMs == actual + 3_600_000,
            "repeated ticks during a long run must neither overlap nor replay missed periods");
        f.Factory.Hold.TrySetResult(true);
        var completed = await f.Finish(item.Id, actual);
        var later = actual + 5 * 3_600_000;
        f.Service.RunDueSchedules(later);
        completed = await f.Finish(item.Id, later);
        Assert(completed.LastState == "SUCCEEDED" && completed.NextRunMs == later + 3_600_000 &&
            f.Factory.Connections.Count == 2 && f.TaskCount == 2,
            "after completion a later tick starts only one current period");
    }

    private static void RevalidatesRoleAndDisabledOwner()
    {
        using var f = new Fixture();
        var item = f.Create();
        f.Execute("UPDATE web_users SET role='ADMIN' WHERE id=$id", f.Actor.UserId);
        f.Service.RunDueSchedules(item.NextRunMs!.Value);
        Expect("CONFIG_BACKUP_SCHEDULE_OWNER_REVOKED", () => f.Service.List(f.Actor, f.Scope));
        f.Execute("UPDATE web_users SET role='SUPERADMIN' WHERE id=$id", f.Actor.UserId);
        var blocked = f.Service.List(f.Actor, f.Scope).Single();
        Assert(blocked is { LastState: "BLOCKED", LastError: "CONFIG_BACKUP_SCHEDULE_OWNER_REVOKED", LastBatchId: null } &&
            f.TaskCount == 0 && f.Factory.Connections.Count == 0, "a stale SUPERADMIN identity must not authorize an automatic run");
        f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.Actor.UserId);
        f.Service.RunDueSchedules(blocked.NextRunMs!.Value);
        f.Execute("UPDATE web_users SET disabled=0 WHERE id=$id", f.Actor.UserId);
        Assert(f.Service.List(f.Actor, f.Scope).Single().LastState == "BLOCKED" && f.TaskCount == 0,
            "a disabled owner must also block the next run");
    }

    private static void RevalidatesTargetAndSiteAssociation()
    {
        using var f = new Fixture();
        var item = f.Create();
        f.Execute("INSERT INTO sites(id,name) VALUES($id,'Changed fixture site')", "changed-site");
        f.Execute("UPDATE collection_sources SET site_id='changed-site' WHERE id='h3c-' || $id", f.Profile);
        f.Service.RunDueSchedules(item.NextRunMs!.Value);
        var blocked = f.Service.List(f.Actor, f.Scope).Single();
        Assert(blocked.LastState == "BLOCKED" && f.TaskCount == 0,
            "a changed source/device Site relationship must be revalidated rather than reusing create-time scope");
        f.Execute("UPDATE collection_sources SET site_id='local' WHERE id='h3c-' || $id", f.Profile);
        f.Execute("UPDATE device_inventory SET device_type='FIT_AP' WHERE terminal_profile_id=$id", f.Profile);
        f.Service.RunDueSchedules(blocked.NextRunMs!.Value);
        blocked = f.Service.List(f.Actor, f.Scope).Single();
        Assert(blocked.LastError == "CONFIG_BACKUP_TARGET_UNSUPPORTED" && f.TaskCount == 0,
            "a target changed to managed FIT-AP after plan creation must never connect independently");
    }

    private static async Task RemovedProfilesStillReleaseCompletedBatchSlots()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = Enumerable.Range(0, 4).Select(_ => f.Create()).ToArray();
        var healthyProfile = Guid.NewGuid().ToString("N");
        f.Catalog.CreateH3cDeviceProfile(new(healthyProfile, "192.0.2.250", 22, "fixture", "SCHEDULE_SECOND_REFERENCE",
            string.Empty, true, 0, 0, TerminalProtocol: "SSH", Password: "fixture-second-secret"));
        var healthy = f.Service.Create(new([healthyProfile], "RUNNING", 61), f.Actor, f.Scope);
        var due = healthy.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        await WaitUntil(() => f.Factory.Commands.Count == 1);
        var admitted = f.Service.List(f.Actor, f.Scope).Where(item => item.LastState == "RUNNING").ToArray();
        Assert(admitted.Length == 4 && admitted.All(item => item.ProfileIds.Single() == f.Profile) &&
            f.Factory.Connections.All(item => item.Host != "192.0.2.250"),
            "only four schedules may be admitted even when a fifth is already due");
        var hidden = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "another-site" }, "fixture");
        foreach (var item in admitted)
        {
            Assert(f.Backups.GetBatch(item.LastBatchId!, f.Actor, hidden) is null &&
                f.Backups.GetScheduledBatchStatus(item.LastBatchId!, "another-owner") is null,
                "public Site scope and internal batch-owner checks must remain intact");
            var metadata = f.Backups.GetScheduledBatchStatus(item.LastBatchId!, f.Actor.UserId)!;
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert(!json.Contains("fixture-secret", StringComparison.Ordinal) &&
                !json.Contains("password", StringComparison.OrdinalIgnoreCase) && !json.Contains("output", StringComparison.OrdinalIgnoreCase),
                "internal monitoring may contain batch metadata, never configuration or authentication data");
        }
        // Actual referentially valid removal, while the first command is still
        // held and three batches are waiting on the existing per-profile gate.
        f.Execute("UPDATE device_inventory SET terminal_profile_id=NULL WHERE terminal_profile_id=$id; DELETE FROM h3c_device_profiles WHERE id=$id", f.Profile);
        Assert(admitted.All(item => f.Backups.GetBatch(item.LastBatchId!, f.Actor, f.Scope) is null),
            "removing the profile must reproduce the hidden public batch view");
        f.Service.RunDueSchedules(due);
        Assert(f.Factory.Connections.All(item => item.Host != "192.0.2.250"),
            "a hidden but still executing batch must not release its concurrency slot early");
        f.Factory.Hold.TrySetResult(true);
        var result = await f.Finish(healthy.Id, due);
        Assert(result.LastState == "SUCCEEDED" && f.Factory.Connections.Count(item => item.Host == "192.0.2.250") == 1,
            "actual completed batches must release their slots so another due plan can run");
        await WaitUntil(() => admitted.All(item =>
            f.Backups.GetScheduledBatchStatus(item.LastBatchId!, f.Actor.UserId)?.State is "SUCCEEDED" or "PARTIAL"));
        f.Service.RunDueSchedules(due);
        var document = JsonDocument.Parse(File.ReadAllText(f.StorePath));
        using (document)
        {
            var stored = document.RootElement.GetProperty("schedules").EnumerateArray()
                .Where(item => removed.Any(old => old.Id == item.GetProperty("id").GetString())).ToArray();
            Assert(stored.Length == 4 && stored.All(item => item.GetProperty("lastState").GetString() == "PARTIAL" &&
                item.GetProperty("lastCompletedMs").ValueKind == JsonValueKind.Number &&
                item.GetProperty("lastBatchId").ValueKind == JsonValueKind.String),
                "removed-target plans must retain their real failure completion and batch reference");
        }
        Assert(f.Service.List(f.Actor, f.Scope).Count == 5 && f.Service.List(f.Actor, hidden).Count == 0 &&
            f.Service.Delete(removed[0].Id, f.Actor, f.Scope),
            "a global owner may clean up its own orphaned plans while Site-limited views stay hidden");
    }

    private static async Task DeletedRunningPlansReleaseSlotsAndRetentionPins()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = Enumerable.Range(0, 4).Select(_ => f.Create()).ToArray();
        var waiting = f.Create(61);
        var due = waiting.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        await WaitUntil(() => f.Factory.Commands.Count == 1);
        var batches = f.Service.List(f.Actor, f.Scope).Where(item => item.LastState == "RUNNING")
            .Select(item => item.LastBatchId!).ToArray();
        foreach (var item in removed) Assert(f.Service.Delete(item.Id, f.Actor, f.Scope), "running plans must remain deletable");
        f.Service.RunDueSchedules(due);
        Assert(f.Service.List(f.Actor, f.Scope).Single().LastState == "NEVER_RUN" && f.Factory.Connections.Count == 1,
            "deleting a running plan must not free an executing device slot early");
        f.Factory.Hold.TrySetResult(true);
        Assert((await f.Finish(waiting.Id, due)).LastState == "SUCCEEDED" && f.TaskCount == 5,
            "deleted plans must release all slots after their actual batches finish");
        var metadata = SeedCompletedMetadata(f.Backups, 256);
        var pins = (HashSet<string>)typeof(ConfigBackupService).GetField("_scheduledPins", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Backups)!;
        f.Service.RunDueSchedules(due);
        Assert(batches.All(id => !pins.Contains(id)) && f.Restart().List(f.Actor, f.Scope).Single().Id == waiting.Id,
            "deleted plans must release retention pins without resurrecting durable plans");
        var prune = typeof(ConfigBackupService).GetMethod("PruneCompletedBatches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        prune.Invoke(f.Backups, null);
        Assert(metadata.Values.Count(item => item.CompletedMs is not null) == ConfigBackupService.MaximumCompletedBatchMetadata &&
            batches.All(id => !metadata.ContainsKey(id)), "released deleted-plan batches must be prunable under metadata pressure");
    }

    private static async Task ReadsCurrentEndpointAndCredentialEachRun()
    {
        using var f = new Fixture();
        var item = f.Create();
        f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.252',port=2222,credential_password='fixture-secret-two' WHERE id=$id", f.Profile);
        var due = item.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        var result = await f.Finish(item.Id, due);
        Assert(result.LastState == "SUCCEEDED" && f.Factory.Connections.Single() is
            { Host: "192.0.2.252", Port: 2222, Password: "fixture-secret-two" },
            "a plan must resolve the current endpoint and real device-record credential provider at execution");
        f.Execute("UPDATE h3c_device_profiles SET host='192.0.2.253',port=2223,credential_password='fixture-secret-three' WHERE id=$id", f.Profile);
        due = result.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        Assert((await f.Finish(item.Id, due)).LastState == "SUCCEEDED" && f.Factory.Connections.Last() is
            { Host: "192.0.2.253", Port: 2223, Password: "fixture-secret-three" } && f.Factory.Connections.Count == 2,
            "subsequent periods must not reuse a cached connection or credential");
        var json = File.ReadAllText(f.StorePath);
        Assert(!json.Contains("fixture-secret", StringComparison.Ordinal) && !json.Contains("192.0.2.", StringComparison.Ordinal),
            "updated endpoint and credentials must not enter schedule persistence");
    }

    private static async Task CompletedMetadataBoundPreservesUnobservedScheduledRuns()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = f.Create();
        var due = plan.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        await WaitUntil(() => f.Factory.Commands.Count == 1);
        var scheduledId = f.Service.List(f.Actor, f.Scope).Single().LastBatchId!;
        Assert(!f.Backups.AcknowledgeScheduledBatch(scheduledId, f.Actor.UserId),
            "a running scheduled batch must not lose its retention pin");
        var batches = SeedCompletedMetadata(f.Backups, 256);
        var manual = f.Backups.Start([f.Profile], f.Actor, f.Scope);
        Assert(batches.Values.Count(item => item.CompletedMs is not null) == ConfigBackupService.MaximumCompletedBatchMetadata &&
            f.Backups.GetScheduledBatchStatus(scheduledId, f.Actor.UserId) is { State: "RUNNING" } &&
            f.Backups.GetScheduledBatchStatus(manual.Id, f.Actor.UserId) is { State: "RUNNING" },
            "completed metadata must be bounded while running scheduled and manual batches remain intact");
        f.Factory.Hold.TrySetResult(true);
        await WaitUntil(() => f.Backups.GetScheduledBatchStatus(scheduledId, f.Actor.UserId)?.State == "SUCCEEDED" &&
            f.Backups.GetScheduledBatchStatus(manual.Id, f.Actor.UserId)?.State == "SUCCEEDED");
        Assert(!f.Backups.AcknowledgeScheduledBatch(scheduledId, "another-owner"),
            "only the original owner may acknowledge terminal scheduled metadata");

        // Fail the terminal-result write, then apply completion-metadata pressure.
        // A batch must remain pinned until its scheduler actually persists it.
        var part = f.StorePath + ".part";
        Directory.CreateDirectory(part);
        ConfigBackupBatch pressure;
        try
        {
            Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => f.Service.RunDueSchedules(due));
            SeedCompletedMetadata(f.Backups, 256);
            pressure = f.Backups.Start([f.Profile], f.Actor, f.Scope);
            Assert(f.Backups.GetScheduledBatchStatus(scheduledId, f.Actor.UserId) is { State: "SUCCEEDED" } &&
                batches.Values.Count(item => item.CompletedMs is not null && item.Id != scheduledId) == ConfigBackupService.MaximumCompletedBatchMetadata,
                "an unobserved terminal result must survive pruning even after a failed persistence attempt");
        }
        finally { Directory.Delete(part); }
        var observed = await f.Finish(plan.Id, due);
        Assert(observed is { LastState: "SUCCEEDED", LastBatchId: not null } && observed.LastBatchId == scheduledId &&
            f.Backups.GetScheduledBatchStatus(scheduledId, f.Actor.UserId) is null,
            "a successfully persisted terminal result may release its pin and age out of memory");
        await WaitUntil(() => f.Backups.GetScheduledBatchStatus(pressure.Id, f.Actor.UserId)?.State == "SUCCEEDED");
        Assert(batches.Values.Count(item => item.CompletedMs is not null) == ConfigBackupService.MaximumCompletedBatchMetadata &&
            f.Factory.Connections.Count == 3 && f.Backups.List(f.Profile, f.Scope).Count == 3,
            "the limiter test uses three synthetic connections; pruning metadata must preserve configuration version files");
    }

    private static ConcurrentDictionary<string, ConfigBackupService.BatchState> SeedCompletedMetadata(ConfigBackupService backups, int count)
    {
        // Populate historical metadata directly instead of making hundreds of
        // synthetic device connections just to test a memory-retention boundary.
        var field = typeof(ConfigBackupService).GetField("_batches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batches = (ConcurrentDictionary<string, ConfigBackupService.BatchState>)field.GetValue(backups)!;
        for (var index = 0; index < count; index++)
        {
            var batch = new ConfigBackupService.BatchState(Guid.NewGuid().ToString("N"), "metadata-fixture",
                long.MaxValue - index, Array.Empty<ConfigBackupService.BatchItemState>(), ConfigBackupKinds.Running);
            batch.Complete();
            batches[batch.Id] = batch;
        }
        return batches;
    }

    private static async Task RetainsSafeFailureAndBatchReference()
    {
        using var f = new Fixture();
        f.Factory.Failure = new InvalidOperationException("password=fixture-secret-error");
        var item = f.Create();
        var due = item.NextRunMs!.Value;
        f.Service.RunDueSchedules(due);
        var result = await f.Finish(item.Id, due);
        Assert(result is { LastState: "PARTIAL", LastBatchId: not null, LastCompletedMs: not null, LastError: not null } &&
            f.Backups.List(f.Profile, f.Scope).Count == 0, "a failed existing batch must keep a safe error and batch reference without a snapshot");
        Assert(!File.ReadAllText(f.StorePath).Contains("fixture-secret-error", StringComparison.Ordinal) &&
            f.Restart().List(f.Actor, f.Scope).Single().LastBatchId == result.LastBatchId,
            "arbitrary exception messages must not persist and recent batch metadata must survive restart");
    }

    private static void RestoresFutureDueAndClassifiesInterruptedRun()
    {
        using var f = new Fixture();
        var item = f.Create();
        var restored = f.Restart();
        restored.RunDueSchedules(item.NextRunMs!.Value - 1);
        Assert(f.TaskCount == 0 && restored.List(f.Actor, f.Scope).Single().NextRunMs == item.NextRunMs,
            "normal restart must preserve a future due time without recapturing");
        var interrupted = item with { LastState = "RUNNING", LastBatchId = Guid.NewGuid().ToString("N"),
            LastRunMs = item.CreatedMs - 7_200_000, NextRunMs = item.CreatedMs - 3_600_000 };
        File.WriteAllText(f.StorePath, JsonSerializer.Serialize(new { version = 1, schedules = new[] { interrupted } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        restored = f.Restart();
        var recovered = restored.List(f.Actor, f.Scope).Single();
        Assert(recovered is { LastState: "INTERRUPTED", LastError: "CONFIG_BACKUP_SCHEDULE_RESTARTED" } &&
            recovered.NextRunMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "an interrupted run must be classified and wait a new interval rather than replaying a lost batch");
        restored.RunDueSchedules(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert(f.TaskCount == 0, "restart recovery must not duplicate an interrupted run immediately");
    }

    private static void StorageFailureDoesNotClaimSuccess()
    {
        using var f = new Fixture();
        var item = f.Create();
        var part = f.StorePath + ".part";
        Directory.CreateDirectory(part); // A deterministic write failure on every supported platform.
        try
        {
            Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => f.Service.SetEnabled(item.Id, false, f.Actor, f.Scope));
            Assert(f.Service.List(f.Actor, f.Scope).Single().Enabled, "failed persistence must not change in-memory enabled state");
            Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => f.Service.Delete(item.Id, f.Actor, f.Scope));
            Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => f.Service.RunDueSchedules(item.NextRunMs!.Value));
            Assert(f.TaskCount == 0 && f.Factory.Connections.Count == 0 &&
                f.Restart().List(f.Actor, f.Scope).Single().LastState == "NEVER_RUN",
                "a failed durable admission claim must never connect or report successful deletion");
        }
        finally { Directory.Delete(part); }
        File.WriteAllText(f.StorePath, "{broken");
        var damaged = f.Restart();
        Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => damaged.List(f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => damaged.Create(new([f.Profile], "RUNNING", 60), f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => damaged.SetEnabled(item.Id, false, f.Actor, f.Scope));
        Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => damaged.Delete(item.Id, f.Actor, f.Scope));
        damaged.RunDueSchedules(item.NextRunMs!.Value);
        Assert(File.ReadAllText(f.StorePath) == "{broken" && f.TaskCount == 0 &&
            f.Catalog.GetActiveTaskActor(f.Actor.UserId) is not null,
            "corrupt persistence must be retained and isolated without stopping the catalog or other service features");
    }

    private static void DisabledModulePreventsAutomaticAdmission()
    {
        using var f = new Fixture();
        var item = f.Create();
        f.Catalog.SetModuleEnabled("config-backups", false, "fixture");
        f.Service.RunDueSchedules(item.NextRunMs!.Value);
        Expect("CONFIG_BACKUP_MODULE_DISABLED", () => f.Create());
        Expect("CONFIG_BACKUP_MODULE_DISABLED", () => f.Service.SetEnabled(item.Id, true, f.Actor, f.Scope));
        Assert(f.Service.List(f.Actor, f.Scope).Single().LastState == "NEVER_RUN" && f.TaskCount == 0 &&
            f.Factory.Connections.Count == 0, "disabling the module must prevent background and direct-service admission");
        Assert(f.Service.SetEnabled(item.Id, false, f.Actor, f.Scope) is { Enabled: false },
            "an existing plan may still be disabled while the module is off");
    }

    private static async Task ManualRunPreservesCadenceAndPausedState()
    {
        using var f = new Fixture();
        var plan = f.Create();
        var admitted = f.Service.RunNow(plan.Id, f.Actor, f.Scope)!;
        Assert(admitted is { LastState: "RUNNING", LastBatchId: not null } && admitted.NextRunMs == plan.NextRunMs &&
            admitted.Enabled && f.Restart().List(f.Actor, f.Scope).Single().LastState == "INTERRUPTED",
            "manual admission must persist a real batch and preserve cadence, including restart classification");
        var done = await f.Finish(plan.Id, plan.CreatedMs);
        Assert(done is { LastState: "SUCCEEDED", LastCompletedMs: not null } && done.NextRunMs == plan.NextRunMs &&
            done.LastBatchId == admitted.LastBatchId && f.Backups.GetBatch(done.LastBatchId!, f.Actor, f.Scope) is not null,
            "manual completion must retain a browsable real batch using normal scheduled observation");
        f.Service.SetEnabled(plan.Id, false, f.Actor, f.Scope);
        admitted = f.Service.RunNow(plan.Id, f.Actor, f.Scope)!;
        Assert(!admitted.Enabled && admitted.NextRunMs is null && admitted.LastBatchId != done.LastBatchId,
            "a paused plan may run explicitly once without becoming enabled");
        done = await f.Finish(plan.Id, plan.CreatedMs);
        Assert(done is { Enabled: false, NextRunMs: null, LastState: "SUCCEEDED" } && f.Factory.Connections.Count == 2,
            "manual completion must not schedule another capture or enable a paused plan");
    }

    private static async Task ManualRunSharesOverlapAndCapacityLimits()
    {
        using var f = new Fixture();
        f.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var plans = Enumerable.Range(0, 5).Select(_ => f.Create()).ToArray();
        f.Service.RunNow(plans[0].Id, f.Actor, f.Scope);
        Expect("CONFIG_BACKUP_SCHEDULE_ALREADY_RUNNING", () => f.Service.RunNow(plans[0].Id, f.Actor, f.Scope));
        f.Service.RunDueSchedules(plans[0].NextRunMs!.Value);
        Expect("CONFIG_BACKUP_SCHEDULE_ALREADY_RUNNING", () => f.Service.RunNow(plans[0].Id, f.Actor, f.Scope));
        // The due tick and manual path share four slots, regardless of which wins admission.
        foreach (var plan in plans.Skip(1))
        {
            var current = f.Service.List(f.Actor, f.Scope).Single(item => item.Id == plan.Id);
            if (current.LastState == "NEVER_RUN")
            {
                if (f.Service.List(f.Actor, f.Scope).Count(item => item.LastState == "RUNNING") < 4)
                    f.Service.RunNow(plan.Id, f.Actor, f.Scope);
                else
                    Expect("CONFIG_BACKUP_SCHEDULE_ACTIVE_LIMIT", () => f.Service.RunNow(plan.Id, f.Actor, f.Scope));
            }
        }
        Assert(f.Service.List(f.Actor, f.Scope).Count(item => item.LastState == "RUNNING") == 4,
            "automatic and manual admission must share both same-plan overlap and global capacity limits");
        f.Factory.Hold.TrySetResult(true);
        foreach (var plan in f.Service.List(f.Actor, f.Scope).Where(item => item.LastState == "RUNNING"))
            await f.Finish(plan.Id, plans[0].CreatedMs);
        Assert(f.TaskCount == 4, "exactly the four admitted batches must execute after the per-profile gate releases");
    }

    private static void ManualRunRevalidatesAuthorityBeforeAdmission()
    {
        using var f = new Fixture();
        var plan = f.Create();
        var other = new WebUserIdentity("local-api-token", "local-api-token", "SUPERADMIN", "local-api-token");
        Assert(f.Service.RunNow(plan.Id, other, f.Scope) is null && f.Service.RunNow("missing", f.Actor, f.Scope) is null,
            "manual execution must not expose or assume another owner's plan");
        Expect("CONFIG_BACKUP_PERMISSION_DENIED", () => f.Service.RunNow(plan.Id, f.Actor with { Role = "ADMIN" }, f.Scope));
        Expect("CONFIG_BACKUP_SCOPE_DENIED", () => f.Service.RunNow(plan.Id, f.Actor, P2B5SiteScopeDecision.Denied("fixture")));
        f.Catalog.SetModuleEnabled("config-backups", false, "fixture");
        Expect("CONFIG_BACKUP_MODULE_DISABLED", () => f.Service.RunNow(plan.Id, f.Actor, f.Scope));
        f.Catalog.SetModuleEnabled("config-backups", true, "fixture");
        f.Execute("UPDATE h3c_device_profiles SET enabled=0 WHERE id=$id", f.Profile);
        Expect("CONFIG_BACKUP_TARGET_NOT_VISIBLE", () => f.Service.RunNow(plan.Id, f.Actor, f.Scope));
        f.Execute("UPDATE h3c_device_profiles SET enabled=1 WHERE id=$id", f.Profile);
        var part = f.StorePath + ".part";
        Directory.CreateDirectory(part);
        try { Expect("CONFIG_BACKUP_SCHEDULE_STORE_UNAVAILABLE", () => f.Service.RunNow(plan.Id, f.Actor, f.Scope)); }
        finally { Directory.Delete(part); }
        f.Execute("UPDATE web_users SET disabled=1 WHERE id=$id", f.Actor.UserId);
        Expect("CONFIG_BACKUP_SCHEDULE_OWNER_REVOKED", () => f.Service.RunNow(plan.Id, f.Actor, f.Scope));
        Assert(f.TaskCount == 0 && f.Factory.Connections.Count == 0 &&
            !File.ReadAllText(f.StorePath).Contains("lastState\":\"RUNNING", StringComparison.Ordinal),
            "invalid authority, target or durable storage must block manual execution before any task or connection");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public WebUserIdentity Actor { get; }
        public P2B5SiteScopeDecision Scope => P2B5SiteScopeDecision.Global;
        public Factory Factory { get; } = new();
        public ConfigBackupService Backups { get; }
        public ConfigBackupScheduleService Service { get; }
        private readonly P2B5SiteScopeStore _scopes;
        private readonly TaskSiteScopeAuthorizer _authorizer;
        private readonly BoundedCollectionScheduler _scheduler;
        public string StorePath => Path.Combine(Root, "backup", "config-schedules.json");
        public long TaskCount => long.Parse(Scalar("SELECT COUNT(*) FROM task_definitions"), System.Globalization.CultureInfo.InvariantCulture);

        public Fixture()
        {
            var repository = new DirectoryInfo(File.Exists(Path.Combine(Environment.CurrentDirectory, "tools", "workspace_paths.py"))
                ? Environment.CurrentDirectory : AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "tools", "workspace_paths.py")))
                repository = repository.Parent ?? throw new InvalidOperationException("Repository root unavailable.");
            Root = Path.Combine(repository.Parent!.FullName, "test-data", "next-config-backup-schedules-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings); Catalog.Initialize();
            Actor = Catalog.GetActiveTaskActor(Scalar("SELECT id FROM web_users WHERE username='superadmin'"))!;
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.251", 22, "fixture", "SCHEDULE_FIXTURE_REFERENCE",
                string.Empty, true, 0, 0, TerminalProtocol: "SSH", Password: "fixture-secret-one"));
            _scopes = new(Settings);
            _authorizer = new(Catalog, _scopes);
            _scheduler = new(new H3cSshDeviceSessionFactory(Catalog, new DeviceRecordH3cCredentialProvider(Catalog), Factory),
                2, 8, Catalog, resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: _authorizer);
            Backups = new(Catalog, _scheduler, _authorizer, Settings);
            Service = Restart();
        }

        public ConfigBackupScheduleService Restart() => new(Catalog, _scopes, _authorizer, Backups, Settings,
            NullLogger<ConfigBackupScheduleService>.Instance);
        public ConfigBackupScheduleSnapshot Create(int interval = 60, string kind = ConfigBackupKinds.Running) =>
            Service.Create(new([Profile], kind, interval), Actor, Scope);
        public async Task<ConfigBackupScheduleSnapshot> Finish(string id, long nowMs)
        {
            ConfigBackupScheduleSnapshot? snapshot = null;
            await WaitUntil(() =>
            {
                Service.RunDueSchedules(nowMs);
                snapshot = Service.List(Actor, Scope).Single(item => item.Id == id);
                return snapshot.LastState is "SUCCEEDED" or "PARTIAL" or "BLOCKED" or "INTERRUPTED";
            });
            return snapshot!;
        }
        public void Execute(string sql, string id)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = sql; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
        public string Scalar(string sql)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }
        private SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "catalog.db"), Pooling = false }.ToString());
            db.Open(); return db;
        }
        public void Dispose()
        {
            Factory.Hold?.TrySetResult(true);
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Service.Dispose();
            SqliteConnection.ClearAllPools();
            var resolvedRoot = Path.GetFullPath(Root);
            var allowedParent = Path.Combine(Directory.GetParent(Root)!.Parent!.FullName, "test-data") + Path.DirectorySeparatorChar;
            if (!resolvedRoot.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith("next-config-backup-schedules-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its dedicated workspace test-data directory.");
            if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, true);
        }
    }

    private sealed record Connection(string Host, int Port, string Password);
    private sealed class Factory : IH3cSshTransportFactory
    {
        public ConcurrentQueue<Connection> Connections { get; } = new();
        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource<bool>? Hold { get; set; }
        public Exception? Failure { get; set; }
        public IH3cSshTransport Create(H3cDeviceProfileSnapshot profile, string password)
        {
            Connections.Enqueue(new(profile.Host, profile.Port, password));
            return new Transport(this);
        }
        private sealed class Transport(Factory owner) : IH3cSshTransport
        {
            public bool IsConnected { get; private set; }
            public Task ConnectAsync(Func<string?, string?, bool> verifyHostKey, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); IsConnected = true; return Task.CompletedTask; }
            public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                owner.Commands.Enqueue(command);
                if (owner.Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
                if (owner.Failure is { } failure) throw failure;
                return command == H3cCommandPolicy.DisplayStartup
                    ? "Current startup saved-configuration file: NULL\nNext main startup saved-configuration file: flash:/startup.cfg\nNext backup startup saved-configuration file: NULL\n"
                    : "#\nsysname schedule-fixture\ninterface GigabitEthernet1/0/1\n description fixture\n#\nreturn\n";
            }
            public void Abort() => IsConnected = false;
            public void Dispose() => IsConnected = false;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (ConfigBackupException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
