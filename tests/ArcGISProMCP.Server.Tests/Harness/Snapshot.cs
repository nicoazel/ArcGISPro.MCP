using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ArcGISProMCP.Server.Tests.Harness;

/// <summary>
/// JSON snapshot assertions. Run the tests with <c>UPDATE_SNAPSHOTS=1</c> to rewrite the files
/// under tests/ArcGISProMCP.Server.Tests/Snapshots instead of comparing.
/// </summary>
public static class Snapshot
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Directory { get; } = typeof(Snapshot).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "SnapshotDirectory").Value!;

    public static bool Updating =>
        string.Equals(Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS"), "1", StringComparison.Ordinal);

    public static void Match(string fileName, JsonNode actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var path = Path.Combine(Directory, fileName);
        var text = actual.ToJsonString(WriteOptions).ReplaceLineEndings("\n") + "\n";
        if (Updating)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, text);
            return;
        }

        Assert.True(File.Exists(path), $"Snapshot '{path}' is missing. Run the tests with UPDATE_SNAPSHOTS=1 to create it.");
        var stored = File.ReadAllText(path);
        if (!JsonNode.DeepEquals(JsonNode.Parse(stored), actual))
        {
            // Text comparison gives a readable diff in the failure message.
            Assert.Equal(stored.ReplaceLineEndings("\n"), text);
            Assert.Fail($"Snapshot '{fileName}' differs. Run the tests with UPDATE_SNAPSHOTS=1 to accept the change.");
        }
    }
}
