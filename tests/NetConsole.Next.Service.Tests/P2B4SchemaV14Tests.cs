using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service.Tests;

internal static class P2B4MigrationChild
{
    public static Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 2)
            throw new ArgumentException("Expected: --p2b4-migrate <database-path> <data-root>.");
        var databasePath = Path.GetFullPath(arguments[0]);
        var dataRoot = Path.GetFullPath(arguments[1]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        var result = P2B4SchemaV14Migration.Apply(connection,
            P2B4MigrationContext.ForIsolatedTest(databasePath, dataRoot));
        Console.WriteLine($"P2B4 child migration complete: {result}");
        return Task.CompletedTask;
    }
}
