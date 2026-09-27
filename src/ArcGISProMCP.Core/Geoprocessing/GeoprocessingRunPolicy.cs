using System.Text.RegularExpressions;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>Resolution of a <c>gp.query</c> tool name. Exactly one of <see cref="Tool"/> or <see cref="ErrorCode"/> is set.</summary>
public sealed record GpQueryResolution(GpToolDescription? Tool, string? ErrorCode, string? Message)
{
    public bool Succeeded => Tool is not null;
}

/// <summary>
/// Static preview of a <c>gp.run</c> request: catalog validation plus the tier, confirmation and
/// user-code flags that decide how it would be gated. Nothing is executed to produce it.
/// </summary>
public sealed record GpRunDryRunReport(
    GpValidationResult Validation,
    GpRiskAssessment? Risk,
    bool ExecutesUserCode,
    bool RequiresConfirmation,
    bool AutonomousMode,
    OperationRefusal? UnattendedRefusal,
    string? ApprovalWarning)
{
    /// <summary>The risk tier, or null when the tool is not in the catalog (risk unknown).</summary>
    public GpRiskTier? RiskTier => Risk?.Tier;

    /// <summary>True when autonomous mode is on and would refuse this request.</summary>
    public bool WouldBeRefused => AutonomousMode && UnattendedRefusal is not null;

    /// <summary>No static error was found and the request would not be refused. The tool can still reject it at run time.</summary>
    public bool Valid => Validation.IsValid && !WouldBeRefused;
}

/// <summary>
/// How <c>gp.run</c> and <c>gp.query</c> use the toolbox catalog's risk assessment: which requests
/// autonomous mode refuses, what the approval card and result notices say, and which tool names
/// <c>gp.query</c> may execute. Classification itself lives in <see cref="GeoprocessingRiskPolicy"/>.
/// </summary>
public static partial class GeoprocessingRunPolicy
{
    /// <summary>Error code when autonomous mode would run a Destructive or UserCode tool without review.</summary>
    public const string DestructiveToolRequiresReviewCode = "destructive_tool_requires_review";

    /// <summary>Result notice: the tool modifies or deletes its input data in place.</summary>
    public const string MutatesInputNoticeCode = "gp_mutates_input";

    /// <summary>Result notice: the tool consumes ArcGIS Online credits.</summary>
    public const string ConsumesCreditsNoticeCode = "gp_consumes_credits";

    /// <summary>Result notice: the tool is not in the catalog, so its risk tier is unknown.</summary>
    public const string ToolNotIndexedNoticeCode = "gp_tool_not_indexed";

    public const string MutatesInputWarning = "Modifies/deletes input data in place; there is no automatic undo.";

    public const string ConsumesCreditsWarning = "Consumes ArcGIS Online credits (billable).";

    private static readonly string[] ToolboxExtensions = [".pyt", ".atbx", ".tbx"];

