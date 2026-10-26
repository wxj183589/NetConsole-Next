using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NetConsole.Next.Service;

namespace NetConsole.Next.Service.Tests;

internal static class P2B2MigrationPathSecurityTests
{
    public static void Run()
    {
        VerifiesTemporaryPathIsAllowed();
        VerifiesPathPrefixEscapeIsRejected();
        VerifiesDotDotEscapeIsRejected();
        VerifiesConnectionPathMismatchIsRejected();
        VerifiesProductionPathIsRejectedWithoutOpeningProduction();
        VerifiesMissingExplicitFlagIsRejected();
        VerifiesMissingIsolatedContextIsRejected();
        var symlinkCreated = VerifiesSymlinkEscapeIsRejected();
        var junctionCreated = VerifiesJunctionEscapeIsRejected();

        Console.WriteLine($"MIGRATION_REPARSE_POINT_REJECTED={(symlinkCreated || junctionCreated ? "PASS" : "SKIPPED_NO_LINK_PERMISSION")}");
        Console.WriteLine("PASS: P2B-2 migration path boundary, connection identity and reparse-point security");
    }

    private static void VerifiesTemporaryPathIsAllowed()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-allowed-");
        try
        {
            var databasePath = Path.Combine(root, "catalog.db");
            using var connection = Open(databasePath);
            P2B2MigrationContext.ForIsolatedTest(databasePath).Validate(connection);
            Console.WriteLine("MIGRATION_TEMP_PATH_ALLOWED=PASS");
        }
        finally { DeleteDirectory(root); }
    }

    private static void VerifiesPathPrefixEscapeIsRejected()
    {
        var tempRoot = TrimEndingSeparators(Path.GetFullPath(Path.GetTempPath()));
        var parent = Directory.GetParent(tempRoot)?.FullName ?? throw new InvalidOperationException("Temp parent is unavailable.");
        var outsideRoot = Path.Combine(parent, Path.GetFileName(tempRoot) + "-p2b2-prefix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var databasePath = Path.Combine(outsideRoot, "catalog.db");
            using var connection = Open(databasePath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(databasePath).Validate(connection));
            Console.WriteLine("MIGRATION_PATH_PREFIX_ESCAPE_REJECTED=PASS");
        }
        finally { DeleteDirectory(outsideRoot); }
    }

    private static void VerifiesDotDotEscapeIsRejected()
    {
        var tempRoot = TrimEndingSeparators(Path.GetFullPath(Path.GetTempPath()));
        var parent = Directory.GetParent(tempRoot)?.FullName ?? throw new InvalidOperationException("Temp parent is unavailable.");
        var outsideRootName = Path.GetFileName(tempRoot) + "-p2b2-dotdot-" + Guid.NewGuid().ToString("N");
        var outsideRoot = Path.Combine(parent, outsideRootName);
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var databasePath = Path.Combine(tempRoot, "..", outsideRootName, "catalog.db");
            using var connection = Open(databasePath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(databasePath).Validate(connection));
            Console.WriteLine("MIGRATION_DOT_DOT_ESCAPE_REJECTED=PASS");
        }
        finally { DeleteDirectory(outsideRoot); }
    }

    private static void VerifiesConnectionPathMismatchIsRejected()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-mismatch-");
        try
        {
            var contextPath = Path.Combine(root, "context.db");
            var connectionPath = Path.Combine(root, "connection.db");
            using var connection = Open(connectionPath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(contextPath).Validate(connection));
            Console.WriteLine("MIGRATION_CONNECTION_PATH_MISMATCH_REJECTED=PASS");
        }
        finally { DeleteDirectory(root); }
    }

    private static void VerifiesProductionPathIsRejectedWithoutOpeningProduction()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-production-");
        try
        {
            var connectionPath = Path.Combine(root, "connection.db");
            using var connection = Open(connectionPath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(Path.Combine("D:\\NetConsoleData", "catalog.db")).Validate(connection));
            Console.WriteLine("MIGRATION_PRODUCTION_PATH_REJECTED=PASS");
        }
        finally { DeleteDirectory(root); }
    }

    private static void VerifiesMissingExplicitFlagIsRejected()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-flag-");
        var previousGate = Environment.GetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, null);
            var databasePath = Path.Combine(root, "catalog.db");
            using var connection = Open(databasePath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(databasePath).Validate(connection));
            Console.WriteLine("MIGRATION_WITHOUT_EXPLICIT_FLAG_REJECTED=PASS");
        }
        finally
        {
            Environment.SetEnvironmentVariable(P2B2MigrationContext.ExplicitMigrationEnvironmentVariable, previousGate);
            DeleteDirectory(root);
        }
    }

    private static void VerifiesMissingIsolatedContextIsRejected()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-context-");
        try
        {
            var databasePath = Path.Combine(root, "catalog.db");
            using var connection = Open(databasePath);
            ExpectRejected(() => new P2B2MigrationContext(databasePath, IsolatedTest: false).Validate(connection));
            Console.WriteLine("MIGRATION_WITHOUT_ISOLATED_CONTEXT_REJECTED=PASS");
        }
        finally { DeleteDirectory(root); }
    }

    private static bool VerifiesSymlinkEscapeIsRejected()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-symlink-");
        var link = Path.Combine(root, "database-link");
        var target = Path.Combine(root, "outside");
        try
        {
            Directory.CreateDirectory(target);
            var targetDatabase = Path.Combine(target, "catalog.db");
            using (Open(targetDatabase)) { }
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.WriteLine($"MIGRATION_SYMLINK_ESCAPE_REJECTED=SKIPPED_NO_SYMLINK_PERMISSION ({exception.GetType().Name})");
                return false;
            }

            var databasePath = Path.Combine(link, "catalog.db");
            using var connection = Open(databasePath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(databasePath).Validate(connection));
            Console.WriteLine("MIGRATION_SYMLINK_ESCAPE_REJECTED=PASS");
            return true;
        }
        finally
        {
            RemoveLink(link);
            DeleteDirectory(root);
        }
    }

    private static bool VerifiesJunctionEscapeIsRejected()
    {
        var root = CreateRoot("netconsole-next-p2b2-path-junction-");
        var link = Path.Combine(root, "database-junction");
        var target = Path.Combine(root, "outside");
        try
        {
            Directory.CreateDirectory(target);
            var targetDatabase = Path.Combine(target, "catalog.db");
            using (Open(targetDatabase)) { }
            if (!TryCreateJunction(link, target))
            {
                Console.WriteLine("MIGRATION_JUNCTION_ESCAPE_REJECTED=SKIPPED_NO_JUNCTION_PERMISSION");
                return false;
            }

            var databasePath = Path.Combine(link, "catalog.db");
            using var connection = Open(databasePath);
            ExpectRejected(() => P2B2MigrationContext.ForIsolatedTest(databasePath).Validate(connection));
            Console.WriteLine("MIGRATION_JUNCTION_ESCAPE_REJECTED=PASS");
            return true;
        }
        finally
        {
            RemoveLink(link);
            DeleteDirectory(root);
        }
    }

    private static void ExpectRejected(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("P2B-2 path security assertion failed: an unsafe migration context was accepted.");
    }

    private static string CreateRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TryCreateJunction(string link, string target)
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

    private static void RemoveLink(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: false); }
        catch (IOException) when (OperatingSystem.IsWindows()) { }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
    }

    private static string TrimEndingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 40 && Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (IOException) when (OperatingSystem.IsWindows()) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Thread.Sleep(100); }
        }
        if (Directory.Exists(path)) throw new IOException($"Could not remove P2B-2 path fixture root: {path}");
    }
}
