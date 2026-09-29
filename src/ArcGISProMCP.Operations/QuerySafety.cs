namespace ArcGISProMCP.Operations;

/// <summary>Where-clause guard shared by the table and feature queries.</summary>
internal static class QuerySafety
{
    private static readonly string[] ForbiddenTokens = [";", "--", "/*", "*/"];

    public static void ValidateWhereClause(string where)
    {
        if (where.Length > 4096) throw new ArgumentException("where clause exceeds 4096 characters.", nameof(where));
        if (ForbiddenTokens.Any(token => where.Contains(token, StringComparison.Ordinal)))
            throw new ArgumentException("where clause contains a forbidden statement or comment token.", nameof(where));
    }
}
