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

        if (arguments.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String &&
            IsCustomToolbox(tool.GetString()))
            return "the tool comes from a custom toolbox (.pyt/.atbx/.tbx) that can contain arbitrary Python";

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
