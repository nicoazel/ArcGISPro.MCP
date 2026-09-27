using System.Collections.Immutable;

namespace ArcGISProMCP.Core.Search;

/// <summary>A weighted piece of an item's text (id, title, keywords, summary...).</summary>
public sealed record SearchField(double Weight, IEnumerable<string?> Texts)
{
    public SearchField(double weight, params string?[] texts) : this(weight, (IEnumerable<string?>)texts)
    {
    }
}

/// <summary>How well one item matched a query.</summary>
public sealed record SearchMatch(double Score, ImmutableArray<string> MatchedTerms, int TermCount)
{
    public bool AllTermsMatched => TermCount > 0 && MatchedTerms.Length == TermCount;
}

/// <summary>
/// A small in-memory ranking index over <typeparamref name="T"/>. Each item is a set of weighted
/// fields of stemmed words (<see cref="SearchText"/>). A query term scores the best field it appears
/// in, as a whole word (full weight), a word prefix (<see cref="PrefixFactor"/>, stems of 4+ letters)
/// or inside a compound word (<see cref="SubstringFactor"/>, stems of 5+ letters); a synonym match
/// scores <see cref="SynonymFactor"/> of that. Each term is scaled by how rare its word is in the index
/// (a softened inverse document frequency), so "feature" or "layer" count for less than "thiessen".
/// </summary>
public sealed class SearchIndex<T>
{
    /// <summary>Weight of a match on a word that starts with the query stem.</summary>
    public const double PrefixFactor = 0.75;

    /// <summary>Weight of a match inside a longer word ("database" in "geodatabase").</summary>
    public const double SubstringFactor = 0.4;

    /// <summary>Weight of a match through <see cref="SearchSynonyms"/> relative to a direct match.</summary>
    public const double SynonymFactor = 0.6;

    /// <summary>Weight of a word that occurs in every item; a word unique to one item weighs 1.</summary>
    public const double CommonWordFloor = 0.25;

    private readonly Document[] _documents;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);

    public SearchIndex(IEnumerable<T> items, Func<T, IEnumerable<SearchField>> fields)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fields);
        Items = items.ToArray();
        _documents = Items.Select(item => new Document(fields(item).Select(Field.Create).ToArray())).ToArray();
        foreach (var document in _documents)
        foreach (var stem in document.Fields.SelectMany(field => field.Stems).Distinct(StringComparer.Ordinal))
            _documentFrequency[stem] = _documentFrequency.GetValueOrDefault(stem) + 1;
    }

    public IReadOnlyList<T> Items { get; }

    /// <summary>Scores item <paramref name="index"/> (a position in <see cref="Items"/>).</summary>
    public SearchMatch Score(int index, IReadOnlyList<QueryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var document = _documents[index];
        double score = 0;
        var matched = ImmutableArray.CreateBuilder<string>();
        foreach (var term in terms)
        {
            var termScore = MatchSequence(document, term.Stems);
            foreach (var synonym in term.Synonyms)
                termScore = Math.Max(termScore, SynonymFactor * MatchSequence(document, synonym));
            if (termScore <= 0) continue;
            matched.Add(term.Text);
            score += termScore;
        }

        return new SearchMatch(score, matched.ToImmutable(), terms.Count);
    }

    /// <summary>Rarity weight of a stem in [<see cref="CommonWordFloor"/>, 1].</summary>
    public double Rarity(string stem)
    {
        var total = _documents.Length;
        if (total <= 1) return 1;
        var frequency = Math.Min(_documentFrequency.GetValueOrDefault(stem), total);
        var idf = Math.Log((total + 1d) / (frequency + 1d)) / Math.Log(total + 1d);
        return CommonWordFloor + ((1 - CommonWordFloor) * Math.Clamp(idf, 0, 1));
    }

    // All stems must match inside one field: the field's weight times the weakest stem's match quality,
    // times the mean rarity of the query stems. A stem that occurs nowhere as a word of its own (a prefix
    // or compound hit such as "geodata") takes the rarity of the indexed word it matched instead.
    private double MatchSequence(Document document, ImmutableArray<string> stems)
    {
        if (stems.IsEmpty) return 0;
        double best = 0;
        foreach (var field in document.Fields)
        {
            var quality = 1d;
            var rarity = 0d;
            foreach (var stem in stems)
            {
                var (stemQuality, word) = field.Match(stem);
                quality = Math.Min(quality, stemQuality);
                if (quality <= 0) break;
                rarity += Rarity(_documentFrequency.ContainsKey(stem) ? stem : word!);
            }

            if (quality > 0) best = Math.Max(best, field.Weight * quality * rarity / stems.Length);
        }

        return best;
    }

    private sealed record Document(Field[] Fields);

    private sealed class Field
    {
        private Field(double weight, string[] stems)
        {
            Weight = weight;
            Stems = stems;
            _set = new HashSet<string>(stems, StringComparer.Ordinal);
        }

        private readonly HashSet<string> _set;

        public double Weight { get; }

        public string[] Stems { get; }

        public static Field Create(SearchField field) =>
            new(field.Weight, field.Texts.SelectMany(SearchText.Stems).Distinct(StringComparer.Ordinal).ToArray());

        /// <summary>Match quality of <paramref name="stem"/> in this field and the indexed word it matched.</summary>
        public (double Quality, string? Word) Match(string stem)
        {
            if (_set.Contains(stem)) return (1, stem);
            (double Quality, string? Word) best = (0, null);
            if (stem.Length < 4) return best;
            foreach (var word in Stems)
            {
                if (word.Length <= stem.Length) continue;
                if (word.StartsWith(stem, StringComparison.Ordinal)) return (PrefixFactor, word);
                if (stem.Length >= 5 && best.Word is null && word.Contains(stem, StringComparison.Ordinal)) best = (SubstringFactor, word);
            }

            return best;
        }
    }
}
