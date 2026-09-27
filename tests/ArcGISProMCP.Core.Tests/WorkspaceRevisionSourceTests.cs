namespace ArcGISProMCP.Core.Tests;

public sealed class WorkspaceRevisionSourceTests
{
    [Fact]
    public void Mutations_publish_a_revision_after_deferred_host_events_settle()
    {
        var provider = ReadSource("ArcGIS", "ProWorkspaceStateProvider.cs");
        var operation = ReadSource("Operations", "ProOperationBase.cs");

        Assert.Contains("GetSettledSnapshotAsync", provider, StringComparison.Ordinal);
        Assert.Contains("quietSamples >= 2", provider, StringComparison.Ordinal);
        Assert.Contains("attempt < 20", provider, StringComparison.Ordinal);
        Assert.Contains("GetSettledSnapshotAsync(CancellationToken.None)", operation, StringComparison.Ordinal);
    }

    private static string ReadSource(string directoryName, string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "ArcGISProMCP.AddIn",
                directoryName,
                fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test output tree.");
    }
}
