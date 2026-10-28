using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service.Tests;

internal static class P2B5ExportMigrationChild
{
    public static Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 2) throw new ArgumentException("Expected: --p2b5-export-migrate <database-path> <data-root>.");
        var databasePath = Path.GetFullPath(arguments[0]);
        var dataRoot = Path.GetFullPath(arguments[1]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        P2B5ExportMigration.Apply(connection, P2B5ExportMigrationContext.ForIsolatedTest(databasePath, dataRoot));
        Console.WriteLine("P2B5ExportMigration: Applied = True, BaseSchemaVersion = 14");
        return Task.CompletedTask;
    }
}
