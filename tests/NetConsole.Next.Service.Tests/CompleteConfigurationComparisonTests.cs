using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;
using System.Net;
using System.Text.Json;

internal static class CompleteConfigurationComparisonTests
{
    private static readonly WebUserIdentity Actor = new("local-api-token", "local-api-token", "SUPERADMIN", "test");
    private const string Original = "#\n version 7.1.070, Release 3507P26\n#\n sysname fixture\n#\ninterface GigabitEthernet1/0/1\n description original\n ip address 10.0.0.1 255.255.255.0\n#\nlocal-user admin class manage\n password hash SYNTHETIC_SECRET\n#\nreturn\n";

    public static async Task RunAsync()
    {
        ContextAndCategories();
        SecretOnlyChange();
        PrivateKeyChange();
        OrderedDuplicateCommands();
        BoundedComparison();
        NoiseAndStanzaOrder();
        await HistoricalComparison();
        await TamperRejected();
        await ReadOnlyPreview();
        await CompatibilityPreview();
        Console.WriteLine("COMPLETE_CONFIGURATION_COMPARISON_TESTS=10/10 PASS");
    }

    private static void ContextAndCategories()
    {
        var next = Original.Replace("description original", "description changed")
            .Replace("#\nreturn", "#\nvlan 100\n name office\n#\nwlan service-template test\n ssid fixture\n#\nwlan ap-group test\n ap test\n#\nacl advanced 3000\n rule 0 permit ip\n#\nip route-static 0.0.0.0 0 10.0.0.2\n#\nreturn");
        var differences = CompleteConfigurationDiffer.Compare(Original, next);
        foreach (var category in new[] { "INTERFACE", "VLAN", "WIRELESS", "AP_GROUP", "MANAGEMENT_ADDRESS", "ACL", "ROUTING" })
            Assert(differences.Any(item => item.Categories.Contains(category)), "missing configuration category " + category);
        var port = differences.Single(item => item.Context == "interface GigabitEthernet1/0/1");
        Assert(port.AddedLines == 1 && port.RemovedLines == 1 && port.Lines.Any(item => item.Kind == "CONTEXT" && item.Text.Contains("interface")),
            "interface change must retain its stanza and neighboring context");
    }

    private static void SecretOnlyChange()
    {
        var next = Original.Replace("SYNTHETIC_SECRET", "SECOND_SECRET");
        var difference = CompleteConfigurationDiffer.Compare(Original, next).Single();
        Assert(difference.SensitiveValuesChanged && difference.AddedLines == 0 && difference.RemovedLines == 0,
            "a hidden credential-only change must remain visible as metadata");
        var report = JsonSerializer.Serialize(difference);
        Assert(!report.Contains("SYNTHETIC_SECRET") && !report.Contains("SECOND_SECRET"), "comparison must never disclose secret values");
    }

    private static void PrivateKeyChange()
    {
        var before = Original.Replace("#\nreturn", "#\npublic-key local\n-----BEGIN RSA PRIVATE KEY-----\n#\nPRIVATE_CONTENT_A\n-----END RSA PRIVATE KEY-----\n#\nreturn");
        var after = before.Replace("PRIVATE_CONTENT_A", "PRIVATE_CONTENT_B").Replace("description original", "description updated");
        var differences = CompleteConfigurationDiffer.Compare(before, after);
        Assert(CompleteConfigurationDiffer.SecretsChanged(before, after) && differences.Any(item => item.SensitiveValuesChanged), "multiline private key changes must be detected");
        var report = JsonSerializer.Serialize(differences);
        Assert(!report.Contains("PRIVATE_CONTENT") && !report.Contains("SYNTHETIC_SECRET"), "multiline private key fragments must be redacted before diffing");
    }

    private static void OrderedDuplicateCommands()
    {
        var before = Original.Replace(" description original", " description original\n packet-filter 3000 inbound\n packet-filter 3000 inbound\n packet-filter 3001 outbound");
        var after = before.Replace(" packet-filter 3000 inbound\n packet-filter 3000 inbound", " packet-filter 3000 inbound");
        var difference = CompleteConfigurationDiffer.Compare(before, after).Single();
        Assert(difference.RemovedLines == 1 && difference.AddedLines == 0, "duplicate command removal must not disappear through set comparison");
    }

    private static void BoundedComparison()
    {
        var before = Original.Replace(" description original", string.Join('\n', Enumerable.Range(0, 2200).Select(index => " description old-" + index)));
        var after = before.Replace("old-", "new-");
        var difference = CompleteConfigurationDiffer.Compare(before, after).Single();
        Assert(difference.Truncated && difference.AddedLines == 2200 && difference.RemovedLines == 2200 && difference.Lines.Count <= 512,
            "large differences must preserve full conservative counts while bounding memory and returned previews");
    }

    private static void NoiseAndStanzaOrder()
    {
        Assert(CompleteConfigurationDiffer.Compare(Original, Original.Replace("\n", "\r\n").Replace(" original", " original  ")).Count == 0,
            "line endings and trailing spaces must not generate false changes");
        var before = Original.Replace("#\nreturn", "#\nvlan 10\n#\nvlan 20\n#\nreturn");
        var after = before.Replace("vlan 10\n#\nvlan 20", "vlan 20\n#\nvlan 10");
        Assert(CompleteConfigurationDiffer.Compare(before, after).Any(item => item.Context == "配置段顺序"), "stanza order changes must be reported");
    }

