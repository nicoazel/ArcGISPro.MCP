using ArcGISProMCP.Core.Search;

namespace ArcGISProMCP.Core.Tests;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("Does it have unsaved changes?", new[] { "does", "it", "have", "unsaved", "changes" })]
    [InlineData("management.GetCount", new[] { "management", "get", "count" })]
    [InlineData("XYTableToPoint", new[] { "xy", "table", "to", "point" })]
    [InlineData("the zoning layer's metadata", new[] { "the", "zoning", "layer", "metadata" })]
    [InlineData("see-through, 60%", new[] { "see", "through", "60" })]
    [InlineData("3D scene", new[] { "3d", "scene" })]
    public void Words_strip_punctuation_split_identifiers_and_lower_case(string text, string[] expected)
    {
        Assert.Equal(expected, SearchText.Words(text));
    }

    [Theory]
    [InlineData("geometries", "geometry")]
    [InlineData("layers", "layer")]
    [InlineData("addresses", "address")]
    [InlineData("boxes", "box")]
    [InlineData("labeling", "label")]
    [InlineData("mapping", "map")]
    [InlineData("clipped", "clip")]
    [InlineData("filled", "fill")]
    [InlineData("analysis", "analysis")]
    [InlineData("gis", "gis")]
    [InlineData("3d", "3d")]
    public void Stem_strips_common_inflections(string word, string expected)
    {
        Assert.Equal(expected, SearchText.Stem(word));
    }

    [Theory]
    [InlineData("select", "selected", "selection", "selections")]
    [InlineData("create", "created", "creating", "creation")]
    [InlineData("calculate", "calculated", "calculating", "calculation")]
    [InlineData("project", "projected", "projection", "projects")]
    [InlineData("feature", "features", "featured", "feature")]
    public void Inflections_of_one_word_share_a_stem(string a, string b, string c, string d)
    {
        var stem = SearchText.Stem(a);
        Assert.All(new[] { b, c, d }, word => Assert.Equal(stem, SearchText.Stem(word)));
    }

    [Fact]
    public void Parse_query_drops_stop_words_numbers_and_punctuation()
    {
        var terms = SearchText.ParseQuery("What project is open right now, and does it have unsaved changes?");

        Assert.Equal(["project", "open", "have", "unsaved", "changes"], terms.Select(term => term.Text));
    }

    [Fact]
    public void Parse_query_keeps_domain_words_that_look_like_filler()
    {
        var terms = SearchText.ParseQuery("set the layer within the map");

        Assert.Equal(["set", "layer", "within", "map"], terms.Select(term => term.Text));
    }

    [Fact]
    public void Parse_query_of_only_stop_words_keeps_them()
    {
        var terms = SearchText.ParseQuery("the");

        Assert.Equal(["the"], terms.Select(term => term.Text));
    }

    [Fact]
    public void Parse_query_honours_extra_stop_words()
    {
        var terms = SearchText.ParseQuery("buffer tool", new HashSet<string>(StringComparer.Ordinal) { "tool" });

        Assert.Equal(["buffer"], terms.Select(term => term.Text));
    }

    [Fact]
    public void Synonym_phrases_become_one_term()
    {
        var terms = SearchText.ParseQuery("how many rows are in the bounding box");

        Assert.Equal(["how many", "rows", "bounding box"], terms.Select(term => term.Text));
        Assert.Contains(terms[0].Synonyms, synonym => synonym.SequenceEqual(["count"]));
        Assert.Contains(terms[2].Synonyms, synonym => synonym.SequenceEqual(["extent"]));
    }

    [Theory]
    [InlineData("colour", "symbology")]
    [InlineData("highlight", "select")]
    [InlineData("picture", "captur")]
    [InlineData("reproject", "project")]
    [InlineData("overlap", "intersect")]
    [InlineData("combine", "merg")]
    [InlineData("fix", "repair")]
    [InlineData("acres", "area")]
    [InlineData("column", "field")]
    public void Everyday_words_map_to_product_vocabulary(string word, string target)
    {
        var term = Assert.Single(SearchText.ParseQuery(word));

        Assert.Contains(term.Synonyms, synonym => synonym.SequenceEqual([target]));
    }

    [Fact]
    public void Synonyms_never_include_the_word_itself()
    {
        var term = Assert.Single(SearchText.ParseQuery("row"));

        Assert.DoesNotContain(term.Synonyms, synonym => synonym.SequenceEqual([term.Stems[0]]));
    }
}

public sealed class SearchIndexTests
{
    private static readonly (string Id, string Title, string Summary)[] Items =
    [
        ("layer.transparency", "Set layer transparency", "Sets the transparency of one layer."),
        ("zone.buffer", "Buffer", "Creates zones around features."),
        ("layer.list", "List layers", "Lists every layer and its drawing order."),
        ("table.count", "Get Count", "Returns the number of rows."),
        ("data.geodatabase", "Create file geodatabase", "Creates a geodatabase folder.")
    ];

    private static SearchIndex<(string Id, string Title, string Summary)> Index() =>
        new(Items, item => [new SearchField(10, item.Title), new SearchField(3, item.Summary)]);

    private static string[] Rank(string query)
    {
        var index = Index();
        var terms = SearchText.ParseQuery(query);
        return Enumerable.Range(0, index.Items.Count)
            .Select(i => (index.Items[i].Id, index.Score(i, terms).Score))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .Select(hit => hit.Id)
            .ToArray();
    }

    [Fact]
    public void Short_words_match_whole_words_only()
    {
        // "one" used to substring-match "zones"; "set" used to match "sets" only by accident.
        var index = Index();
        var match = index.Score(1, SearchText.ParseQuery("zone"));

        Assert.True(match.Score > 0);
        Assert.Empty(Rank("on"));
        Assert.Equal(["layer.transparency"], Rank("sets"));
    }

    [Fact]
    public void Prefix_and_compound_matches_score_below_whole_words()
    {
        var index = Index();

        var whole = index.Score(4, SearchText.ParseQuery("geodatabase")).Score;
        var prefix = index.Score(4, SearchText.ParseQuery("geodata")).Score;
        var compound = index.Score(4, SearchText.ParseQuery("database")).Score;

        Assert.True(whole > prefix);
        Assert.True(prefix > compound);
        Assert.True(compound > 0);
    }

    [Fact]
    public void Synonym_matches_score_below_direct_matches()
    {
        var index = Index();

        var direct = index.Score(3, SearchText.ParseQuery("count")).Score;
        var synonym = index.Score(3, SearchText.ParseQuery("how many")).Score;

        Assert.True(synonym > 0);
        Assert.True(direct > synonym);
        Assert.Equal(["how many"], index.Score(3, SearchText.ParseQuery("how many")).MatchedTerms);
    }

    [Fact]
    public void Common_words_weigh_less_than_rare_words()
    {
        var index = Index();

        Assert.True(index.Rarity("layer") < index.Rarity("buffer"));
        Assert.InRange(index.Rarity("layer"), SearchIndex<object>.CommonWordFloor, 1);
        Assert.Equal(1, index.Rarity("unseen"), 3);
    }

    [Fact]
    public void Matched_terms_report_what_the_caller_typed()
    {
        var match = Index().Score(0, SearchText.ParseQuery("Set the layers' transparency!"));

        Assert.Equal(["Set".ToLowerInvariant(), "layers", "transparency"], match.MatchedTerms);
        Assert.True(match.AllTermsMatched);
    }
}
