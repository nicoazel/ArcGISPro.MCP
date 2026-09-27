using System.Collections.Frozen;

namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>
/// Static risk classification of geoprocessing tools from toolbox metadata. Precedence:
/// tool-level user code (Python toolboxes, non-system script tools) &gt; <see cref="GpRiskTier.Destructive"/>
/// &gt; Python expression parameters (<see cref="GpRiskTier.UserCode"/>) &gt; <see cref="GpRiskTier.ConsumesCredits"/>
/// &gt; curated <see cref="GpRiskTier.ReadOnlyQuery"/> &gt; <see cref="GpRiskTier.Standard"/>.
/// The attribute <c>no_data_change</c> is deliberately ignored: management.Delete carries it.
/// </summary>
public static class GeoprocessingRiskPolicy
{
    /// <summary>Tool modifies its input in place (DeleteFeatures, CalculateField, AddField, ...).</summary>
    public const string InputDataChangeAttribute = "input_data_change";

    /// <summary>Tool edits inside an edit session (Append, DeleteRows, CalculateGeometryAttributes, ...).</summary>
    public const string EditSessionAttribute = "edit_session";

    /// <summary>Tool consumes ArcGIS Online credits.</summary>
    public const string CreditsAttribute = "credits";

    /// <summary>Allowlisted system tools that only read and report. Nothing else is ReadOnlyQuery.</summary>
    public static IReadOnlySet<string> ReadOnlyQueryTools { get; } = new[]
    {
        "management.GetCount",
        "management.GetRasterProperties",
        "management.GetCellValue"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// System tools that delete, rename or empty data without declaring <c>input_data_change</c>
    /// (or where the attribute alone is too weak a signal). Names verified against Pro 3.7.
    /// </summary>
    public static IReadOnlySet<string> CuratedDestructiveTools { get; } = new[]
    {
        "management.Delete",
        "management.DeleteMultiple",
        "management.Rename",
        "management.DeleteIdentical",
        "management.TruncateTable",
        "management.DeleteRows",
        "management.DeleteFeatures",
        "management.DeleteField",
        "management.DeleteDomain",
        "management.DeleteCodedValueFromDomain",
        "management.DeleteSchemaGeodatabase",
        "management.DeleteMosaicDataset",
        "management.DeleteVersion",
        "management.DeleteDatabaseSequence",
        "management.DeleteColormap"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attributes that mark a tool as modifying data in place.</summary>
    public static IReadOnlySet<string> DestructiveAttributes { get; } =
        new[] { InputDataChangeAttribute, EditSessionAttribute }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Coded values that select Python as an expression language (CalculateField, CalculateValue).</summary>
    public static IReadOnlySet<string> PythonExpressionTypes { get; } =
        new[] { "PYTHON", "PYTHON3", "PYTHON_9.3" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ScriptToolTypes =
        new[] { "ScriptTool", "PythonScriptTool" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the curated read-only query allowlist (callers must also require a system tool).</summary>
    public static bool IsReadOnlyQuery(string executionName) =>
        !string.IsNullOrWhiteSpace(executionName) && ReadOnlyQueryTools.Contains(executionName.Trim());

    /// <summary>Classifies one indexed tool.</summary>
    public static GpRiskAssessment Assess(
        string executionName,
        string toolType,
        bool isSystem,
        IReadOnlyCollection<string> attributes,
        IReadOnlyList<GpParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(executionName);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(parameters);
        var reasons = new List<string>();

        var toolIsUserCode = !isSystem && ScriptToolTypes.Contains(toolType);
        if (toolIsUserCode)
            reasons.Add($"{toolType} from a non-system toolbox runs user-authored Python.");
        else if (isSystem && ScriptToolTypes.Contains(toolType))
            reasons.Add($"System {toolType}: implemented in Esri-shipped Python (informational).");
        else if (!isSystem && string.Equals(toolType, "ModelTool", StringComparison.OrdinalIgnoreCase))
            reasons.Add("User model tool: it chains other tools, which are not inspected here.");

        var mutatingAttributes = attributes.Where(DestructiveAttributes.Contains).ToArray();
        var curated = CuratedDestructiveTools.Contains(executionName);
        var mutatesInput = mutatingAttributes.Length > 0 || curated;
        if (mutatingAttributes.Length > 0)
            reasons.Add($"Declares {string.Join(", ", mutatingAttributes)}: modifies input data in place.");
        if (curated)
            reasons.Add("Curated destructive tool: deletes, renames or empties data.");

        var pythonParameters = parameters.Where(AcceptsPython).Select(parameter => parameter.Name).ToArray();
        var acceptsPython = pythonParameters.Length > 0;
        if (acceptsPython)
            reasons.Add($"Parameter(s) {string.Join(", ", pythonParameters)} can select a Python expression.");

        var consumesCredits = attributes.Contains(CreditsAttribute, StringComparer.OrdinalIgnoreCase);
        if (consumesCredits)
            reasons.Add("Consumes ArcGIS Online credits.");

        var readOnly = isSystem && IsReadOnlyQuery(executionName);
        GpRiskTier tier;
        if (toolIsUserCode) tier = GpRiskTier.UserCode;
        else if (mutatesInput) tier = GpRiskTier.Destructive;
        else if (acceptsPython) tier = GpRiskTier.UserCode;
        else if (consumesCredits) tier = GpRiskTier.ConsumesCredits;
        else if (readOnly) tier = GpRiskTier.ReadOnlyQuery;
        else tier = GpRiskTier.Standard;

        if (tier == GpRiskTier.ReadOnlyQuery)
            reasons.Add("Curated read-only query tool.");
        return new GpRiskAssessment(tier, mutatesInput, toolIsUserCode || acceptsPython, acceptsPython, consumesCredits, reasons);
    }

    /// <summary>Classification for toolboxes whose tools cannot be indexed (.pyt, legacy binary .tbx).</summary>
    public static GpRiskAssessment ForUnindexedToolbox(GpToolboxKind kind) =>
        new(
            GpRiskTier.UserCode,
            MutatesInput: false,
            ExecutesUserCode: true,
            AcceptsPythonExpression: false,
            ConsumesCredits: false,
            [kind == GpToolboxKind.PythonToolbox
                ? "Python toolbox: unindexed; executes user code."
                : "Legacy binary toolbox: unindexed; may contain script tools that execute user code."]);

    private static bool AcceptsPython(GpParameter parameter) => DomainAcceptsPython(parameter.Domain, 0);

    private static bool DomainAcceptsPython(GpDomain? domain, int depth)
    {
        if (domain is null || depth > 8) return false;
        if (domain.CodedValues is { } values &&
            values.Any(value => PythonExpressionTypes.Contains(value.Value) || (value.Keyword is { } keyword && PythonExpressionTypes.Contains(keyword))))
            return true;
        return domain.Items?.Any(item => DomainAcceptsPython(item, depth + 1)) ?? false;
    }
}
