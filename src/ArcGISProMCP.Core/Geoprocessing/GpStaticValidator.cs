using System.Globalization;
using System.Text;

namespace ArcGISProMCP.Core.Geoprocessing;

public enum GpValidationSeverity
{
    Warning,
    Error
}

/// <summary>One static-validation finding. <see cref="Position"/> is the index in the caller's value array.</summary>
public sealed record GpValidationIssue(
    string Code,
    GpValidationSeverity Severity,
    string Message,
    string? Parameter = null,
    int? Position = null);

/// <summary>
/// Outcome of a dry run. <see cref="Validated"/> is false when the tool could not be checked statically
/// (unknown or unindexed); <see cref="IsValid"/> only means no static error was found — the tool's own
/// validation at run time can still reject the request.
/// </summary>
public sealed record GpValidationResult(
    string Tool,
    bool Validated,
    GpToolSummary? ToolSummary,
    GpRiskAssessment? Risk,
    IReadOnlyList<GpValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != GpValidationSeverity.Error);
}

/// <summary>
/// Static (no ArcGIS) checks of a positional geoprocessing value array against catalog metadata:
/// tool exists / is deprecated, parameter count, required values present (not <c>#</c>), coded-value
/// membership, GPBoolean/GPLong/GPDouble parsing and numeric ranges, applied per element of
/// multivalue (<c>a;b;c</c>) values. Values are positional in arcpy signature order (derived omitted).
/// </summary>
public static class GpStaticValidator
{
    /// <summary>The geoprocessing placeholder for "use the default".</summary>
    public const string UnsetValue = "#";

    private static readonly string[] ToolboxExtensions = [".pyt", ".atbx", ".tbx"];

    public static GpValidationResult Validate(ToolboxCatalog catalog, string tool, IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(values);
        var name = (tool ?? string.Empty).Trim();
        if (catalog.Describe(name) is { } description) return Validate(description, values);

        if (ToolboxExtensions.Any(extension => name.Contains(extension, StringComparison.OrdinalIgnoreCase)))
        {
            var kind = name.Contains(".pyt", StringComparison.OrdinalIgnoreCase) ? GpToolboxKind.PythonToolbox : GpToolboxKind.LegacyBinary;
            return new GpValidationResult(name, false, null, GeoprocessingRiskPolicy.ForUnindexedToolbox(kind),
            [
                new GpValidationIssue("tool_unindexed", GpValidationSeverity.Warning,
                    $"'{name}' refers to a toolbox path that is not indexed; it cannot be validated statically and may execute user code.")
            ]);
        }

        var suggestions = catalog.Search(name, 3).Select(hit => hit.Tool.ExecutionName).ToArray();
        var hint = suggestions.Length == 0 ? string.Empty : $" Did you mean: {string.Join(", ", suggestions)}?";
        return new GpValidationResult(name, false, null, null,
        [
            new GpValidationIssue("tool_not_found", GpValidationSeverity.Error,
                $"Tool '{name}' is not in the toolbox catalog. Use the 'alias.ToolName' execution name.{hint}")
        ]);
    }

    public static GpValidationResult Validate(GpToolDescription description, IReadOnlyList<string?> values)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(values);
        var issues = new List<GpValidationIssue>();
        var tool = description.Tool;
        if (tool.Deprecated)
            issues.Add(new GpValidationIssue("tool_deprecated", GpValidationSeverity.Warning, $"{tool.ExecutionName} is deprecated."));

        var positional = description.PositionalParameters.ToArray();
        if (values.Count > positional.Length)
        {
            issues.Add(new GpValidationIssue("too_many_parameters", GpValidationSeverity.Error,
                $"{tool.ExecutionName} accepts {positional.Length} values but {values.Count} were supplied (derived outputs are not supplied)."));
        }

        foreach (var parameter in positional)
        {
            var position = parameter.Position!.Value;
            var value = position < values.Count ? values[position] : null;
            if (IsUnset(value))
            {
                if (parameter.Required && parameter.DefaultValue is null)
                {
                    issues.Add(new GpValidationIssue("required_parameter_missing", GpValidationSeverity.Error,
                        $"Required parameter '{parameter.Name}' ({parameter.DisplayName}) has no value.", parameter.Name, position));
                }

                continue;
            }

            CheckValue(parameter, value!, position, issues);
        }

