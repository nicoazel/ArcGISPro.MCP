using System.Xml.Linq;

namespace ArcGISProMCP.Core.Metadata;

/// <summary>
/// Reads and updates the small, common subset of ArcGIS metadata that the MCP
/// exposes. The ArcGIS metadata editor supports several metadata styles and
/// namespaces, so this deliberately matches local element names and preserves
/// all unrelated XML. It is not a general ISO metadata writer.
/// </summary>
public static class MetadataXml
{
    public static MetadataValues Read(string xml)
    {
        var document = Parse(xml);
        return Read(document);
    }

    public static string Update(string xml, MetadataPatch patch)
    {
        if (patch.IsEmpty)
            throw new ArgumentException("At least one metadata field must be supplied.", nameof(patch));

        var document = Parse(xml);
        var root = document.Root ?? throw new ArgumentException("Metadata XML has no root element.", nameof(xml));
        if (!string.Equals(root.Name.LocalName, "metadata", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Metadata XML must have a metadata root element.", nameof(xml));

        if (patch.Title is not null)
            SetFirstOrCreate(root, ["resTitle", "title"], "resTitle", patch.Title);
        if (patch.Summary is not null)
            SetFirstOrCreate(root, ["idAbs", "abstract", "summary"], "idAbs", patch.Summary);
        if (patch.Description is not null)
            SetFirstOrCreate(root, ["idPurp", "purpose", "description"], "idPurp", patch.Description);
        if (patch.Credits is not null)
            SetFirstOrCreate(root, ["idCredit", "credit", "credits"], "idCredit", patch.Credits);
        if (patch.UseLimitations is not null)
            SetFirstOrCreate(root, ["useLimit", "useLimitations", "useConstraints"], "useLimit", patch.UseLimitations);
        if (patch.Tags is not null)
            SetKeywords(root, patch.Tags);

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static MetadataValues Read(XDocument document) => new(
        FirstText(document, ["resTitle", "title"]),
        FirstText(document, ["idAbs", "abstract", "summary"]),
        FirstText(document, ["idPurp", "purpose", "description"]),
        Keywords(document),
        FirstText(document, ["idCredit", "credit", "credits"]),
        FirstText(document, ["useLimit", "useLimitations", "useConstraints"]));

    private static XDocument Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new ArgumentException("Metadata XML is required.", nameof(xml));
        try
        {
            return XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException)
        {
            throw new ArgumentException("Metadata XML is not well-formed.", nameof(xml), ex);
        }
    }

    private static string? FirstText(XContainer container, IReadOnlyList<string> names) =>
        container.Descendants()
            .FirstOrDefault(element => names.Any(name => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)))
            ?.Value.Trim() switch
        {
            { Length: > 0 } value => value,
            _ => null
        };

    private static string[] Keywords(XContainer container) =>
        container.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "searchKeys", StringComparison.OrdinalIgnoreCase))
            .SelectMany(element => element.Descendants())
            .Where(element => string.Equals(element.Name.LocalName, "keyword", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void SetFirstOrCreate(XElement root, IReadOnlyList<string> names, string createName, string value)
    {
        var element = root.Descendants().FirstOrDefault(candidate =>
            names.Any(name => string.Equals(candidate.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)));
        if (element is not null)
        {
            element.Value = value;
            return;
        }

        var container = root.Descendants().FirstOrDefault(candidate =>
            string.Equals(candidate.Name.LocalName, "dataIdInfo", StringComparison.OrdinalIgnoreCase)) ?? root;
        container.Add(new XElement(root.GetDefaultNamespace() + createName, value));
    }

    private static void SetKeywords(XElement root, IReadOnlyList<string> tags)
    {
        var searchKeys = root.Descendants().FirstOrDefault(candidate =>
            string.Equals(candidate.Name.LocalName, "searchKeys", StringComparison.OrdinalIgnoreCase));
        var existing = searchKeys?.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "keyword", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (existing.Length > 0)
        {
            var parent = existing[0].Parent!;
            parent.Elements().Where(element => string.Equals(element.Name.LocalName, "keyword", StringComparison.OrdinalIgnoreCase)).Remove();
            foreach (var tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase))
                parent.Add(new XElement(existing[0].Name.Namespace + "keyword", tag));
            return;
        }

        searchKeys ??= root.Descendants().FirstOrDefault(candidate =>
            string.Equals(candidate.Name.LocalName, "dataIdInfo", StringComparison.OrdinalIgnoreCase));
        if (searchKeys is null)
        {
            searchKeys = new XElement(root.GetDefaultNamespace() + "searchKeys");
            root.Add(searchKeys);
        }
        else if (!string.Equals(searchKeys.Name.LocalName, "searchKeys", StringComparison.OrdinalIgnoreCase))
        {
            var container = searchKeys;
            searchKeys = new XElement(root.GetDefaultNamespace() + "searchKeys");
            container.Add(searchKeys);
        }
        foreach (var tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase))
            searchKeys.Add(new XElement(root.GetDefaultNamespace() + "keyword", tag));
    }
}

public sealed record MetadataValues(
    string? Title,
    string? Summary,
    string? Description,
    IReadOnlyList<string> Tags,
    string? Credits,
    string? UseLimitations);

public sealed record MetadataPatch(
    string? Title = null,
    string? Summary = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? Credits = null,
    string? UseLimitations = null)
{
    public bool IsEmpty => Title is null && Summary is null && Description is null && Tags is null && Credits is null && UseLimitations is null;
}