    private static async Task HistoricalComparison()
    {
        using var fixture = new Fixture();
        var before = await fixture.Capture();
        fixture.Factory.Text = Original.Replace("description original", "description changed").Replace("SYNTHETIC_SECRET", "SECOND_SECRET");
        var after = await fixture.Capture();
        var result = fixture.Service.Compare(before.Id, after.Id, Actor, P2B5SiteScopeDecision.Global);
        Assert(result.OriginalConfigurationChanged && result.SensitiveValuesChanged && result.AddedLines == 1 && result.RemovedLines == 1,
            "verified originals must drive both ordinary and hidden differences");
        Assert(fixture.Factory.Opens == 2 && fixture.Factory.Writes == 0, "historical comparison must not connect or write");
        Assert(!JsonSerializer.Serialize(result).Contains("SECOND_SECRET"), "public report must contain only sanitized text");
        var identical = fixture.Service.Compare(after.Id, after.Id, Actor, P2B5SiteScopeDecision.Global);
        Assert(!identical.OriginalConfigurationChanged && identical.Contexts.Count == 0, "same backup comparison must report unchanged");
    }

    private static async Task TamperRejected()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        File.WriteAllText(fixture.Raw(backup), Original.Replace("original", "tampered"));
        await Reject(() => fixture.Service.PreviewRestoreAsync(backup.Id, Actor, P2B5SiteScopeDecision.Global, default), "CONFIG_COMPLETE_SOURCE_UNAVAILABLE");
        await Reject(() => Task.FromResult(fixture.Service.Compare(backup.Id, backup.Id, Actor, P2B5SiteScopeDecision.Global)), "CONFIG_COMPLETE_SOURCE_UNAVAILABLE");
        Assert(fixture.Factory.Opens == 1 && fixture.Factory.Writes == 0, "tampered source must fail before any live connection");
    }

    private static async Task ReadOnlyPreview()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        fixture.Factory.Text = Original.Replace("description original", "description live");
        var result = await fixture.Service.PreviewRestoreAsync(backup.Id, Actor, P2B5SiteScopeDecision.Global, default);
        Assert(result.Compatible && !result.WriteAllowed && result.Blockers.Contains("FULL_DEVICE_RESTORE_NOT_IMPLEMENTED"),
            "live preview must not grant unsupported whole-device restore permission");
        Assert(result.Comparison.Before.Id == result.CurrentBackupId && result.Comparison.After.Id == backup.Id && result.Comparison.OriginalConfigurationChanged,
            "restoration preview must compare live current to the selected historical source");
        Assert(fixture.Factory.Writes == 0 && fixture.Service.List(fixture.Profile, Actor, P2B5SiteScopeDecision.Global).Count == 2,
            "preview must preserve a verified current snapshot without issuing a write");
    }

    private static async Task CompatibilityPreview()
    {
        using var fixture = new Fixture();
        var backup = await fixture.Capture();
        fixture.Factory.Version = "7.1.070, Release 9999";
        var result = await fixture.Service.PreviewRestoreAsync(backup.Id, Actor, P2B5SiteScopeDecision.Global, default);
        Assert(!result.Compatible && !result.WriteAllowed && result.Blockers.Contains("MODEL_OR_SOFTWARE_VERSION_MISMATCH") && fixture.Factory.Writes == 0,
            "software version mismatch must remain an explicit restore blocker");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task<T>> action, string code)
    {
        try { await action(); }
        catch (ConfigRestoreException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("NETCONSOLE_NEXT_WORKSPACE_ROOT") ?? @"D:\study\NetConsole-Next-Workspace",
            "test-data", "r70-config-compare-" + Guid.NewGuid().ToString("N"));
        public string Profile { get; } = Guid.NewGuid().ToString("N");
        public Factory Factory { get; } = new();
        public SimulatorConfigurationService Service { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var settings = new RuntimeSettings { DataRoot = Root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var catalog = new Catalog(settings); catalog.Initialize();
            catalog.CreateH3cDeviceProfile(new(Profile, "192.168.56.3", 22, "fixture", "FIXTURE_REFERENCE", "", true, 0, 0, TerminalProtocol: "SSH"));
            Service = new(catalog, settings, Factory);
        }
        public Task<CompleteConfigurationBackup> Capture() => Service.CaptureAsync(Profile, Actor, P2B5SiteScopeDecision.Global, default);
        public string Raw(CompleteConfigurationBackup backup) => Path.Combine(Root, "backup", "config-complete", Profile, backup.Id + ".cfg");
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class Factory : ISimulatorConfigurationSessionFactory
    {
        public string Text = Original;
        public string Version = "7.1.070, Release 3507P26";
        public int Opens;
        public int Writes;
        public Task<ISimulatorConfigurationSession> OpenAsync(H3cDeviceProfileSnapshot profile, CancellationToken token)
        { Opens++; return Task.FromResult<ISimulatorConfigurationSession>(new Session(this)); }
        private sealed class Session(Factory owner) : ISimulatorConfigurationSession
        {
            public Task<string> ReadAsync(string command, CancellationToken token) => Task.FromResult(command == H3cCommandPolicy.DisplayVersion
                ? $"H3C Comware Software, Version {owner.Version}\nH3C WA6638 uptime is 1 day\n" : owner.Text);
            public Task SetDescriptionAsync(string name, string? description, CancellationToken token)
            { owner.Writes++; throw new InvalidOperationException("Read-only workflow must not write"); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
