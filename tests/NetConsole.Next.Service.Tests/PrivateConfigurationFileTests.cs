using NetConsole.Next.Service;

internal static class PrivateConfigurationFileTests
{
    public static void Run()
    {
        var root = Path.Combine(@"D:\study\NetConsole-Next-Workspace\test-data", "r69-private-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "backup", "config-complete", "profile");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "backup.cfg"), "SYNTHETIC_PRIVATE_CONFIGURATION");
            File.WriteAllText(Path.Combine(root, "backup", "public.txt"), "public");
            var settings = new RuntimeSettings { DataRoot = root, WebPort = 18771, SyslogPort = 0,
                SyslogBind = System.Net.IPAddress.Loopback, SyslogReceiveBufferBytes = 4 * 1024 * 1024 };
            var files = new FileManagerService(settings);
            var list = files.List("backup", "", null, null, null, 1, 100);
            if (list.Items.Count != 1 || list.Items[0].Name != "public.txt")
                throw new InvalidOperationException("Private originals must not appear in the public backup listing.");
            foreach (var path in new[] { "config-complete", "config-complete/profile", "config-complete/profile/backup.cfg", "CONFIG-COMPLETE/profile/backup.cfg" })
            {
                try { files.Resolve("backup", path, false); throw new InvalidOperationException("Private configuration path was exposed."); }
                catch (UnauthorizedAccessException) { }
            }
            try { files.OpenDownload("backup", "config-complete/profile/backup.cfg"); throw new InvalidOperationException("Private original download was exposed."); }
            catch (UnauthorizedAccessException) { }
            var expanded = files.Expand(new("backup", [""]), new(settings), new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);
            if (expanded.Count != 1 || expanded[0].File.RelativePath != "public.txt")
                throw new InvalidOperationException("Recursive archives must exclude all private configuration files.");
            Console.WriteLine("PRIVATE_CONFIGURATION_FILES_TESTS=PASS");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
