using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Execution;

/// <summary>
/// Conservative, advisory detection of requests that will run caller-chosen Python. It only
/// labels approvals and results; it is not a sandbox and never makes a request safe. Operations
/// whose descriptor does not declare <see cref="OperationDescriptor.ExecutesUserCode"/> are
/// never flagged.
/// </summary>
public static class UserCodeExecutionDetector
{
    public const string NoticeCode = "user_code_execution";

    private static readonly string[] PythonExpressionTypes = ["PYTHON", "PYTHON3", "PYTHON_9.3"];
    private static readonly string[] CustomToolboxExtensions = [".pyt", ".atbx", ".tbx"];

    // System tools that evaluate an expression. ArcGIS Pro defaults their expression type to
    // PYTHON3, so they run Python even when no parameter names the expression type.
    private static readonly string[] ExpressionEvaluatingTools = ["CalculateField", "CalculateFields", "CalculateValue"];

    /// <summary>Returns true when the request will (or is likely to) execute user-supplied Python.</summary>
    public static bool RunsUserCode(OperationDescriptor descriptor, JsonElement arguments) =>
        Detect(descriptor, arguments) is not null;

    /// <summary>A short human-readable warning for the approval card, or null when none applies.</summary>
    public static string? GetWarning(OperationDescriptor descriptor, JsonElement arguments) =>
        Detect(descriptor, arguments) is { } reason
            ? $"Runs user code: {reason}. Approve only if you trust the code; it runs with your ArcGIS Pro permissions."
            : null;

    private static string? Detect(OperationDescriptor descriptor, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.ExecutesUserCode) return null;
        if (descriptor.Id.StartsWith("arcpy.", StringComparison.OrdinalIgnoreCase))
            return "this request runs a Python script in the ArcGIS Pro Python environment";
        if (arguments.ValueKind != JsonValueKind.Object) return null;

        if (arguments.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
        {
            var toolName = tool.GetString();
            if (IsCustomToolbox(toolName))
                return "the tool comes from a custom toolbox (.pyt/.atbx/.tbx) that can contain arbitrary Python";
            if (IsExpressionEvaluatingTool(toolName))
                return "the tool evaluates an expression that defaults to Python";
        }

        if (arguments.TryGetProperty("parameters", out var parameters) && ContainsPythonMarker(parameters))
            return "a parameter selects a Python expression or references arcpy";

        return null;
    }

    private static bool IsCustomToolbox(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return false;
        var value = tool.Trim();
        foreach (var extension in CustomToolboxExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                value.Contains(extension + "\\", StringComparison.OrdinalIgnoreCase) ||
                value.Contains(extension + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Matches the tool segment after the last '.', '\' or '/' (for example
    /// <c>management.CalculateField</c>), also accepting the <c>CalculateField_management</c> form.
    /// </summary>
    private static bool IsExpressionEvaluatingTool(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return false;
        var value = tool.Trim();
        var segment = value[(value.LastIndexOfAny(['.', '\\', '/']) + 1)..];
        if (Matches(segment)) return true;
        var underscore = segment.LastIndexOf('_');
        return underscore > 0 && Matches(segment[..underscore]);

        static bool Matches(string name) => ExpressionEvaluatingTools.Any(candidate =>
            string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsPythonMarker(JsonElement value, int depth = 0)
    {
        if (depth > 16) return false;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                var trimmed = text.Trim();
                return PythonExpressionTypes.Any(type => string.Equals(trimmed, type, StringComparison.OrdinalIgnoreCase)) ||
                    text.Contains("arcpy.", StringComparison.OrdinalIgnoreCase);
            case JsonValueKind.Array:
                return value.EnumerateArray().Any(item => ContainsPythonMarker(item, depth + 1));
            case JsonValueKind.Object:
                return value.EnumerateObject().Any(property => ContainsPythonMarker(property.Value, depth + 1));
            default:
                return false;
        }
    }
}
