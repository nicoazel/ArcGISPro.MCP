using System.Collections.Immutable;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Registry;

public sealed class OperationRegistry : IOperationRegistry
{
    private readonly Dictionary<string, IOperation> _operations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyCollection<OperationDescriptor> Descriptors
    {
        get
        {
            lock (_gate)
            {
                return _operations.Values
                    .Select(operation => operation.Descriptor)
                    .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public void Register(IOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Validate(operation.Descriptor);

        lock (_gate)
        {
            if (!_operations.TryAdd(operation.Descriptor.Id, operation))
            {
                throw new InvalidOperationException($"Operation '{operation.Descriptor.Id}' is already registered.");
            }
        }
    }

    public bool TryGet(string id, out IOperation operation)
    {
        lock (_gate)
        {
            return _operations.TryGetValue(id, out operation!);
        }
    }

    public IReadOnlyList<SearchHit> Search(OperationQuery query)
    {
        var terms = Tokenize(query.Text);
        IEnumerable<OperationDescriptor> candidates = Descriptors;

        if (!string.IsNullOrWhiteSpace(query.Domain))
        {
            var domain = query.Domain.Trim();
            candidates = candidates.Where(descriptor =>
                descriptor.Id.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase) ||
                descriptor.Tags.Contains(domain));
        }

        if (query.Capabilities is { Count: > 0 })
        {
            candidates = candidates.Where(descriptor =>
                query.Capabilities.All(capability => descriptor.RequiredCapabilities.Contains(capability)));
        }

        if (query.MaximumRisk is { } risk)
        {
            candidates = candidates.Where(descriptor => descriptor.Risk <= risk);
        }

        return candidates
            .Select(descriptor => Score(descriptor, terms))
            .Where(hit => terms.Length == 0 || hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Descriptor.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(query.Limit, 1, 100))
            .ToArray();
    }

    private static SearchHit Score(OperationDescriptor descriptor, string[] terms)
    {
        if (terms.Length == 0)
        {
            return new SearchHit(descriptor, 1, []);
        }

        var matches = ImmutableArray.CreateBuilder<string>();
        double score = 0;
        foreach (var term in terms)
        {
            var termScore = 0d;
            if (descriptor.Id.Contains(term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 12);
            if (descriptor.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 9);
            if (descriptor.Aliases.Any(alias => alias.Contains(term, StringComparison.OrdinalIgnoreCase))) termScore = Math.Max(termScore, 8);
            if (descriptor.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase))) termScore = Math.Max(termScore, 6);
            if (descriptor.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 4);
            if (termScore <= 0) continue;
            matches.Add(term);
            score += termScore;
        }

        if (matches.Count == terms.Length) score += 5;
        return new SearchHit(descriptor, score, matches.ToImmutable());
    }

    private static string[] Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([' ', '\t', '\r', '\n', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static void Validate(OperationDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) ||
            descriptor.Id.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException("Operation ids must use lower-case dot-separated names.", nameof(descriptor));
        }

        if (!Version.TryParse(descriptor.Version, out _))
        {
            throw new ArgumentException($"Operation '{descriptor.Id}' has invalid version '{descriptor.Version}'.", nameof(descriptor));
        }

        if (descriptor.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !descriptor.InputSchema.TryGetProperty("type", out var type) ||
            type.GetString() != "object")
        {
            throw new ArgumentException($"Operation '{descriptor.Id}' must declare an object input schema.", nameof(descriptor));
        }

        if (descriptor.Risk is OperationRisk.Destructive or OperationRisk.ExternalSideEffect && !descriptor.RequiresConfirmation)
        {
            throw new ArgumentException($"Risky operation '{descriptor.Id}' must require confirmation.", nameof(descriptor));
        }
    }
}
