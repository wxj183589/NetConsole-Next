using NetConsole.Next.Service;
using System.Net;

internal static class FitApRemoteConsoleTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "next-fit-ap-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RuntimeSettings
            {
                DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024,
                FitApDefaultPassword = "fixture-default-marker"
            };
            var catalog = new Catalog(settings);
            catalog.Initialize();
            var authority = new FakeAuthority();
            var store = new FitApConnectionSettingsStore(settings, authority);

            var defaultSettings = store.Effective("ac-1", "ap-1");
            Assert(defaultSettings.Source == "DEFAULT" && defaultSettings.Protocol == "TELNET" &&
                defaultSettings.Port == 23 && defaultSettings.AuthMode == "PASSWORD_ONLY" &&
                defaultSettings.Username == string.Empty && defaultSettings.PasswordConfigured,
                "FIT-AP defaults must be password-only Telnet without a username");

            store.Save(new("GLOBAL", null, Password: "global-marker"), "ac-1", "ap-1");
            store.Save(new("AC", "ac-1", AuthMode: "USERNAME_PASSWORD", Username: "ac-user", Password: "ac-marker"), "ac-1", "ap-1");
            Assert(store.Effective("ac-1", "ap-1") is { Source: "AC", AuthMode: "USERNAME_PASSWORD", Username: "ac-user", PasswordConfigured: true },
                "AC settings must override global settings");

            store.Save(new("AP", "ap-1", Password: "ap-marker"), "ac-1", "ap-1");
            Assert(store.Effective("ac-1", "ap-1") is { Source: "AP", Protocol: "TELNET", Port: 23, PasswordConfigured: true },
                "AP settings must override AC settings");
            var description = store.Describe("ac-1", "ap-1");
            Assert(description.Effective.PasswordConfigured && description.Overrides.Count == 3 &&
                description.Overrides.All(item => item.PasswordConfigured),
                "settings API view must expose only configured state, not credentials");
            var json = File.ReadAllText(Path.Combine(root, "wireless", "fit-ap-connection-settings.json"));
            Assert(!json.Contains("marker", StringComparison.OrdinalIgnoreCase),
                "FIT-AP settings metadata must not contain plaintext passwords");

            store.Save(new("AP", "ap-1", ClearPassword: true), "ac-1", "ap-1");
            Assert(store.Effective("ac-1", "ap-1").Source == "AC",
                "clearing an AP password must fall back to the AC override");

            var resourceId = "fit-ap-resource-1";
            var capturedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var writer = new RawSegmentWriterFactory().CreateFitAp(catalog, resourceId);
            writer.AppendFitApCommand(resourceId, Guid.NewGuid().ToString("N"), "ac-1", "local",
                "AP-1", "192.0.2.4", H3cCommandPolicy.DisplayVersion,
                "H3C Comware Software, Version 7.1.064 (mock)", capturedMs, "COMPLETE");
            writer.SealAsync().GetAwaiter().GetResult();
            Assert(writer.Sha256 is not null && File.Exists(Path.Combine(root, writer.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                "FIT-AP Raw result must seal under its isolated source folder");
            var fact = new FitApFactSnapshot("fact-1", resourceId, "ac-1", "local", "AP-1",
                "device.identity", "PARSED", capturedMs, writer.RelativePath, writer.Sha256!, "{\"version\":\"7.1.064\"}");
            catalog.RecordFitApFact(fact);
            Assert(catalog.QueryFitApFacts(resourceId, P2B5SiteScopeDecision.Global).Single().RawRelativePath == writer.RelativePath,
                "FIT-AP fact query must retain the sealed Raw reference");
            Console.WriteLine("FIT_AP_REMOTE_CONSOLE_SETTINGS_TESTS=PASS");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private sealed class FakeAuthority : IH3cCredentialAuthority
    {
        private readonly Dictionary<string, string> _passwords = new(StringComparer.Ordinal);
        public string? GetPassword(H3cDeviceProfileSnapshot profile) => GetPassword(profile.Id);
        public string? GetPassword(string profileId) => _passwords.TryGetValue(profileId, out var value) ? value : null;
        public bool HasPassword(H3cDeviceProfileSnapshot profile) => HasPassword(profile.Id);
        public bool HasPassword(string profileId) => _passwords.ContainsKey(profileId);
        public void SetPassword(H3cDeviceProfileSnapshot profile, string password) => SetPassword(profile.Id, password);
        public void SetPassword(string profileId, string password) => _passwords[profileId] = password;
        public bool RevokePassword(H3cDeviceProfileSnapshot profile) => RevokePassword(profile.Id);
        public bool RevokePassword(string profileId) => _passwords.Remove(profileId);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
