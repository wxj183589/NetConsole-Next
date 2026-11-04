using Microsoft.Data.Sqlite;

namespace NetConsole.Next.Service;

internal sealed class DeviceSiteScopeException(string code, string detail) : InvalidOperationException(detail)
{
    public string Code { get; } = code;
}

internal static class DeviceSiteScope
{
    public static bool Allows(P2B5SiteScopeDecision? scope, string siteId) =>
        scope is null || scope.IsGlobal || scope.IsAuthorized && scope.AllowedSiteReferences.Contains(siteId);

    public static void Require(P2B5SiteScopeDecision? scope, string siteId)
    {
        if (scope is not null && !Allows(scope, siteId))
            throw new DeviceSiteScopeException("DEVICE_SITE_SCOPE_DENIED", "The requested Site is outside the current user scope.");
    }

    public static string BuildSqlFilter(P2B5SiteScopeDecision? scope, List<(string Name, object? Value)> parameters,
        string column, string parameterPrefix = "$deviceScopeSite")
    {
        if (scope is null || scope.IsGlobal) return "1=1";
        if (!scope.IsAuthorized || scope.AllowedSiteReferences.Count == 0) return "1=0";

        var placeholders = new List<string>();
        var index = 0;
        foreach (var site in scope.AllowedSiteReferences.Order(StringComparer.Ordinal))
        {
            var name = $"{parameterPrefix}{index++}";
            placeholders.Add(name);
            parameters.Add((name, site));
        }
        return $"{column} IN ({string.Join(',', placeholders)})";
    }

    public static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
