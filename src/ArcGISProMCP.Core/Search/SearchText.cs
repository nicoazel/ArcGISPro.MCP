using System.Collections.Immutable;
using System.Text;

namespace ArcGISProMCP.Core.Search;

/// <summary>
/// One query term. <see cref="Text"/> is what the caller typed (a word, or a phrase such as
/// "bounding box" that has a synonym entry); <see cref="Stems"/> must all appear in one field for a
/// direct match; each <see cref="Synonyms"/> entry is an alternative stem sequence scored lower.
/// </summary>
public sealed record QueryTerm(string Text, ImmutableArray<string> Stems, ImmutableArray<ImmutableArray<string>> Synonyms);

/// <summary>
/// Text normalization shared by <c>registry_search</c> and <c>gp.search</c>: punctuation is stripped,
/// identifiers are split at camel-case and separator boundaries, words are lower-cased and lightly
/// stemmed, and filler words are dropped from queries.
/// </summary>
public static class SearchText
{
    // Function words and conversational filler. They carry no GIS meaning, and before whole-word
    // matching they substring-matched almost every summary. Domain words (layer, map, set, select,
    // within, new, make, get) are never listed here.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "as", "than", "then",
        "about", "at", "by", "for", "from", "in", "into", "of", "on", "onto", "to", "with", "up", "out",
        "is", "are", "be", "been", "was", "were", "am", "do", "does", "did",
        "i", "me", "my", "we", "our", "you", "your", "it", "its", "they", "them", "their",
        "this", "that", "these", "those", "there", "here",
        "what", "which", "who", "how", "when", "where", "why",
        "all", "every", "each", "some", "any", "one", "own", "now", "just", "only", "also", "very", "too",
        "can", "could", "would", "should", "will", "please", "want", "need", "like", "so", "if", "but",
        "let", "use", "using", "sure", "right"
    };

    /// <summary>
    /// Lower-cased words of <paramref name="text"/>: split on every non-alphanumeric character and at
    /// camel-case boundaries (<c>GetCount</c> gives <c>get</c>, <c>count</c>; <c>XYTableToPoint</c> gives
    /// <c>xy</c>, <c>table</c>, <c>to</c>, <c>point</c>). A possessive <c>'s</c> is dropped.
    /// </summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character is '\'' or '\u2019')
            {
                // "layer's" -> "layer"; "don't" -> "dont".
                if (i + 1 < text.Length && text[i + 1] is 's' or 'S' && (i + 2 == text.Length || !char.IsLetterOrDigit(text[i + 2])))
                    i++;
                continue;
            }

            if (!char.IsLetterOrDigit(character))
            {
                Flush();
                continue;
            }

            if (current.Length > 0 && char.IsUpper(character))
            {
                var previous = current[^1];
                var nextIsLower = i + 1 < text.Length && char.IsLower(text[i + 1]);
                if (char.IsLower(previous) || (char.IsUpper(previous) && nextIsLower)) Flush();
            }

            current.Append(character);
        }

        Flush();
        return words;
    }

    /// <summary>Stems of <see cref="Words"/>.</summary>
    public static IReadOnlyList<string> Stems(string? text) => Words(text).Select(Stem).ToArray();

    /// <summary>True for function words and filler that never count as search terms.</summary>
    public static bool IsStopWord(string word) => StopWords.Contains(word);

    /// <summary>
    /// A deliberately light suffix stripper (plural, -ing, -ed, -ion, final -e) so that inflections of
    /// one word meet: <c>geometries</c>/<c>geometry</c>, <c>selection</c>/<c>selected</c>/<c>select</c>,
    /// <c>created</c>/<c>creation</c>/<c>create</c>. Both queries and indexed text go through it, so the
    /// stems only need to agree with each other, not be dictionary words.
    /// </summary>
    public static string Stem(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var w = word.ToLowerInvariant();
        if (w.Length <= 3 || !w.All(char.IsLetter)) return w;

        if (w.Length > 4 && (w.EndsWith("ies", StringComparison.Ordinal) || w.EndsWith("ied", StringComparison.Ordinal)))
            return w[..^3] + "y";

        if (w.EndsWith("sses", StringComparison.Ordinal) || w.EndsWith("xes", StringComparison.Ordinal) ||
            w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal))
            w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) &&
                 !w.EndsWith("us", StringComparison.Ordinal) && !w.EndsWith("is", StringComparison.Ordinal))
            w = w[..^1];

        if (w.EndsWith("ing", StringComparison.Ordinal) && HasVowel(w[..^3]) && w.Length - 3 >= 3)
            w = Undouble(w[..^3]);
        else if (w.EndsWith("ed", StringComparison.Ordinal) && HasVowel(w[..^2]) && w.Length - 2 >= 3)
            w = Undouble(w[..^2]);
        else if ((w.EndsWith("tion", StringComparison.Ordinal) || w.EndsWith("sion", StringComparison.Ordinal)) && w.Length - 3 >= 4)
            w = w[..^3];

        if (w.EndsWith('e') && w.Length >= 5) w = w[..^1];
        return w;

        static bool HasVowel(string value) => value.Any(character => character is 'a' or 'e' or 'i' or 'o' or 'u' or 'y');

        // "mapp" -> "map", "clipp" -> "clip"; l, s and z stay doubled ("fill", "process").
        static string Undouble(string value) =>
            value.Length > 3 && value[^1] == value[^2] && value[^1] is not ('l' or 's' or 'z') && !HasVowel(value[^1..])
                ? value[..^1]
                : value;
    }

    /// <summary>
    /// Splits a query into terms. Phrases with a synonym entry ("bounding box", "how many") become one
    /// term; stop words, bare numbers and single letters are dropped. A query made only of dropped words
    /// keeps its words as plain terms rather than silently matching everything.
    /// </summary>
    public static IReadOnlyList<QueryTerm> ParseQuery(string? text, IReadOnlySet<string>? extraStopWords = null)
    {
        var words = Words(text);
        if (words.Count == 0) return [];

        var terms = new List<QueryTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < words.Count)
        {
            var phrase = MatchPhrase(words, i);
            if (phrase is { } found)
            {
                var phraseWords = words.Skip(i).Take(found.Length).ToArray();
                var phraseText = string.Join(' ', phraseWords);
                if (seen.Add(phraseText))
                {
                    var stems = phraseWords.Where(word => !IsStopWord(word)).Select(Stem).ToImmutableArray();
                    terms.Add(new QueryTerm(phraseText, stems, found.Synonyms));
                }

                i += found.Length;
                continue;
            }

            var word = words[i++];
            if (IsStopWord(word) || (extraStopWords?.Contains(word) ?? false) || word.Length == 1 || word.All(char.IsDigit))
                continue;
            if (!seen.Add(word)) continue;
            var stem = Stem(word);
            terms.Add(new QueryTerm(word, [stem], SearchSynonyms.For(stem)));
        }

        if (terms.Count > 0) return terms;
        return words.Distinct(StringComparer.Ordinal)
            .Select(word => new QueryTerm(word, [Stem(word)], []))
            .ToArray();
    }

    private static (int Length, ImmutableArray<ImmutableArray<string>> Synonyms)? MatchPhrase(IReadOnlyList<string> words, int start)
    {
        for (var length = Math.Min(SearchSynonyms.LongestPhrase, words.Count - start); length >= 2; length--)
        {
            var key = string.Join(' ', words.Skip(start).Take(length).Select(Stem));
            var synonyms = SearchSynonyms.For(key);
            if (!synonyms.IsEmpty) return (length, synonyms);
        }

        return null;
    }
}
