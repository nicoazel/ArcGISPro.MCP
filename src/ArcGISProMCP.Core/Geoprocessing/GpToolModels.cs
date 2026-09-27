namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>How a toolbox is stored on disk.</summary>
public enum GpToolboxKind
{
    /// <summary>A Pro-format toolbox expanded into a directory (system <c>*.tbx</c> folders).</summary>
    Directory,

    /// <summary>A Pro-format <c>.atbx</c> zip archive with the same layout as a directory toolbox.</summary>
    Archive,

    /// <summary>A Python toolbox (<c>.pyt</c>). Its metadata only exists by running its Python source.</summary>
    PythonToolbox,

    /// <summary>A legacy ArcMap binary <c>.tbx</c> file whose format is not documented.</summary>
    LegacyBinary
}

/// <summary>Risk tier of a geoprocessing tool, ordered from least to most review-worthy.</summary>
public enum GpRiskTier
{
    /// <summary>Curated read-only query tool (see <see cref="GeoprocessingRiskPolicy.ReadOnlyQueryTools"/>).</summary>
    ReadOnlyQuery,

    /// <summary>Creates new outputs; does not declare in-place modification of its inputs.</summary>
    Standard,

    /// <summary>Consumes ArcGIS Online credits (external, billable side effect); no in-place input change declared.</summary>
    ConsumesCredits,

    /// <summary>Modifies, deletes or renames its input data in place.</summary>
    Destructive,

    /// <summary>Runs user-authored code (Python toolboxes, custom script tools, Python expression parameters).</summary>
    UserCode
}

/// <summary>Direction of a geoprocessing parameter.</summary>
public enum GpParameterDirection
{
    Input,
    Output
}

/// <summary>Whether a parameter must be supplied, may be supplied, or is produced by the tool.</summary>
public enum GpParameterUsage
{
    Required,
    Optional,
    Derived
}

/// <summary>Toolbox-level summary. Unindexed toolboxes (.pyt, legacy .tbx) are listed but have no tools.</summary>
public sealed record GpToolboxInfo(
    string Path,
    string Name,
    string? Alias,
    string DisplayName,
    string? Description,
    GpToolboxKind Kind,
    bool IsSystem,
    bool Indexed,
    int ToolCount,
    string? UnindexedReason,
    bool MayExecuteUserCode);

/// <summary>Search/list level view of a tool.</summary>
public sealed record GpToolSummary(
    string ExecutionName,
    string Name,
    string DisplayName,
    string? Summary,
    string Toolbox,
    string ToolboxAlias,
    string? Toolset,
    string ToolType,
    GpRiskTier RiskTier,
    bool Deprecated,
    bool IsSystem);

/// <summary>One ranked search result.</summary>
public sealed record GpSearchHit(GpToolSummary Tool, double Score, IReadOnlyList<string> MatchedTerms);

/// <summary>
/// A coded value. <see cref="Value"/> is what the tool stores; <see cref="Keyword"/> is the alternative
/// scripting keyword (booleans use it, e.g. <c>true</c> / <c>DISSOLVE</c>); <see cref="Label"/> is the UI text.
/// </summary>
public sealed record GpCodedValue(string Value, string? Label, string? Keyword);

/// <summary>Inclusive/exclusive numeric bounds from a range or numeric domain (raw invariant strings).</summary>
public sealed record GpRange(string? Minimum, string? Maximum, bool MinimumInclusive = true, bool MaximumInclusive = true);

/// <summary>Flattened parameter domain. Only the members relevant to <see cref="Type"/> are populated.</summary>
public sealed record GpDomain(
    string Type,
    IReadOnlyList<GpCodedValue>? CodedValues = null,
    GpRange? Range = null,
    IReadOnlyList<string>? FileTypes = null,
    IReadOnlyList<string>? GeometryTypes = null,
    IReadOnlyList<string>? FieldTypes = null,
    IReadOnlyList<string>? Units = null,
    IReadOnlyList<GpDomain>? Items = null);

/// <summary>
/// A tool parameter. <see cref="Position"/> is the zero-based index in an execution value array
/// (arcpy signature order: definition order with derived parameters removed); it is null for derived
/// parameters, which callers never supply. <see cref="DisplayOrder"/> is the tool dialog order.
/// </summary>
public sealed record GpParameter(
    string Name,
    string DisplayName,
    string? Description,
    string DataType,
    IReadOnlyList<string> DataTypes,
    bool MultiValue,
    GpParameterUsage Usage,
    GpParameterDirection Direction,
    string? DefaultValue,
    GpDomain? Domain,
    IReadOnlyList<string> DependsOn,
    string? Category,
    bool Enabled,
    int DefinitionIndex,
    int? Position,
    int DisplayOrder)
{
    /// <summary>True when the caller must supply a value (not optional and not derived).</summary>
    public bool Required => Usage == GpParameterUsage.Required;
}

/// <summary>Risk classification with every signal that contributed to it.</summary>
public sealed record GpRiskAssessment(
    GpRiskTier Tier,
    bool MutatesInput,
    bool ExecutesUserCode,
    bool AcceptsPythonExpression,
    bool ConsumesCredits,
    IReadOnlyList<string> Reasons);

/// <summary>Full description of one tool.</summary>
public sealed record GpToolDescription(
    GpToolSummary Tool,
    string? Description,
    IReadOnlyList<GpParameter> Parameters,
    IReadOnlyList<string> Environments,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<string> Keywords,
    GpRiskAssessment Risk,
    string ToolboxPath)
{
    /// <summary>Parameters a caller supplies, in execution (positional) order.</summary>
    public IEnumerable<GpParameter> PositionalParameters =>
        Parameters.Where(parameter => parameter.Position is not null).OrderBy(parameter => parameter.Position);
}
