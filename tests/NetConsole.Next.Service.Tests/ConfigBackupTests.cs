using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ConfigBackupTests
{
    public static async Task RunAsync()
    {
        using var fixture = new Fixture();
        var actor = new WebUserIdentity("local-api-token", "local-api-token", "SUPERADMIN", "test");
        var first = await fixture.Backup(actor);
        Assert(first.State == "SUCCEEDED" && first.Succeeded == 1, "configuration backup batch must complete");
        var firstSnapshot = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global).Single();
        Assert(firstSnapshot.Version == 1 && firstSnapshot.CanDownload && firstSnapshot.ByteCount > 0,
            "first backup must persist a downloadable versioned snapshot");
        var firstDownload = fixture.Backups.OpenDownload(firstSnapshot.Id, P2B5SiteScopeDecision.Global)
            ?? throw new InvalidOperationException("first backup download must open");
        using (var reader = new StreamReader(firstDownload.Stream))
            Assert(!reader.ReadToEnd().Contains("super-secret", StringComparison.Ordinal),
                "configuration backup must redact secret values before persistence");

        var forged = firstSnapshot with
        {
            Id = Guid.NewGuid().ToString("N"), DownloadName = "..\\..\\catalog.db", ByteCount = 1,
            Sha256 = new string('0', 64)
        };
        var manifestPath = Path.Combine(fixture.Root, "backup", "config", fixture.Profile, forged.Id + ".json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(forged, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert(fixture.Backups.OpenDownload(forged.Id, P2B5SiteScopeDecision.Global) is null,
            "forged backup manifests must not allow download path traversal");

        fixture.Factory.Output = "<H3C>sysname changed\ninterface GigabitEthernet1/0/1\n description changed\n</H3C>\n";
        var second = await fixture.Backup(actor);
        Assert(second.State == "SUCCEEDED" && second.Succeeded == 1, "second configuration backup must complete");
        var history = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global);
        Assert(history.Count == 2 && history.Any(item => item.Version == 2), "backup history must retain both versions");
        var latest = history.Single(item => item.Version == 2);
        var diff = fixture.Backups.Diff(firstSnapshot.Id, latest.Id, P2B5SiteScopeDecision.Global);
        Assert(diff is { Identical: false, AddedLines: > 0, RemovedLines: > 0 },
            "configuration diff must expose changed lines without returning full secrets");
        var download = fixture.Backups.OpenDownload(latest.Id, P2B5SiteScopeDecision.Global)
            ?? throw new InvalidOperationException("authorized backup download must open");
        download.Stream.Dispose();
        Assert(fixture.Catalog.QueryH3cFacts(fixture.Profile, history: true)
            .All(item => item.Kind != "command.display.current-configuration"),
            "configuration backups must not publish device configuration into the Facts UI projection");
        RejectUnsupportedTargets(actor);
        RejectManagedFitApTargets(actor);
        await VerifyLineDiffs(actor);
        await VerifyConfigurationKinds(actor);
        await VerifyInvalidSavedOutputs(actor);
        await VerifyPrivateKeyRedaction(actor);
        VerifyStartupFileSupport();
        await VerifyStartupBodyCollection(actor);
        await RejectReparseBackupDirectory(actor);
        await RejectReparseBackupReads(actor);
        Console.WriteLine("CONFIG_BACKUP_TESTS=12/12 PASS");
    }

    private static async Task RejectReparseBackupReads(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var batch = await fixture.Backup(actor);
        var snapshotId = batch.Items.Single().SnapshotId!;
        var opened = fixture.Backups.OpenDownload(snapshotId, P2B5SiteScopeDecision.Global)
            ?? throw new InvalidOperationException("the ordinary backup must remain downloadable before relocating its directory");
        opened.Stream.Dispose();
        var configDirectory = Path.Combine(fixture.Root, "backup", "config");
        var relocatedDirectory = Path.Combine(fixture.Root, "relocated-config");
        Directory.Move(configDirectory, relocatedDirectory);
        try
        {
            if (!TryCreateDirectoryLink(configDirectory, relocatedDirectory))
            {
                Console.WriteLine("CONFIG_BACKUP_REPARSE_READ=SKIPPED_NO_LINK_PERMISSION");
                return;
            }
            Assert(fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global).Count == 0 &&
                fixture.Backups.Get(snapshotId, P2B5SiteScopeDecision.Global) is null &&
                fixture.Backups.OpenDownload(snapshotId, P2B5SiteScopeDecision.Global) is null &&
                fixture.Backups.Diff(snapshotId, snapshotId, P2B5SiteScopeDecision.Global) is null,
                "backup history, lookup, download and diff must reject an ancestor reparse point");
            Console.WriteLine("CONFIG_BACKUP_REPARSE_READ=PASS");
        }
        finally
        {
            RemoveDirectoryLink(configDirectory);
            Directory.Move(relocatedDirectory, configDirectory);
        }
    }

    private static async Task RejectReparseBackupDirectory(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var profileDirectory = Path.Combine(fixture.Root, "backup", "config", fixture.Profile);
        var outside = Path.Combine(Directory.GetParent(fixture.Root)!.FullName,
            "next-config-backup-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(profileDirectory)!);
        Directory.CreateDirectory(outside);
        try
        {
            if (!TryCreateDirectoryLink(profileDirectory, outside))
            {
                Console.WriteLine("CONFIG_BACKUP_REPARSE_PATH=SKIPPED_NO_LINK_PERMISSION");
                return;
            }

            var batch = await fixture.Backup(actor);
            Assert(batch is { State: "PARTIAL" } && batch.Items.Single() is
                { State: "FAILED", Error: "CONFIG_BACKUP_STORAGE_PATH_UNSAFE" },
                "a backup profile directory reparse point must fail closed with a stable storage error");
            Assert(!Directory.EnumerateFileSystemEntries(outside).Any(),
                "backup output must never be written through a reparse point outside its data root");
            Console.WriteLine("CONFIG_BACKUP_REPARSE_PATH=PASS");
        }
        finally
        {
            RemoveDirectoryLink(profileDirectory);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
    }

    private static void RemoveDirectoryLink(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: false); }
        catch (IOException) when (OperatingSystem.IsWindows()) { }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
    }

    private static void VerifyStartupFileSupport()
    {
        var reference = StartupConfigurationSupport.Inspect(
            "Current startup saved-configuration file: NULL\n" +
            "Main startup saved-configuration file: flash:/startup.cfg,\n" +
            "Next main startup saved-configuration file: NULL\n");
        Assert(reference is { FileReference: "flash:/startup.cfg", ReadCommand: "more flash:/startup.cfg",
                BodyReadSupported: true, RestoreError: "CONFIG_RESTORE_UNSUPPORTED" } &&
            H3cCommandPolicy.IsAllowed(reference.ReadCommand),
            "a valid display startup pointer must produce one allowlisted read command");

        foreach (var value in new[] { "flash:/../secret.cfg", "flash:/startup.cfg;save force",
            "flash:/startup.txt", "flash:/startup.cfg extra", "http:/startup.cfg", "flash:/startup.cfg\nreboot" })
        {
            Assert(!H3cCommandPolicy.TryCreateStartupFileReadCommand(value, out _) &&
                !H3cCommandPolicy.IsAllowed("more " + value),
                "startup file reads must reject traversal, injection, non-config paths and unsupported schemes");
        }
        Assert(StartupConfigurationSupport.Inspect("Main startup saved-configuration file: NULL\n") is
            { BodyReadSupported: false, RestoreError: "CONFIG_BACKUP_STARTUP_FILE_UNAVAILABLE" },
            "a NULL startup pointer must fail closed without scheduling a body read");
    }

    private static async Task VerifyStartupBodyCollection(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        fixture.Factory.Outputs[H3cCommandPolicy.DisplayStartup] =
            "<H3C>display startup\nMain startup saved-configuration file: flash:/startup.cfg\n<H3C>\n";
        fixture.Factory.Outputs["more flash:/startup.cfg"] =
            "<H3C>more flash:/startup.cfg\n#\nsysname startup-body\n<H3C>\n";
        var batch = fixture.Backups.StartStartupBody(new[] { fixture.Profile }, actor, P2B5SiteScopeDecision.Global);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ConfigBackupBatch current;
        do
        {
            await Task.Delay(20, timeout.Token);
            current = fixture.Backups.GetBatch(batch.Id, actor, P2B5SiteScopeDecision.Global)!;
        } while (current.State is "RUNNING");
        Assert(current is { State: "SUCCEEDED", Succeeded: 1 } &&
            fixture.Factory.Commands.SequenceEqual(new[] { H3cCommandPolicy.DisplayStartup, "more flash:/startup.cfg" }),
            "startup body collection must read the pointer first and then execute the exact validated file command");
        var snapshot = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global, ConfigBackupKinds.StartupBody).Single();
        Assert(snapshot.Command == "more flash:/startup.cfg" && snapshot.ConfigurationKind == ConfigBackupKinds.StartupBody,
            "startup body snapshot must retain the validated command and independent category");
        var download = fixture.Backups.OpenDownload(snapshot.Id, P2B5SiteScopeDecision.Global)!.Value;
        using var reader = new StreamReader(download.Stream);
        Assert(reader.ReadToEnd().Contains("sysname startup-body", StringComparison.Ordinal),
            "startup body snapshot must persist the file contents through the normal download boundary");
    }

    private static async Task VerifyPrivateKeyRedaction(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var labels = new[] { "PRIVATE KEY", "RSA PRIVATE KEY", "EC PRIVATE KEY", "OPENSSH PRIVATE KEY", "ENCRYPTED PRIVATE KEY" };
        var keywords = new[] { "password", "passwd", "passphrase", "pass-phrase", "pre-shared-key", "community", "private-key", "secret", "credential", "authorization", "token", "psk" };
        var lineSecrets = keywords.Select((keyword, index) => $"synthetic-line-value-{index}").ToArray();
        var blockSecrets = labels.Select((label, index) => $"SYNTHETIC_PEM_BODY_{index}").ToArray();
        var output = "#\r\nsysname redaction-fixture\r\n" +
            string.Join("\r\n", keywords.Select((keyword, index) => $" {keyword.ToUpperInvariant()} {lineSecrets[index]}")) + "\r\n" +
            string.Join("\r\n", labels.Select((label, index) =>
                $"-----BEGIN {label}-----\r\n{blockSecrets[index]}\r\nSECOND_SYNTHETIC_PEM_LINE\r\n-----END {label}-----")) +
            "\r\ninterface GigabitEthernet1/0/1\r\n description visible\r\nreturn\r\n";
        var allSecrets = lineSecrets.Concat(blockSecrets).Append("SECOND_SYNTHETIC_PEM_LINE").ToArray();
        foreach (var kind in new[] { ConfigBackupKinds.Running, ConfigBackupKinds.Saved, ConfigBackupKinds.StartupInfo })
        {
            fixture.Factory.Output = output;
            var first = await fixture.Backup(actor, kind);
            Assert(first is { State: "SUCCEEDED", Succeeded: 1 }, "redaction fixtures must complete for every configuration category");
            var snapshot = fixture.Backups.Get(first.Items.Single().SnapshotId!, P2B5SiteScopeDecision.Global)!;
            var raw = fixture.Catalog.ReadCompleteH3cTaskRaw(snapshot.TaskRunId!)!;
            var opened = fixture.Backups.OpenDownload(snapshot.Id, P2B5SiteScopeDecision.Global)!.Value;
            string downloaded;
            using (var reader = new StreamReader(opened.Stream)) downloaded = reader.ReadToEnd();
            foreach (var sanitized in new[] { raw.Output, downloaded })
                Assert(allSecrets.All(secret => !sanitized.Contains(secret, StringComparison.Ordinal)) &&
                    !sanitized.Contains("-----BEGIN", StringComparison.Ordinal) &&
                    !sanitized.Contains("-----END", StringComparison.Ordinal) &&
                    sanitized.Contains("<REDACTED>", StringComparison.Ordinal) &&
                    sanitized.Contains("description visible", StringComparison.Ordinal),
                    $"{kind} Raw and downloads must redact entire private key blocks and diagnostic secret keywords while retaining ordinary configuration");

            fixture.Factory.Output = output.Replace("SYNTHETIC_PEM_BODY_", "CHANGED_PEM_BODY_", StringComparison.Ordinal)
                .Replace("synthetic-line-value-", "changed-line-value-", StringComparison.Ordinal);
            var second = await fixture.Backup(actor, kind);
            var difference = fixture.Backups.Diff(snapshot.Id, second.Items.Single().SnapshotId!, P2B5SiteScopeDecision.Global);
            Assert(difference is { Identical: true, AddedLines: 0, RemovedLines: 0 },
                $"{kind} differences must never reveal changes within secret lines or private key bodies");

            foreach (var label in labels)
            {
                var truncated = $"#\nsysname visible\n-----BEGIN {label}-----\nSYNTHETIC_TRUNCATED_BODY\nANOTHER_PRIVATE_BODY_LINE";
                var sanitized = ConfigBackupSanitizer.Sanitize(ConfigBackupKinds.CommandFor(kind), truncated);
                Assert(sanitized == "#\nsysname visible\n<REDACTED>",
                    $"{kind} must redact a truncated private key block through the end of partial output");
            }
        }
        Assert(ConfigBackupSanitizer.Sanitize(H3cCommandPolicy.DisplayVersion, output) == output,
            "configuration sanitization must remain limited to the configuration command boundary");
    }

    private static async Task VerifyConfigurationKinds(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        foreach (var kind in new[] { "SAVE", "saved", "", "RUNNING;save force" })
        {
            try
            {
                fixture.Backups.Start(new[] { fixture.Profile }, actor, P2B5SiteScopeDecision.Global, kind);
                throw new InvalidOperationException("unsupported backup kinds must fail before scheduling");
            }
            catch (ConfigBackupException ex)
            {
                Assert(ex.Code == "CONFIG_BACKUP_KIND_UNSUPPORTED" && ex.StatusCode == 400,
                    "unknown backup kinds must return a bounded validation error");
            }
        }
        Assert(fixture.Factory.Commands.Count == 0, "invalid kinds must not execute a device command");
        var running = await fixture.Backup(actor);
        fixture.Factory.Output = "#\nsysname saved-switch\nlocal-user reader password hash saved-only-secret\nreturn\n";
        var saved = await fixture.Backup(actor, ConfigBackupKinds.Saved);
        var savedSnapshot = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global, ConfigBackupKinds.Saved).Single();
        Assert(saved is { State: "SUCCEEDED", ConfigurationKind: ConfigBackupKinds.Saved } &&
            savedSnapshot is { Version: 1, ConfigurationKind: ConfigBackupKinds.Saved,
                Command: H3cCommandPolicy.DisplaySavedConfiguration },
            "saved configuration must retain its command and start its own version sequence");
        Assert(savedSnapshot.DownloadName!.EndsWith(".saved.cfg", StringComparison.Ordinal),
            "saved download names must identify their category");
        var savedRaw = fixture.Catalog.ReadCompleteH3cTaskRaw(savedSnapshot.TaskRunId!)!;
        Assert(!savedRaw.Output.Contains("saved-only-secret", StringComparison.Ordinal),
            "saved configuration secrets must be removed before Raw persistence");
        var download = fixture.Backups.OpenDownload(savedSnapshot.Id, P2B5SiteScopeDecision.Global)!.Value;
        using (var reader = new StreamReader(download.Stream))
            Assert(!reader.ReadToEnd().Contains("saved-only-secret", StringComparison.Ordinal),
                "saved configuration downloads must not expose secrets");

        fixture.Factory.Output = "Main startup saved-configuration file: flash:/startup.cfg\nNext main startup saved-configuration file: NULL\n";
        var startup = await fixture.Backup(actor, ConfigBackupKinds.StartupInfo);
        var startupSnapshot = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global, ConfigBackupKinds.StartupInfo).Single();
        Assert(startup is { State: "SUCCEEDED", ConfigurationKind: ConfigBackupKinds.StartupInfo } &&
            startupSnapshot is { Version: 1, ConfigurationKind: ConfigBackupKinds.StartupInfo, Command: H3cCommandPolicy.DisplayStartup },
            "startup file pointers, including NULL, must be separate metadata with their own versions");
        Assert(fixture.Backups.Diff(savedSnapshot.Id, startupSnapshot.Id, P2B5SiteScopeDecision.Global) is null &&
            fixture.Backups.Diff(running.Items.Single().SnapshotId!, savedSnapshot.Id, P2B5SiteScopeDecision.Global) is null,
            "different configuration kinds must never be compared as version history");

        fixture.Factory.Output = "#\nsysname running-new\nreturn\n";
        var runningSecond = await fixture.Backup(actor);
        var runningHistory = fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global, ConfigBackupKinds.Running);
        Assert(runningHistory.Select(item => item.Version).Order().SequenceEqual(new[] { 1, 2 }) &&
            fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global).Count == 4,
            "interleaved kinds must retain independent version sequences and an optional combined listing");
        var runningFirst = runningHistory.Single(item => item.Version == 1);
        var directory = Path.Combine(fixture.Root, "backup", "config", fixture.Profile);
        var manifestPath = Path.Combine(directory, runningFirst.Id + ".json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest.Remove("configurationKind");
        manifest.Remove("command");
        File.Move(Path.Combine(directory, runningFirst.DownloadName!), Path.Combine(directory, runningFirst.Id + ".cfg"));
        manifest["downloadName"] = runningFirst.Id + ".cfg";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var legacy = fixture.Backups.Get(runningFirst.Id, P2B5SiteScopeDecision.Global);
        Assert(legacy is { ConfigurationKind: ConfigBackupKinds.Running, Command: H3cCommandPolicy.DisplayCurrentConfiguration },
            "old manifests without kind or command must remain running configurations");
        var legacyDownload = fixture.Backups.OpenDownload(runningFirst.Id, P2B5SiteScopeDecision.Global);
        Assert(legacyDownload is not null, "legacy id.cfg downloads must remain compatible");
        legacyDownload!.Value.Stream.Dispose();
        Assert(fixture.Backups.Diff(runningFirst.Id, runningSecond.Items.Single().SnapshotId!,
                P2B5SiteScopeDecision.Global) is { ConfigurationKind: ConfigBackupKinds.Running } &&
            fixture.Backups.Diff(runningFirst.Id, savedSnapshot.Id, P2B5SiteScopeDecision.Global) is null,
            "legacy running records must compare only with running records");

        var savedManifestPath = Path.Combine(directory, savedSnapshot.Id + ".json");
        var savedManifest = JsonNode.Parse(File.ReadAllText(savedManifestPath))!.AsObject();
        savedManifest["command"] = H3cCommandPolicy.DisplayCurrentConfiguration;
        File.WriteAllText(savedManifestPath, savedManifest.ToJsonString());
        Assert(fixture.Backups.Get(savedSnapshot.Id, P2B5SiteScopeDecision.Global) is null &&
            fixture.Backups.OpenDownload(savedSnapshot.Id, P2B5SiteScopeDecision.Global) is null &&
            fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global, ConfigBackupKinds.Saved).Count == 0,
            "manifest command and kind mismatches must never permit list, diff or download");
        Assert(fixture.Factory.Commands.All(H3cCommandPolicy.IsConfigurationCommand) &&
            fixture.Factory.Commands.Contains(H3cCommandPolicy.DisplaySavedConfiguration) &&
            fixture.Factory.Commands.Contains(H3cCommandPolicy.DisplayStartup),
            "backup collection must use only the three read-only configuration commands");
        Assert(fixture.Catalog.QueryH3cFacts(fixture.Profile, history: true)
            .All(item => !item.Kind.StartsWith("command.display.saved-configuration", StringComparison.Ordinal) &&
                !item.Kind.StartsWith("command.display.startup", StringComparison.Ordinal)),
            "saved configuration and startup information must not enter general Facts");
    }

    private static async Task VerifyInvalidSavedOutputs(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        foreach (var output in new[]
        {
            "The saved configuration file does not exist.\n", "The configuration file doesn't exist.\n",
            "No saved configuration file.\n", "Error: Unrecognized command found at '^' position.\n",
            "Permission denied.\n", "#\nsysname incomplete\n---- More ----\n", "\n"
        })
        {
            fixture.Factory.Output = output;
            var batch = await fixture.Backup(actor, ConfigBackupKinds.Saved);
            Assert(batch is { State: "PARTIAL", Succeeded: 0, Failed: 1 } &&
                fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global, ConfigBackupKinds.Saved).Count == 0,
                "missing, unsupported, denied, paged or empty saved configurations must not create a successful snapshot");
        }
        fixture.Factory.Failure = new H3cPartialOutputException("#\nsysname interrupted\n", "H3C_DISCONNECTED_PARTIAL", false);
        var partial = await fixture.Backup(actor, ConfigBackupKinds.Saved);
        Assert(partial is { State: "PARTIAL", Succeeded: 0, Failed: 1 } &&
            fixture.Backups.List(fixture.Profile, P2B5SiteScopeDecision.Global).Count == 0,
            "a partial Raw capture must not become a complete saved configuration backup");
    }

    private static async Task VerifyLineDiffs(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var failures = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) failures.Add(message);
        }

        async Task<ConfigBackupDiff> Compare(string baseline, string target)
        {
            fixture.Factory.Output = baseline;
            var first = await fixture.Backup(actor);
            fixture.Factory.Output = target;
            var second = await fixture.Backup(actor);
            Assert(first.State == "SUCCEEDED" && second.State == "SUCCEEDED", "diff fixtures must back up successfully");
            return fixture.Backups.Diff(first.Items.Single().SnapshotId!, second.Items.Single().SnapshotId!,
                P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("diff fixture must be readable");
        }

        var reordered = await Compare("alpha\nbeta\ngamma\n", "gamma\nbeta\nalpha\n");
        Check(!reordered.Identical && reordered.Added.SequenceEqual(new[] { "beta", "alpha" }) &&
            reordered.Removed.SequenceEqual(new[] { "alpha", "beta" }),
            "reordered lines must produce differences in their original sequence");

        var removedDuplicate = await Compare("head\nrepeat\nrepeat\ntail\n", "head\nrepeat\ntail\n");
        Check(!removedDuplicate.Identical && removedDuplicate.AddedLines == 0 && removedDuplicate.RemovedLines == 1 &&
            removedDuplicate.Removed.SequenceEqual(new[] { "repeat" }), "removing one duplicate occurrence must be visible");
        var addedDuplicates = await Compare("head\ntail\n", "head\nrepeat\nrepeat\ntail\n");
        Check(!addedDuplicates.Identical && addedDuplicates.AddedLines == 2 && addedDuplicates.RemovedLines == 0 &&
            addedDuplicates.Added.SequenceEqual(new[] { "repeat", "repeat" }), "added duplicate occurrences must not collapse");
        var same = await Compare("head\nrepeat\nrepeat\ntail\n", "head\nrepeat\nrepeat\ntail\n");
        Check(same.Identical && same.AddedLines == 0 && same.RemovedLines == 0 && same.ChangedLines == 0,
            "identical sequences with duplicates must remain identical");
        var replaced = await Compare("head\nbefore\ntail\n", "head\nafter\ntail\n");
        Check(replaced is { Identical: false, AddedLines: 1, RemovedLines: 1, ChangedLines: 1 },
            "equal-length replacements must count changed lines");

        var prefix = string.Concat(Enumerable.Repeat("x\n", 17_000));
        var lateChange = await Compare(prefix + "before\n", prefix + "after\n");
        Check(!lateChange.Identical && lateChange.Added.SequenceEqual(new[] { "after" }) &&
            lateChange.Removed.SequenceEqual(new[] { "before" }), "changes after line 16,384 must not be hidden");

        const int lineCount = 12_000;
        var baseline = string.Join('\n', Enumerable.Range(0, lineCount).Select(index => $"old-{index:D5}")) + "\n";
        var target = string.Join('\n', Enumerable.Range(0, lineCount).Select(index => $"new-{index:D5}")) + "\n";
        fixture.Factory.Output = baseline;
        var largeFirst = await fixture.Backup(actor);
        fixture.Factory.Output = target;
        var largeSecond = await fixture.Backup(actor);
        Assert(largeFirst.State == "SUCCEEDED" && largeSecond.State == "SUCCEEDED", "large diff fixtures must back up successfully");
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var large = fixture.Backups.Diff(largeFirst.Items.Single().SnapshotId!, largeSecond.Items.Single().SnapshotId!,
            P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("large diff fixture must be readable");
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(large is { Identical: false, AddedLines: lineCount, RemovedLines: lineCount, ChangedLines: lineCount } &&
            large.Added.Count == 256 && large.Removed.Count == 256 &&
            large.Added.SequenceEqual(Enumerable.Range(0, 256).Select(index => $"new-{index:D5}")) &&
            large.Removed.SequenceEqual(Enumerable.Range(0, 256).Select(index => $"old-{index:D5}")),
            "large differences must retain total counts and cap each ordered preview at 256 lines");
        // A full 12,000 x 12,000 integer LCS table would allocate over 500 MiB.
        Check(allocatedBytes < 32 * 1024 * 1024, "large comparisons must use bounded memory instead of a full quadratic table");
        var corrupt = fixture.Backups.Get(largeSecond.Items.Single().SnapshotId!, P2B5SiteScopeDecision.Global)!;
        var corruptPath = Path.Combine(fixture.Root, "backup", "config", fixture.Profile, corrupt.DownloadName!);
        var original = File.ReadAllBytes(corruptPath);
        original[0] ^= 1;
        File.WriteAllBytes(corruptPath, original);
        Check(fixture.Backups.OpenDownload(corrupt.Id, P2B5SiteScopeDecision.Global) is null &&
            fixture.Backups.Diff(largeFirst.Items.Single().SnapshotId!, corrupt.Id, P2B5SiteScopeDecision.Global) is null &&
            fixture.Backups.Diff(corrupt.Id, largeFirst.Items.Single().SnapshotId!, P2B5SiteScopeDecision.Global) is null,
            "same-length corruption must reject downloads and comparisons in either direction");
        Assert(failures.Count == 0, string.Join("; ", failures));
    }

    private static void RejectUnsupportedTargets(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var unsupported = Guid.NewGuid().ToString("N");
        fixture.Catalog.CreateH3cDeviceProfile(new(unsupported, "192.0.2.62", 22, "fixture", "FIXTURE_REFERENCE",
            string.Empty, true, 0, 0));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "catalog.db"), Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE device_inventory SET vendor=$vendor WHERE terminal_profile_id=$profile";
        command.Parameters.AddWithValue("$profile", unsupported);
        var vendor = command.Parameters.Add("$vendor", SqliteType.Text);
        foreach (var name in new[] { "Cisco", "Huawei", "ZTE", "unknown-vendor", "" })
        {
            vendor.Value = name;
            command.ExecuteNonQuery();
            try
            {
                fixture.Backups.Start(new[] { fixture.Profile, unsupported }, actor, P2B5SiteScopeDecision.Global);
                throw new InvalidOperationException("mixed-vendor backup must be rejected before scheduling");
            }
            catch (ConfigBackupException ex)
            {
                Assert(ex.Code == "CONFIG_BACKUP_TARGET_UNSUPPORTED" && ex.StatusCode == 409,
                    "unsupported vendors must return a specific compatibility error");
            }
        }
        var hiddenScope = new P2B5SiteScopeDecision("AUTHORIZED", false, new HashSet<string> { "another-site" }, "fixture");
        try
        {
            fixture.Backups.Start(new[] { unsupported }, actor, hiddenScope);
            throw new InvalidOperationException("hidden target must be rejected");
        }
        catch (ConfigBackupException ex)
        {
            Assert(ex.Code == "CONFIG_BACKUP_TARGET_NOT_VISIBLE" && ex.StatusCode == 404,
                "target visibility must be checked before vendor compatibility");
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM task_definitions";
        Assert(Convert.ToInt64(command.ExecuteScalar()) == 0,
            "a rejected mixed batch must not create definitions or connect even its first H3C target");
    }

    private static void RejectManagedFitApTargets(WebUserIdentity actor)
    {
        using var fixture = new Fixture();
        var fitAp = Guid.NewGuid().ToString("N");
        fixture.Catalog.CreateH3cDeviceProfile(new(fitAp, "192.0.2.63", 22, "fixture", "FIXTURE_REFERENCE",
            string.Empty, true, 0, 0));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "catalog.db"), Pooling = false
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE device_inventory SET vendor='H3C',device_type=$type WHERE terminal_profile_id=$profile";
        command.Parameters.AddWithValue("$profile", fitAp);
        var type = command.Parameters.Add("$type", SqliteType.Text);
        foreach (var deviceType in new[] { "FIT_AP", "fit_ap", "FiT_aP" })
        {
            type.Value = deviceType;
            command.ExecuteNonQuery();
            foreach (var profiles in new[] { new[] { fitAp }, new[] { fixture.Profile, fitAp } })
            {
                try
                {
                    fixture.Backups.Start(profiles, actor, P2B5SiteScopeDecision.Global);
                    throw new InvalidOperationException("direct service calls must not back up a FIT-AP as a standalone H3C target");
                }
                catch (ConfigBackupException ex)
                {
                    Assert(ex.Code == "CONFIG_BACKUP_TARGET_UNSUPPORTED" && ex.StatusCode == 409,
                        "FIT-AP targets must return the unsupported-target compatibility error");
                }
            }
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM task_definitions";
        Assert(Convert.ToInt64(command.ExecuteScalar()) == 0,
            "single and mixed FIT-AP requests must fail before creating any task definitions");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public RuntimeSettings Settings { get; }
        public Catalog Catalog { get; }
        public Factory Factory { get; } = new();
        public ConfigBackupService Backups { get; }
        private readonly BoundedCollectionScheduler _scheduler;

        public Fixture()
        {
            var repository = new DirectoryInfo(File.Exists(Path.Combine(Environment.CurrentDirectory, "tools", "workspace_paths.py"))
                ? Environment.CurrentDirectory : AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "tools", "workspace_paths.py")))
                repository = repository.Parent ?? throw new InvalidOperationException("Repository root unavailable.");
            var workspace = repository.Parent!.Name.Equals("worktrees", StringComparison.OrdinalIgnoreCase)
                ? repository.Parent.Parent! : repository.Parent;
            Root = Path.Combine(workspace.FullName, "test-data", "next-config-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new() { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            Catalog = new(Settings);
            Catalog.Initialize();
            Catalog.CreateH3cDeviceProfile(new(Profile, "192.0.2.61", 22, "fixture", "FIXTURE_REFERENCE",
                string.Empty, true, 0, 0));
            var scopes = new P2B5SiteScopeStore(Settings);
            var authorizer = new TaskSiteScopeAuthorizer(Catalog, scopes);
            _scheduler = new BoundedCollectionScheduler(Factory, 2, 8, Catalog,
                resultSink: new H3cTaskRawResultSink(Catalog), scopeAuthorizer: authorizer);
            Backups = new ConfigBackupService(Catalog, _scheduler, authorizer, Settings);
        }

        public async Task<ConfigBackupBatch> Backup(WebUserIdentity actor, string? configurationKind = null)
        {
            var batch = Backups.Start(new[] { Profile }, actor, P2B5SiteScopeDecision.Global, configurationKind);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var current = Backups.GetBatch(batch.Id, actor, P2B5SiteScopeDecision.Global) ?? throw new InvalidOperationException("backup batch disappeared");
                if (current.State is "SUCCEEDED" or "PARTIAL") return current;
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose()
        {
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class Factory : IDeviceSessionFactory
    {
        public string Output { get; set; } = "<H3C>sysname initial\nlocal-user admin password hash super-secret\ninterface GigabitEthernet1/0/1\n description initial\n</H3C>\n";
        public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);
        public Exception? Failure { get; set; }
        public List<string> Commands { get; } = new();
        public IDeviceSession Create(string target) => new Session(this);
        private sealed class Session(Factory owner) : IDeviceSession
        {
            public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken)
            {
                owner.Commands.Add(command);
                return owner.Failure is not null ? Task.FromException<string>(owner.Failure) :
                    Task.FromResult(owner.Outputs.TryGetValue(command, out var output) ? output : owner.Output);
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
