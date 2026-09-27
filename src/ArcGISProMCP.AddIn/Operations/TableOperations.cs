using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class TableQueryOperation() : ProOperationBase(OperationDescriptor.Create(
    "table.query", "Query feature attributes",
    "Runs a bounded read-only attribute query against a feature layer and returns plain JSON rows plus schema metadata.",
    TableOperationSchemas.QueryInput,
    capabilities: ["maps"], tags: ["table", "query", "sql", "attributes", "database"],
    aliases: ["query geodata", "select rows", "inspect attributes"], related: ["table.statistics", "gp.run"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var where = OptionalString(arguments, "where") ?? "1=1";
        QuerySafety.ValidateWhereClause(where);
        var limit = arguments.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, 500)
            : 100;
        var requestedFields = arguments.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Array
            ? fieldsElement.EnumerateArray().Select(field => field.GetString()).Where(field => !string.IsNullOrWhiteSpace(field)).Cast<string>().ToArray()
            : [];

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = ProHandles.ResolveLayer(map, layerReference) as BasicFeatureLayer
                ?? throw new InvalidOperationException("Attribute queries require a feature layer.");
            using var table = layer.GetTable();
            var definition = table.GetDefinition();
            var available = definition.GetFields().Where(field => field.FieldType != FieldType.Geometry).ToArray();
            var selected = requestedFields.Length == 0
                ? available.Select(field => field.Name).ToArray()
                : requestedFields.Select(name => available.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? throw new ArgumentException($"Unknown or unsupported field '{name}'.", nameof(arguments))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var filter = new QueryFilter { WhereClause = where, SubFields = string.Join(",", selected) };
            var rows = new List<Dictionary<string, object?>>();
            using var cursor = table.Search(filter, false);
            while (rows.Count < limit && cursor.MoveNext())
            {
                using var row = cursor.Current;
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var field in selected) values[field] = PlainValue(row[field]);
                rows.Add(values);
            }

            return new
            {
                map = ProHandles.ForMap(map),
                layer = ProHandles.ForLayer(layer),
                where,
                fields = selected,
                returned = rows.Count,
                limit,
                rows
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static object? PlainValue(object? value) => value switch
    {
        null or DBNull => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D"),
        byte[] bytes => new { byteLength = bytes.Length },
        string or bool or byte or short or int or long or float or double or decimal => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}

internal sealed class TableStatisticsOperation() : ProOperationBase(OperationDescriptor.Create(
    "table.statistics", "Summarize numeric field",
    "Computes bounded read-only count, null count, minimum, maximum, sum, and mean statistics for one numeric feature-layer field.",
    TableOperationSchemas.StatisticsInput,
    capabilities: ["maps"], tags: ["table", "statistics", "analysis", "database"],
    aliases: ["field stats", "average", "min max", "summarize values"], related: ["table.query", "gp.run"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var requestedField = RequiredString(arguments, "field");
        var where = OptionalString(arguments, "where") ?? "1=1";
        QuerySafety.ValidateWhereClause(where);
        var sampleLimit = arguments.TryGetProperty("sampleLimit", out var limitElement) && limitElement.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, 1_000_000)
            : 100_000;

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = ProHandles.ResolveLayer(map, layerReference) as BasicFeatureLayer
                ?? throw new InvalidOperationException("Statistics require a feature layer.");
            using var table = layer.GetTable();
            var field = table.GetDefinition().GetFields().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, requestedField, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown field '{requestedField}'.", nameof(arguments));
            if (field.FieldType is not (FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or FieldType.Single or FieldType.Double))
                throw new ArgumentException($"Field '{field.Name}' is not numeric.", nameof(arguments));

            var filter = new QueryFilter { WhereClause = where, SubFields = field.Name };
            long inspected = 0;
            long nullCount = 0;
            double sum = 0;
            double mean = 0;
            double? minimum = null;
            double? maximum = null;
            using var cursor = table.Search(filter, false);
            while (inspected < sampleLimit && cursor.MoveNext())
            {
                using var row = cursor.Current;
                var value = row[field.Name];
                inspected++;
                if (value is null or DBNull)
                {
                    nullCount++;
                    continue;
                }

                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var valueCount = inspected - nullCount;
                sum += number;
                mean += (number - mean) / valueCount;
                minimum = minimum is null ? number : Math.Min(minimum.Value, number);
                maximum = maximum is null ? number : Math.Max(maximum.Value, number);
            }

            var matched = table.GetCount(filter);
            return new
            {
                map = ProHandles.ForMap(map),
                layer = ProHandles.ForLayer(layer),
                field = field.Name,
                fieldType = field.FieldType.ToString(),
                where,
                matched,
                inspected,
                sampled = inspected < matched,
                nullCount,
                valueCount = inspected - nullCount,
                minimum,
                maximum,
                sum,
                mean = inspected == nullCount ? (double?)null : mean
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

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
