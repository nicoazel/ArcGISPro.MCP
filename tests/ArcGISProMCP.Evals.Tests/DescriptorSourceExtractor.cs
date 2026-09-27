using System.Collections.Immutable;
using System.Text;
using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>
/// Interim: reads the search-relevant arguments of every <c>OperationDescriptor.Create(...)</c> call in the
/// add-in's operation sources. The add-in references Esri assemblies that cannot load in tests, so until
/// the Phase 4 operations seam this is the only way to evaluate search against the real descriptor text.
/// Handles the forms the sources use: string literals (optionally concatenated with <c>+</c>),
/// collection expressions of string literals, enum members and booleans. Anything else is skipped.
/// </summary>
internal static class DescriptorSourceExtractor
{
    private const string Marker = "OperationDescriptor.Create(";

    public static ImmutableArray<DescriptorFixtureEntry> Extract(string operationsDirectory, string repositoryRoot)
    {
        var entries = ImmutableArray.CreateBuilder<DescriptorFixtureEntry>();
        foreach (var file in Directory.EnumerateFiles(operationsDirectory, "*.cs").Order(StringComparer.Ordinal))
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            var index = 0;
            while ((index = source.IndexOf(Marker, index, StringComparison.Ordinal)) >= 0)
            {
                var start = index + Marker.Length;
                var arguments = SplitArguments(source, start, out index);
                entries.Add(ToEntry(arguments, relative));
            }
        }

        return entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static DescriptorFixtureEntry ToEntry(List<string> arguments, string source)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            var colon = NamedArgumentColon(argument);
            if (colon > 0) named[argument[..colon].Trim()] = argument[(colon + 1)..].Trim();
            else positional.Add(argument);
        }

        if (positional.Count < 3)
            throw new InvalidDataException($"{source}: expected id, title and summary as positional arguments.");

        var id = StringValue(positional[0]);
        var risk = named.TryGetValue("risk", out var riskText) ? riskText.Split('.')[^1] : "ReadOnly";
        return new DescriptorFixtureEntry(
            id,
            StringValue(positional[1]),
            StringValue(positional[2]),
            ListValue(named.GetValueOrDefault("tags")),
            ListValue(named.GetValueOrDefault("aliases")),
            ListValue(named.GetValueOrDefault("capabilities")),
            risk,
            id.Split('.')[0],
            named.GetValueOrDefault("requiresConfirmation") == "true",
            named.GetValueOrDefault("executesUserCode") == "true",
            source);
    }

    // "name: value" where name is a C# identifier; a string literal or expression never starts that way.
    private static int NamedArgumentColon(string argument)
    {
        var i = 0;
        while (i < argument.Length && (char.IsLetterOrDigit(argument[i]) || argument[i] == '_')) i++;
        if (i == 0 || !char.IsLetter(argument[0])) return -1;
        var j = i;
        while (j < argument.Length && char.IsWhiteSpace(argument[j])) j++;
        return j < argument.Length && argument[j] == ':' ? j : -1;
    }

    private static ImmutableArray<string> ListValue(string? expression)
    {
        if (expression is null) return [];
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            throw new InvalidDataException($"Unsupported list expression: {trimmed}");
        return SplitArguments(trimmed, 1, out _).Where(item => item.Length > 0).Select(StringValue).ToImmutableArray();
    }

    /// <summary>Evaluates one or more string literals joined with <c>+</c>.</summary>
    private static string StringValue(string expression)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i < expression.Length)
        {
            var c = expression[i];
            if (char.IsWhiteSpace(c) || c == '+')
            {
                i++;
                continue;
            }

            if (c != '"') throw new InvalidDataException($"Unsupported string expression: {expression}");
            i = ReadRegularString(expression, i, text);
        }

        return text.ToString();
    }

    private static int ReadRegularString(string source, int quote, StringBuilder? into)
    {
        var i = quote + 1;
        while (i < source.Length && source[i] != '"')
        {
            if (source[i] == '\\' && i + 1 < source.Length)
            {
                into?.Append(source[i + 1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var other => other });
                i += 2;
                continue;
            }

            into?.Append(source[i]);
            i++;
        }

        return i + 1;
    }

    /// <summary>Splits the top-level comma-separated arguments after an opening bracket at <paramref name="start"/>.</summary>
    private static List<string> SplitArguments(string source, int start, out int end)
    {
        var arguments = new List<string>();
        var depth = 0;
        var current = start;
        var i = start;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '"')
            {
                if (i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
                {
                    var close = source.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = close + 3;
                }
                else if (i > 0 && source[i - 1] == '@')
                {
                    i++;
                    while (i < source.Length && !(source[i] == '"' && (i + 1 >= source.Length || source[i + 1] != '"')))
                        i += source[i] == '"' ? 2 : 1;
                    i++;
                }
                else
                {
                    i = ReadRegularString(source, i, null);
                }

                continue;
            }

            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(source[current..i].Trim());
                    end = i + 1;
                    return arguments;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(source[current..i].Trim());
                current = i + 1;
            }

            i++;
        }

        throw new InvalidDataException("Unbalanced descriptor expression.");
    }
}