        return new GpValidationResult(tool.ExecutionName, true, tool, description.Risk, issues);
    }

    /// <summary>Splits a multivalue string on ';' (quotes protect separators) and unquotes each element.</summary>
    public static IReadOnlyList<string> SplitMultiValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in value)
        {
            if (quote is null && character is '\'' or '"') quote = character;
            else if (quote == character) quote = null;
            if (character == ';' && quote is null)
            {
                parts.Add(Unquote(current.ToString()));
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        parts.Add(Unquote(current.ToString()));
        return parts.Where(part => part.Length > 0).ToArray();
    }

    private static void CheckValue(GpParameter parameter, string value, int position, List<GpValidationIssue> issues)
    {
        // Composite and value-table parameters accept several shapes; only their presence is checked.
        if (parameter.DataTypes.Count != 1) return;
        var elementType = parameter.DataTypes[0];
        var elements = parameter.MultiValue ? SplitMultiValue(value) : [value.Trim()];
        var coded = parameter.Domain?.CodedValues is { Count: > 0 } codedValues ? codedValues : null;
        var range = parameter.Domain?.Range;

        foreach (var element in elements)
        {
            if (coded is not null && !coded.Any(item => Matches(item, element)))
            {
                var allowed = string.Join(", ", coded.Select(item => item.Keyword is null ? item.Value : $"{item.Value}|{item.Keyword}"));
                issues.Add(new GpValidationIssue("invalid_coded_value", GpValidationSeverity.Error,
                    $"'{element}' is not a valid value for '{parameter.Name}'. Allowed: {allowed}.", parameter.Name, position));
                continue;
            }

            switch (elementType)
            {
                case "GPBoolean" when coded is null && !bool.TryParse(element, out _):
                    issues.Add(new GpValidationIssue("invalid_boolean", GpValidationSeverity.Error,
                        $"'{element}' is not a boolean for '{parameter.Name}' (use true or false).", parameter.Name, position));
                    break;
                case "GPLong":
                    if (!long.TryParse(element, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                        issues.Add(new GpValidationIssue("invalid_long", GpValidationSeverity.Error,
                            $"'{element}' is not an integer for '{parameter.Name}'.", parameter.Name, position));
                    else
                        CheckRange(parameter, range, integer, element, position, issues);
                    break;
                case "GPDouble":
                    if (!double.TryParse(element, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        issues.Add(new GpValidationIssue("invalid_double", GpValidationSeverity.Error,
                            $"'{element}' is not a number for '{parameter.Name}' (use '.' as the decimal separator).", parameter.Name, position));
                    else
                        CheckRange(parameter, range, number, element, position, issues);
                    break;
            }
        }
    }

    private static void CheckRange(GpParameter parameter, GpRange? range, double number, string element, int position, List<GpValidationIssue> issues)
    {
        if (range is null) return;
        var belowMinimum = TryParse(range.Minimum) is { } minimum && (range.MinimumInclusive ? number < minimum : number <= minimum);
        var aboveMaximum = TryParse(range.Maximum) is { } maximum && (range.MaximumInclusive ? number > maximum : number >= maximum);
        if (belowMinimum || aboveMaximum)
        {
            issues.Add(new GpValidationIssue("value_out_of_range", GpValidationSeverity.Error,
                $"'{element}' is outside the range [{range.Minimum ?? "-inf"}, {range.Maximum ?? "+inf"}] for '{parameter.Name}'.", parameter.Name, position));
        }
    }

    private static double? TryParse(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool Matches(GpCodedValue item, string element) =>
        string.Equals(item.Value, element, StringComparison.OrdinalIgnoreCase) ||
        (item.Keyword is { } keyword && string.Equals(keyword, element, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnset(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim() == UnsetValue;

    private static string Unquote(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] is '\'' or '"' && trimmed[^1] == trimmed[0] ? trimmed[1..^1] : trimmed;
    }
}
