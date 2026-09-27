using System.Collections.Immutable;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Search;

namespace ArcGISProMCP.Core.Registry;

public sealed class OperationRegistry : IOperationRegistry
{
    private readonly Dictionary<string, IOperation> _operations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private SearchIndex<OperationDescriptor>? _searchIndex;

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

            _searchIndex = null;
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
        var terms = SearchText.ParseQuery(query.Text);
        var index = GetSearchIndex();

        return Enumerable.Range(0, index.Items.Count)
            .Where(i => Matches(index.Items[i], query))
            .Select(i => Score(index, i, terms))
            .Where(hit => terms.Count == 0 || hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Descriptor.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(query.Limit, 1, 100))
            .ToArray();
    }

    private static bool Matches(OperationDescriptor descriptor, OperationQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Domain))
        {
            var domain = query.Domain.Trim();
            if (!descriptor.Id.StartsWith(domain + ".", StringComparison.OrdinalIgnoreCase) && !descriptor.Tags.Contains(domain))
                return false;
        }

        if (query.Capabilities is { Count: > 0 } &&
            !query.Capabilities.All(capability => descriptor.RequiredCapabilities.Contains(capability)))
            return false;

        return query.MaximumRisk is not { } risk || descriptor.Risk <= risk;
    }

    private static SearchHit Score(SearchIndex<OperationDescriptor> index, int position, IReadOnlyList<QueryTerm> terms)
    {
        var descriptor = index.Items[position];
        if (terms.Count == 0)
        {
            return new SearchHit(descriptor, 1, []);
        }

        var match = index.Score(position, terms);
        var score = match.Score;
        if (match.AllTermsMatched) score += AllTermsBonus;
        return new SearchHit(descriptor, score, match.MatchedTerms);
    }

    // Field weights: the id and title name the operation; aliases and tags are curated search words;
    // the summary is prose and matches incidentally.
    private const double IdWeight = 12;
    private const double TitleWeight = 9;
    private const double AliasWeight = 8;
    private const double TagWeight = 6;
    private const double SummaryWeight = 4;
    private const double AllTermsBonus = 5;

    private SearchIndex<OperationDescriptor> GetSearchIndex()
    {
        lock (_gate)
        {
            return _searchIndex ??= new SearchIndex<OperationDescriptor>(
                _operations.Values.Select(operation => operation.Descriptor).OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal),
                descriptor =>
                [
                    new SearchField(IdWeight, descriptor.Id),
                    new SearchField(TitleWeight, descriptor.Title),
                    new SearchField(AliasWeight, descriptor.Aliases),
                    new SearchField(TagWeight, descriptor.Tags),
                    new SearchField(SummaryWeight, descriptor.Summary)
                ]);
        }
    }

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
