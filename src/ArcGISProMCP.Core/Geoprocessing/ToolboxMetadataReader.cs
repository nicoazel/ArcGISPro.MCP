using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>Result of reading one Pro-format toolbox.</summary>
public sealed record ToolboxReadResult(GpToolboxInfo Toolbox, IReadOnlyList<GpToolDescription> Tools, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads Pro-format toolbox metadata (<c>toolbox.content</c>, <c>toolbox.content.rc</c>,
/// <c>&lt;Tool&gt;.tool/tool.content</c>, <c>tool.content.rc</c>, <c>tool.keywords.rc</c>) from any
/// <see cref="IToolboxSource"/>. It never executes toolbox code.
/// </summary>
public static partial class ToolboxMetadataReader
{
    /// <summary>Prefix of a localized resource reference, resolved against the sibling <c>.rc</c> map.</summary>
    public const string ResourcePrefix = "$rc:";

    private const int MaximumSummaryLength = 300;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64
    };

    /// <summary>Reads the toolbox and every tool listed in its toolsets. Unreadable tools become warnings.</summary>
    public static ToolboxReadResult Read(IToolboxSource source, bool isSystem, GpDeprecationList? deprecated = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var warnings = new List<string>();
        var name = Path.GetFileNameWithoutExtension(source.Path.TrimEnd('\\', '/'));
        var contentText = source.ReadText("toolbox.content")
            ?? throw new InvalidDataException($"Toolbox '{source.Path}' has no toolbox.content.");

        using var document = JsonDocument.Parse(contentText, JsonOptions);
        var root = document.RootElement;
        var resources = ReadResourceMap(source.ReadText("toolbox.content.rc"));
        var displayName = Resolve(GetString(root, "displayname"), resources) ?? name;
        var description = CleanText(Resolve(GetString(root, "description"), resources));
        var alias = GetString(root, "alias");
        if (string.IsNullOrWhiteSpace(alias))
        {
            alias = new string(name.Where(char.IsLetterOrDigit).ToArray());
            warnings.Add($"Toolbox '{source.Path}' declares no alias; using '{alias}'.");
        }

        var tools = new List<GpToolDescription>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("toolsets", out var toolsets) && toolsets.ValueKind == JsonValueKind.Object)
        {
            foreach (var toolset in toolsets.EnumerateObject())
            {
                var toolsetName = toolset.Name == "<root>" ? null : Resolve(toolset.Name, resources);
                if (!toolset.Value.TryGetProperty("tools", out var toolList) || toolList.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in toolList.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String) continue;
                    // Entries are "Name" (folder Name.tool) or "Name:Folder.tool".
                    var text = entry.GetString() ?? string.Empty;
                    var separator = text.IndexOf(':', StringComparison.Ordinal);
                    var toolName = (separator < 0 ? text : text[..separator]).Trim();
                    var folder = separator < 0 ? toolName + ".tool" : text[(separator + 1)..].Trim();
                    if (toolName.Length == 0 || !seen.Add(toolName)) continue;
                    try
                    {
                        var tool = ReadTool(source, folder, toolName, alias, displayName, name, toolsetName, isSystem, deprecated);
                        if (tool is null)
                            warnings.Add($"{alias}.{toolName}: '{folder}/tool.content' is missing.");
                        else
                            tools.Add(tool);
                    }
                    catch (JsonException exception)
                    {
                        warnings.Add($"{alias}.{toolName}: invalid tool metadata ({exception.Message}).");
                    }
                }
            }
        }

        var info = new GpToolboxInfo(
            source.Path,
            name,
            alias,
            displayName,
            description,
            source.Kind,
            isSystem,
            Indexed: true,
            tools.Count,
            UnindexedReason: null,
            MayExecuteUserCode: tools.Any(tool => tool.Risk.ExecutesUserCode));
        return new ToolboxReadResult(info, tools, warnings);
    }

    /// <summary>Resolves a <c>$rc:key</c> reference; a missing key falls back to the raw key text.</summary>
    public static string? Resolve(string? value, IReadOnlyDictionary<string, string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (value is null || !value.StartsWith(ResourcePrefix, StringComparison.Ordinal)) return value;
        var key = value[ResourcePrefix.Length..];
        return resources.TryGetValue(key, out var text) ? text : key;
    }

    /// <summary>Parses an <c>.rc</c> file (<c>{"map": {key: text}}</c>) into a case-insensitive map.</summary>
    public static IReadOnlyDictionary<string, string> ReadResourceMap(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;
        using var document = JsonDocument.Parse(text, JsonOptions);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("map", out var entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                    map[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }

        return map;
    }

    /// <summary>Strips the <c>&lt;xdoc&gt;</c> HTML used by help text, decodes entities and collapses whitespace.</summary>
    public static string? CleanText(string? text)
    {
        if (text is null) return null;
        var stripped = HtmlTag().Replace(text, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        var collapsed = Whitespace().Replace(decoded, " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    private static GpToolDescription? ReadTool(
        IToolboxSource source,
        string folder,
        string toolName,
        string alias,
        string toolboxDisplayName,
        string toolboxName,
        string? toolset,
        bool isSystem,
        GpDeprecationList? deprecated)
    {
        var contentText = source.ReadText($"{folder}/tool.content");
        if (contentText is null) return null;

        using var document = JsonDocument.Parse(contentText, JsonOptions);
        var root = document.RootElement;
        var resources = ReadResourceMap(source.ReadText($"{folder}/tool.content.rc"));
        var keywords = ReadKeywords(source.ReadText($"{folder}/tool.keywords.rc"));
        var type = GetString(root, "type") ?? "Unknown";
        var displayName = Resolve(GetString(root, "displayname"), resources) ?? toolName;
        var description = CleanText(Resolve(GetString(root, "description"), resources));
        var attributes = GetStrings(root, "attributes");
        var environments = GetStrings(root, "environments");

        var parameters = new List<GpParameter>();
        if (root.TryGetProperty("params", out var parameterElements) && parameterElements.ValueKind == JsonValueKind.Object)
        {
            var definitionIndex = 0;
            var position = 0;
            foreach (var property in parameterElements.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var parameter = ReadParameter(property.Name, property.Value, resources, definitionIndex++, ref position);
                parameters.Add(parameter);
            }
        }

        var ordered = parameters
            .OrderBy(parameter => parameter.DisplayOrder)
            .ThenBy(parameter => parameter.DefinitionIndex)
            .ToArray();
        var executionName = $"{alias}.{toolName}";
        var risk = GeoprocessingRiskPolicy.Assess(executionName, type, isSystem, attributes, ordered);
        var summary = new GpToolSummary(
            executionName,
            toolName,
            displayName,
            Truncate(description, MaximumSummaryLength),
            toolboxDisplayName,
            alias,
            toolset,
            type,
            risk.Tier,
            deprecated?.IsDeprecated(toolboxName, toolboxDisplayName, toolName) ?? false,
            isSystem);
        return new GpToolDescription(summary, description, ordered, environments, attributes, keywords, risk, source.Path);
    }

    private static GpParameter ReadParameter(
        string name,
        JsonElement element,
        IReadOnlyDictionary<string, string> resources,
        int definitionIndex,
        ref int position)
    {
        var usage = GetString(element, "type")?.ToUpperInvariant() switch
        {
            "OPTIONAL" => GpParameterUsage.Optional,
            "DERIVED" => GpParameterUsage.Derived,
            _ => GpParameterUsage.Required
        };
        var direction = string.Equals(GetString(element, "direction"), "out", StringComparison.OrdinalIgnoreCase)
            ? GpParameterDirection.Output
            : GpParameterDirection.Input;

        string dataType = "Unknown";
        var leaves = new List<string>();
        var multiValue = false;
        if (element.TryGetProperty("datatype", out var dataTypeElement) && dataTypeElement.ValueKind == JsonValueKind.Object)
        {
            dataType = FlattenDataType(dataTypeElement, leaves);
            multiValue = string.Equals(GetString(dataTypeElement, "type"), "GPMultiValue", StringComparison.Ordinal);
        }

        var defaultValue = GetString(element, "value")?.Trim();
        if (defaultValue is "#" or "") defaultValue = null;

        GpDomain? domain = element.TryGetProperty("domain", out var domainElement) && domainElement.ValueKind == JsonValueKind.Object
            ? ReadDomain(domainElement, resources)
            : null;

        var displayOrder = int.TryParse(GetString(element, "display_order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order)
            ? order
            : definitionIndex;
        int? parameterPosition = usage == GpParameterUsage.Derived ? null : position++;

        return new GpParameter(
            name,
            Resolve(GetString(element, "displayname"), resources) ?? name,
            CleanText(Resolve(GetString(element, "description"), resources)),
            dataType,
            leaves,
            multiValue,
            usage,
            direction,
            defaultValue,
            domain,
            GetStrings(element, "depends"),
            Resolve(GetString(element, "category"), resources),
            !string.Equals(GetString(element, "enabled"), "false", StringComparison.OrdinalIgnoreCase),
            definitionIndex,
            parameterPosition,
            displayOrder);
    }

    // GPMultiValue -> "GPMultiValue<Field>", GPComposite -> "GPComposite<GPLinearUnit|Field>",
    // GPValueTable -> "GPValueTable<GPFeatureLayer|GPLong>". Leaves collect the innermost types.
    private static string FlattenDataType(JsonElement dataType, List<string> leaves, int depth = 0)
    {
        var type = GetString(dataType, "type") ?? "Unknown";
        if (depth > 8) return type;
        if (dataType.TryGetProperty("datatype", out var inner) && inner.ValueKind == JsonValueKind.Object)
            return $"{type}<{FlattenDataType(inner, leaves, depth + 1)}>";
        if (dataType.TryGetProperty("datatypes", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            var parts = members.EnumerateArray()
                .Where(member => member.ValueKind == JsonValueKind.Object)
                .Select(member => FlattenDataType(member, leaves, depth + 1))
                .ToArray();
            return $"{type}<{string.Join('|', parts)}>";
        }

        leaves.Add(type);
        return type;
    }

    private static GpDomain ReadDomain(JsonElement element, IReadOnlyDictionary<string, string> resources, int depth = 0)
    {
        var type = GetString(element, "type") ?? "Unknown";
        IReadOnlyList<GpCodedValue>? codedValues = null;
        IReadOnlyList<string>? units = null;
        IReadOnlyList<GpDomain>? items = null;
        GpRange? range = null;

        switch (type)
        {
            case "GPCodedValueDomain":
                codedValues = ReadCodedValues(element, resources);
                break;
            case "GPUnitDomain":
                units = GetStrings(element, "items");
                break;
            case "GPCompositeDomain" when depth < 8 && element.TryGetProperty("items", out var members) && members.ValueKind == JsonValueKind.Array:
                items = members.EnumerateArray()
                    .Where(member => member.ValueKind == JsonValueKind.Object)
                    .Select(member => ReadDomain(member, resources, depth + 1))
                    .ToArray();
                break;
            case "GPRangeDomain":
                range = new GpRange(GetScalar(element, "min"), GetScalar(element, "max"));
                break;
            case "GPNumericDomain":
                range = ReadNumericRange(element);
                break;
        }

        return new GpDomain(
            type,
            codedValues,
            range,
            NullIfEmpty(GetStrings(element, "filetypes")),
            NullIfEmpty(GetStrings(element, "geometrytype")),
            NullIfEmpty(GetStrings(element, "fieldtype")),
            units is null ? null : NullIfEmpty(units),
            items);
    }

    // Two coded-value conventions exist: strings store the keyword in "value" and a $rc: label in
    // "code"; booleans (and some numerics) store "true"/"false" in "value" and the keyword in "code".
    private static GpCodedValue[] ReadCodedValues(JsonElement element, IReadOnlyDictionary<string, string> resources)
    {
        if (!element.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var values = new List<GpCodedValue>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var value = GetScalar(item, "value");
            if (value is null) continue;
            var code = GetScalar(item, "code");
            if (code is not null && code.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                values.Add(new GpCodedValue(value, Resolve(code, resources), null));
            else
                values.Add(new GpCodedValue(value, null, string.Equals(code, value, StringComparison.Ordinal) ? null : code));
        }

        return [.. values];
    }

    private static GpRange? ReadNumericRange(JsonElement element)
    {
        static (string? Value, bool Inclusive) Bound(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var bound) && bound.ValueKind == JsonValueKind.Object
                ? (GetScalar(bound, "val"), !string.Equals(GetScalar(bound, "inclusive"), "false", StringComparison.OrdinalIgnoreCase))
                : (null, true);

        var low = Bound(element, "low");
        var high = Bound(element, "high");
        return low.Value is null && high.Value is null ? null : new GpRange(low.Value, high.Value, low.Inclusive, high.Inclusive);
    }

    private static string[] ReadKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        using var document = JsonDocument.Parse(text, JsonOptions);
        return document.RootElement.ValueKind == JsonValueKind.Object ? GetStrings(document.RootElement, "set") : [];
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetScalar(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }

    private static string[] GetStrings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind == JsonValueKind.String) return [value.GetString() ?? string.Empty];
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string> values) => values.Count == 0 ? null : values;

    private static string? Truncate(string? text, int length) =>
        text is null || text.Length <= length ? text : text[..(length - 3)].TrimEnd() + "...";

    // Only the markup Esri help text uses, so literal comparisons such as "a < b" survive.
    [GeneratedRegex(@"</?(?:xdoc|p|div|span|strong|em|b|i|u|ul|ol|li|h[1-6]|a|br|code|pre|table|thead|tbody|tr|td|th|img|sup|sub|dl|dt|dd|blockquote|font|note)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>
/// Parsed <c>deprecated.list</c> (YAML list of <c>- [&lt;toolbox name&gt;/]&lt;toolname&gt;</c>, lower case).
/// The toolbox part matches the toolbox file name or display name.
/// </summary>
public sealed class GpDeprecationList
{
    private readonly HashSet<string> _entries;

    private GpDeprecationList(HashSet<string> entries) => _entries = entries;

    public static GpDeprecationList Empty { get; } = new(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public int Count => _entries.Count;

    public static GpDeprecationList Parse(string? text)
    {
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('-')) continue;
            var entry = line[1..].Trim().Trim('"', '\'');
            if (entry.Length > 0) entries.Add(entry);
        }

        return new GpDeprecationList(entries);
    }

    public static GpDeprecationList Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty;

    public bool IsDeprecated(string toolboxName, string toolboxDisplayName, string toolName) =>
        _entries.Contains(toolName) ||
        _entries.Contains($"{toolboxName}/{toolName}") ||
        _entries.Contains($"{toolboxDisplayName}/{toolName}");
}