    /// <summary>
    /// Risk of a <c>gp.run</c> tool name: the catalog assessment for an indexed tool, the curated
    /// Destructive assessment for a known destructive system tool the catalog did not index (either
    /// <c>alias.Name</c> or <c>Name_alias</c>), the unindexed user-code assessment for a toolbox path,
    /// or null when the tool is unknown to the catalog.
    /// </summary>
    public static GpRiskAssessment? Assess(ToolboxCatalog catalog, string? tool)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var name = tool?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (catalog.Describe(name) is { } description) return description.Risk;
        if (IsCuratedDestructive(name)) return GeoprocessingRiskPolicy.ForCuratedDestructive();
        if (!ToolboxExtensions.Any(extension => name.Contains(extension, StringComparison.OrdinalIgnoreCase))) return null;
        return GeoprocessingRiskPolicy.ForUnindexedToolbox(
            name.Contains(".pyt", StringComparison.OrdinalIgnoreCase) ? GpToolboxKind.PythonToolbox : GpToolboxKind.LegacyBinary);
    }

    /// <summary>
    /// The refusal autonomous mode applies before running a tool: Destructive and UserCode tiers, any
    /// request the conservative user-code detector flags (for example a Python expression on a tool
    /// the catalog does not know), and any tool that could not be classified at all (fail closed).
    /// Null means the request may run unattended.
    /// </summary>
    public static OperationRefusal? UnattendedRefusal(string tool, GpRiskAssessment? risk, bool userCodeDetected)
    {
        var reasons = risk is null ? string.Empty : $" ({string.Join(" ", risk.Reasons)})";
        return risk?.Tier switch
        {
            null when !userCodeDetected => new OperationRefusal(DestructiveToolRequiresReviewCode,
                $"'{tool}' could not be classified: it is not in the toolbox catalog, so its risk tier is unknown and review is required. " +
                "Autonomous mode only runs tools it can classify; use gp.search to find the execution name, or run it with local review in the ArcGIS Pro dockpane."),
            GpRiskTier.Destructive => new OperationRefusal(DestructiveToolRequiresReviewCode,
                $"'{tool}' is a Destructive geoprocessing tool: it modifies/deletes input data in place{reasons}. " +
                "Autonomous mode does not run Destructive or UserCode tools; run it with local review in the ArcGIS Pro dockpane."),
            GpRiskTier.UserCode => new OperationRefusal(DestructiveToolRequiresReviewCode,
                $"'{tool}' runs user-authored code{reasons}. " +
                "Autonomous mode does not run Destructive or UserCode tools; run it with local review in the ArcGIS Pro dockpane."),
            _ when userCodeDetected => new OperationRefusal(DestructiveToolRequiresReviewCode,
                $"This '{tool}' request runs user code (a custom toolbox, a Python expression, or arcpy). " +
                "Autonomous mode does not run Destructive or UserCode tools; run it with local review in the ArcGIS Pro dockpane."),
            _ => null
        };
    }

    /// <summary>
    /// True when <paramref name="name"/> (<c>alias.Name</c> or <c>Name_alias</c>, any case) is on the
    /// curated destructive list, so a catalog miss (missing or partial install) cannot hide it.
    /// </summary>
    private static bool IsCuratedDestructive(string name)
    {
        if (GeoprocessingRiskPolicy.CuratedDestructiveTools.Contains(name)) return true;
        var underscore = name.LastIndexOf('_');
        return underscore > 0 && underscore < name.Length - 1 &&
            GeoprocessingRiskPolicy.CuratedDestructiveTools.Contains($"{name[(underscore + 1)..]}.{name[..underscore]}");
    }

    /// <summary>Approval-card text for data-changing and credit-consuming tools, or null.</summary>
    public static string? ApprovalWarning(GpRiskAssessment? risk)
    {
        if (risk is null) return null;
        var parts = new List<string>(2);
        if (risk.MutatesInput) parts.Add(MutatesInputWarning);
        if (risk.ConsumesCredits) parts.Add(ConsumesCreditsWarning);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>Result notices describing the tool's risk (attached whether or not the run succeeded).</summary>
    public static IReadOnlyList<OperationNotice> ResultNotices(string tool, GpRiskAssessment? risk)
    {
        if (risk is null)
        {
            return
            [
                new OperationNotice(ToolNotIndexedNoticeCode,
                    $"'{tool}' is not in the toolbox catalog, so its risk tier is unknown. Use gp.search to find the execution name.", "info")
            ];
        }

        var notices = new List<OperationNotice>(2);
        if (risk.MutatesInput)
            notices.Add(new OperationNotice(MutatesInputNoticeCode, $"'{tool}' modifies/deletes its input data in place.", "warning"));
        if (risk.ConsumesCredits)
            notices.Add(new OperationNotice(ConsumesCreditsNoticeCode, $"'{tool}' consumes ArcGIS Online credits.", "warning"));
        return notices;
    }

    /// <summary>Statically previews a <c>gp.run</c> request (positional values, <c>#</c> for unset).</summary>
    public static GpRunDryRunReport DryRun(
        ToolboxCatalog catalog,
        string tool,
        IReadOnlyList<string?> values,
        bool userCodeDetected,
        bool requiresConfirmation,
        bool autonomousMode)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(values);
        var name = (tool ?? string.Empty).Trim();
        var validation = GpStaticValidator.Validate(catalog, name, values);
        var risk = validation.Risk ?? Assess(catalog, name);
        return new GpRunDryRunReport(
            validation,
            risk,
            userCodeDetected || (risk?.ExecutesUserCode ?? false),
            requiresConfirmation,
            autonomousMode,
            UnattendedRefusal(name, risk, userCodeDetected),
            ApprovalWarning(risk));
    }

    /// <summary>
    /// Resolves a <c>gp.query</c> tool: the name must be a plain <c>alias.Name</c>
    /// (<c>^[a-z0-9]+\.[A-Za-z0-9]+$</c>, so no paths or toolbox files), be on the read-only
    /// allowlist, and resolve to that tool in a system toolbox (never a user toolbox).
    /// </summary>
    public static GpQueryResolution ResolveQueryTool(ToolboxCatalog catalog, string? tool)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var name = tool ?? string.Empty;
        if (!QueryToolNamePattern().IsMatch(name))
        {
            return new GpQueryResolution(null, "invalid_tool_name",
                "gp.query tools are named 'alias.ToolName' (lowercase alias, letters and digits only), for example 'management.GetCount'.");
        }

        var allowed = string.Join(", ", GeoprocessingRiskPolicy.ReadOnlyQueryTools.Order(StringComparer.Ordinal));
        if (!GeoprocessingRiskPolicy.IsReadOnlyQuery(name))
        {
            return new GpQueryResolution(null, "tool_not_query_allowed",
                $"'{name}' is not a read-only query tool. gp.query runs only: {allowed}. Use gp.run (with review) for other tools.");
        }

        var description = catalog.Describe(name);
        if (description is null || !description.Tool.IsSystem ||
            !string.Equals(description.Tool.ExecutionName, name, StringComparison.OrdinalIgnoreCase))
        {
            return new GpQueryResolution(null, "tool_not_found",
                $"'{name}' was not found in the ArcGIS Pro system toolboxes.");
        }

        if (description.Risk.Tier != GpRiskTier.ReadOnlyQuery)
        {
            return new GpQueryResolution(null, "tool_not_query_allowed",
                $"'{name}' is classified {description.Risk.Tier} by its metadata, so gp.query will not run it.");
        }

        return new GpQueryResolution(description, null, null);
    }

    // \z rather than $: '$' would also accept a trailing newline.
    [GeneratedRegex(@"^[a-z0-9]+\.[A-Za-z0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex QueryToolNamePattern();
}
