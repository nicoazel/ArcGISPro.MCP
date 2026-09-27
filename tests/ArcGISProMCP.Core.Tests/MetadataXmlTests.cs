using ArcGISProMCP.Core.Metadata;

namespace ArcGISProMCP.Core.Tests;

public sealed class MetadataXmlTests
{
    private const string Xml = """
        <metadata xmlns="http://www.esri.com/metadata/esri-iso-19139-standards">
          <dataIdInfo>
            <idCitation><resTitle>Parcels</resTitle></idCitation>
            <idAbs>Tax parcel polygons.</idAbs>
            <idPurp>Planning reference.</idPurp>
            <idCredit>County GIS</idCredit>
            <resConst><Consts><useLimit>Internal use.</useLimit></Consts></resConst>
            <searchKeys><keyword>parcel</keyword><keyword>planning</keyword></searchKeys>
          </dataIdInfo>
          <distInfo><customElement>keep me</customElement></distInfo>
        </metadata>
        """;

    [Fact]
    public void Read_is_namespace_insensitive_and_returns_supported_fields()
    {
        var values = MetadataXml.Read(Xml);

        Assert.Equal("Parcels", values.Title);
        Assert.Equal("Tax parcel polygons.", values.Summary);
        Assert.Equal("Planning reference.", values.Description);
        Assert.Equal(["parcel", "planning"], values.Tags);
        Assert.Equal("County GIS", values.Credits);
        Assert.Equal("Internal use.", values.UseLimitations);
    }

    [Fact]
    public void Update_preserves_unrelated_xml_and_replaces_existing_keywords()
    {
        var updated = MetadataXml.Update(Xml, new MetadataPatch(
            Title: "Buildings",
            Summary: "Building footprints.",
            Tags: ["building", "footprint"],
            UseLimitations: "Public."));
        var values = MetadataXml.Read(updated);

        Assert.Equal("Buildings", values.Title);
        Assert.Equal("Building footprints.", values.Summary);
        Assert.Equal(["building", "footprint"], values.Tags);
        Assert.Equal("Public.", values.UseLimitations);
        Assert.Contains("customElement", updated, StringComparison.Ordinal);
        Assert.Contains("keep me", updated, StringComparison.Ordinal);
        Assert.DoesNotContain(">parcel<", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_do_not_replace_keywords_outside_search_keys()
    {
        const string xml = "<metadata><dataIdInfo><searchKeys><keyword>old</keyword></searchKeys><themeKeys><keyword>protected-theme</keyword></themeKeys></dataIdInfo></metadata>";

        var updated = MetadataXml.Update(xml, new MetadataPatch(Tags: ["new"]));
        var values = MetadataXml.Read(updated);

        Assert.Equal(["new"], values.Tags);
        Assert.Contains("protected-theme", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_rejects_empty_patch()
    {
        Assert.Throws<ArgumentException>(() => MetadataXml.Update(Xml, new MetadataPatch()));
    }

    [Fact]
    public void Update_rejects_non_metadata_root()
    {
        Assert.Throws<ArgumentException>(() => MetadataXml.Update("<root />", new MetadataPatch(Title: "x")));
    }
}
