using System.Text.Json;

namespace NetConsole.Next.Service;

// Test-only synchronization points. They are inert unless both Development and
// the explicit crash-harness switch are enabled; production startup/API paths
// never wait on these files.
internal static class DeviceBatchCrashTestHooks
{
    private const string EnabledVariable = "NETCONSOLE_NEXT_BATCH_CRASH_TEST";
    private const string DirectoryVariable = "NETCONSOLE_NEXT_BATCH_CRASH_HOOK_DIR";
    private const string StageVariable = "NETCONSOLE_NEXT_BATCH_CRASH_TEST_STAGE";

    public static void WaitFor(string stage)
    {
        if (!IsEnabled() || !string.Equals(Environment.GetEnvironmentVariable(StageVariable), stage, StringComparison.Ordinal))
            return;

        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var readyPath = Path.Combine(directory, stage + ".ready.json");
        var releasePath = Path.Combine(directory, stage + ".release");
        File.WriteAllText(readyPath, JsonSerializer.Serialize(new
        {
            stage,
            pid = Environment.ProcessId,
            atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (!File.Exists(releasePath))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Crash test synchronization point '{stage}' was not released.");
            Thread.Sleep(25);
        }
    }

    private static bool IsEnabled() =>
        Environment.GetEnvironmentVariable(EnabledVariable) == "1" &&
        (string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase));
}
