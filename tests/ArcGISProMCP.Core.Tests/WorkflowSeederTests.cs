using System.Text.Json;
using ArcGISProMCP.Core;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class WorkflowSeederTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Embedded_resource_names_match_repository_workflow_files()
    {
        var expected = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "workflows"), "*.workflow.json")
            .Select(path => WorkflowSeeder.ResourcePrefix + Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, WorkflowSeeder.BundledResourceNames);
    }

    [Fact]
    public async Task SeedAsync_writes_every_bundled_workflow_into_an_empty_library()
    {
        using var scope = new LibraryScope();

        var report = await WorkflowSeeder.SeedAsync(scope.Library, TestContext.Current.CancellationToken);

        var count = WorkflowSeeder.BundledResourceNames.Count;
        Assert.Equal(count, report.Items.Length);
        Assert.All(report.Items, item => Assert.Equal(WorkflowSeedOutcome.Seeded, item.Outcome));
        Assert.Equal(count, report.SeededCount);
        Assert.Equal(count, Directory.EnumerateFiles(scope.Root, "*.workflow.json").Count());
        var listed = await scope.Library.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(count, listed.Count);
        Assert.All(listed, workflow => Assert.False(string.IsNullOrEmpty(workflow.ContentHash)));
    }

    [Fact]
    public async Task SeedAsync_is_a_no_op_on_second_run()
    {
        using var scope = new LibraryScope();
        await WorkflowSeeder.SeedAsync(scope.Library, TestContext.Current.CancellationToken);
        var before = Snapshot(scope.Root);

        var report = await WorkflowSeeder.SeedAsync(scope.Library, TestContext.Current.CancellationToken);

        Assert.All(report.Items, item => Assert.Equal(WorkflowSeedOutcome.AlreadyPresent, item.Outcome));
        Assert.Equal(0, report.SeededCount);
        Assert.Empty(report.Skipped);
        Assert.Equal(before, Snapshot(scope.Root));
    }

    [Fact]
    public async Task SeedAsync_never_overwrites_a_different_installed_version_and_reports_it()
    {
        using var scope = new LibraryScope();
        var bundled = await ReadBundledAsync(WorkflowSeeder.BundledResourceNames[0]);
        var userEdited = bundled with { Title = bundled.Title + " (user edit)", ContentHash = null };
        await scope.Library.SaveAsync(userEdited, TestContext.Current.CancellationToken);
        var userFile = Assert.Single(Directory.EnumerateFiles(scope.Root, "*.workflow.json"));
        var userBytes = await File.ReadAllBytesAsync(userFile, TestContext.Current.CancellationToken);

        var report = await WorkflowSeeder.SeedAsync(scope.Library, TestContext.Current.CancellationToken);

        var conflict = Assert.Single(report.Skipped);
        Assert.Equal(WorkflowSeedOutcome.Conflict, conflict.Outcome);
        Assert.Equal(bundled.Id, conflict.WorkflowId);
        Assert.Equal(bundled.Version, conflict.Version);
        Assert.False(string.IsNullOrWhiteSpace(conflict.Message));
        Assert.Equal(WorkflowSeeder.BundledResourceNames.Count - 1, report.SeededCount);
        Assert.Equal(userBytes, await File.ReadAllBytesAsync(userFile, TestContext.Current.CancellationToken));
        var installed = await scope.Library.GetAsync(bundled.Id, bundled.Version, TestContext.Current.CancellationToken);
        Assert.Equal(userEdited.Title, installed!.Title);
    }

    [Fact]
    public async Task SeedAsync_reports_conflict_for_same_id_and_version_under_a_different_file_name()
    {
        using var scope = new LibraryScope();
        var bundled = await ReadBundledAsync(WorkflowSeeder.BundledResourceNames[0]);
        var userEdited = bundled with { Title = bundled.Title + " (user edit)", ContentHash = null };
        var userFile = Path.Combine(scope.Root, "my-custom-name.workflow.json");
        await File.WriteAllTextAsync(userFile, JsonSerializer.Serialize(userEdited, WebOptions), TestContext.Current.CancellationToken);
        var userBytes = await File.ReadAllBytesAsync(userFile, TestContext.Current.CancellationToken);

        var report = await WorkflowSeeder.SeedAsync(scope.Library, TestContext.Current.CancellationToken);

        var conflict = Assert.Single(report.Skipped);
        Assert.Equal(WorkflowSeedOutcome.Conflict, conflict.Outcome);
        Assert.Equal(bundled.Id, conflict.WorkflowId);
        Assert.Equal(bundled.Version, conflict.Version);
        Assert.Equal(WorkflowSeeder.BundledResourceNames.Count - 1, report.SeededCount);
        Assert.Equal(userBytes, await File.ReadAllBytesAsync(userFile, TestContext.Current.CancellationToken));
        var installed = (await scope.Library.ListAsync(TestContext.Current.CancellationToken))
            .Where(workflow => workflow.Id == bundled.Id && workflow.Version == bundled.Version);
        Assert.Equal(userEdited.Title, Assert.Single(installed).Title);
    }

    [Fact]
    public async Task SeedAsync_treats_identical_content_under_a_different_file_name_as_present()
    {
        using var scope = new LibraryScope();
        var bundled = await ReadBundledAsync(WorkflowSeeder.BundledResourceNames[0]);
        var userFile = Path.Combine(scope.Root, "my-copy.workflow.json");
        await File.WriteAllTextAsync(userFile, JsonSerializer.Serialize(bundled with { ContentHash = null }, WebOptions), TestContext.Current.CancellationToken);

        var report = await WorkflowSeeder.SeedAsync(scope.Library, TestContext.Current.CancellationToken);

        Assert.Equal(WorkflowSeedOutcome.AlreadyPresent, report.Items[0].Outcome);
        Assert.Empty(report.Skipped);
        Assert.Equal(WorkflowSeeder.BundledResourceNames.Count, Directory.EnumerateFiles(scope.Root, "*.workflow.json").Count());
    }

    [Fact]
    public async Task SeedAsync_reports_failures_instead_of_throwing_when_operations_are_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var library = new FileWorkflowLibrary(root, new OperationRegistry());

            var report = await WorkflowSeeder.SeedAsync(library, TestContext.Current.CancellationToken);

            Assert.All(report.Items, item => Assert.Equal(WorkflowSeedOutcome.Failed, item.Outcome));
            Assert.Empty(Directory.EnumerateFiles(root, "*.workflow.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<WorkflowDefinition> ReadBundledAsync(string resourceName)
    {
        await using var stream = typeof(WorkflowSeeder).Assembly.GetManifestResourceStream(resourceName)!;
        return (await JsonSerializer.DeserializeAsync<WorkflowDefinition>(
            stream,
            WebOptions,
            TestContext.Current.CancellationToken))!;
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*.workflow.json")
            .ToDictionary(path => Path.GetFileName(path), path => File.ReadAllText(path) + "|" + File.GetLastWriteTimeUtc(path).Ticks, StringComparer.Ordinal);

    private static OperationRegistry RegistryForBundledWorkflows()
    {
        var registry = new OperationRegistry();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in WorkflowSeeder.BundledResourceNames)
        {
            using var stream = typeof(WorkflowSeeder).Assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
                operationIds.Add(step.GetProperty("operation").GetString()!);
        }

        foreach (var id in operationIds) registry.Register(new StubOperation(id));
        return registry;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ArcGISPro.MCP.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing ArcGISPro.MCP.slnx was not found.");
    }

    private sealed class LibraryScope : IDisposable
    {
        public LibraryScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-tests", Guid.NewGuid().ToString("N"));
            Library = new FileWorkflowLibrary(Root, RegistryForBundledWorkflows());
        }

        public string Root { get; }

        public FileWorkflowLibrary Library { get; }

        public void Dispose()
        {
            Library.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class StubOperation(string id) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            id,
            "Stub operation",
            "Seeder test operation.",
            JsonSchemas.EmptyObject);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Ok(null, "stub"));
    }
}
