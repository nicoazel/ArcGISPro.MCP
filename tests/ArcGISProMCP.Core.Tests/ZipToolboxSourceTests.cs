using System.Buffers.Binary;
using System.IO.Compression;
using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class ZipToolboxSourceTests
{
    private const int Oversize = (int)ZipToolboxSource.MaximumEntryBytes + 1024;

    [Fact]
    public void An_entry_larger_than_the_limit_is_rejected()
    {
        var atbx = CreateZip(archive => AddZeros(archive, "Big.tool/tool.content", Oversize));

        Assert.Throws<InvalidDataException>(() => new ZipToolboxSource(atbx));
    }

    [Fact]
    public void An_entry_that_declares_a_small_size_is_still_bounded_by_the_bytes_it_decompresses()
    {
        var atbx = CreateZip(archive => AddZeros(archive, "Big.tool/tool.content", Oversize));
        SpoofUncompressedSizes(atbx, declared: 100);
        using (var archive = ZipFile.OpenRead(atbx))
            Assert.Equal(100, Assert.Single(archive.Entries).Length);

        // Either the read is refused or it stops at the declared size (System.IO.Compression's own
        // behaviour); what must never happen is decompressing the real 16 MiB+ body.
        try
        {
            var text = new ZipToolboxSource(atbx).ReadText("Big.tool/tool.content");
            Assert.NotNull(text);
            Assert.True(text.Length <= 100, $"Read {text.Length} characters from an entry declaring 100 bytes.");
        }
        catch (InvalidDataException)
        {
        }
    }

    [Fact]
    public void The_total_metadata_read_from_one_archive_is_capped()
    {
        var perEntry = (int)ZipToolboxSource.MaximumEntryBytes - 1024;
        var count = (int)(ZipToolboxSource.MaximumTotalBytes / perEntry) + 1;
        var atbx = CreateZip(archive =>
        {
            for (var i = 0; i < count; i++) AddZeros(archive, $"Tool{i}.tool/tool.content", perEntry);
        });

        Assert.Throws<InvalidDataException>(() => new ZipToolboxSource(atbx));
    }

    [Fact]
    public void Archives_with_too_many_entries_are_rejected()
    {
        var atbx = CreateZip(archive =>
        {
            for (var i = 0; i <= ZipToolboxSource.MaximumEntries; i++) archive.CreateEntry($"e{i}.txt");
        });

        Assert.Throws<InvalidDataException>(() => new ZipToolboxSource(atbx));
    }

    [Fact]
    public void Script_entries_are_recorded_as_present_but_never_read()
    {
        var atbx = CreateZip(archive =>
        {
            AddZeros(archive, "Script.tool/tool.script.execute.py", Oversize);
            AddText(archive, "toolbox.content", "{}");
        });

        var source = new ZipToolboxSource(atbx);

        Assert.True(source.FileExists("Script.tool/tool.script.execute.py"));
        Assert.True(source.FileExists("Script.tool\\tool.script.execute.py"));
        Assert.Null(source.ReadText("Script.tool/tool.script.execute.py"));
        Assert.Equal("{}", source.ReadText("toolbox.content"));
    }

    private static string CreateZip(Action<ZipArchive> fill)
    {
        var path = Path.Combine(GeoprocessingFixtures.CreateTempDirectory(), "crafted.atbx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) fill(archive);
        return path;
    }

    private static void AddZeros(ZipArchive archive, string name, int length)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        var chunk = new byte[81920];
        for (var written = 0; written < length; written += chunk.Length)
            stream.Write(chunk, 0, Math.Min(chunk.Length, length - written));
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    /// <summary>Rewrites every local and central header's uncompressed size (no data descriptors, no zip64).</summary>
    private static void SpoofUncompressedSizes(string path, uint declared)
    {
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (signature == 0x04034b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), declared);
            else if (signature == 0x02014b50) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), declared);
        }
        File.WriteAllBytes(path, bytes);
    }
}
