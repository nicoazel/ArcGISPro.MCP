using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

public sealed class ProjectOperationTests
{
    [Theory]
    [InlineData("project.open")]
    [InlineData("project.save")]
    public void Project_lifecycle_writes_require_confirmation(string id)
    {
        using var pro = new FakePro();
        var descriptor = pro.Operation(id).Descriptor;

        Assert.Equal(OperationRisk.SafeWrite, descriptor.Risk);
        Assert.True(descriptor.RequiresConfirmation);
        Assert.Equal(ExecutionTarget.ArcGISUiThread, descriptor.ExecutionTarget);
        Assert.Contains("Requires local approval", descriptor.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_get_stays_read_only_without_confirmation()
    {
        using var pro = new FakePro();
        var descriptor = pro.Operation("project.get").Descriptor;

        Assert.Equal(OperationRisk.ReadOnly, descriptor.Risk);
        Assert.False(descriptor.RequiresConfirmation);
    }

    [Fact]
    public async Task Get_returns_the_snapshot_project_revision_and_capture_time()
    {
        using var pro = new FakePro();

        var result = await pro.RunAsync("project.get");

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.Equal("Fixture", data.GetProperty("Project").GetProperty("Name").GetString());
        Assert.True(data.GetProperty("Project").GetProperty("IsOpen").GetBoolean());
        Assert.Equal("rev-0", data.GetProperty("Revision").GetString());
        Assert.True(data.TryGetProperty("CapturedAt", out _));
    }

    [Fact]
    public async Task Open_validates_the_path_before_touching_ArcGIS()
    {
        using var pro = new FakePro();
        var directory = Directory.CreateTempSubdirectory("ArcGISProMCP-Open-");
        try
        {
            var notAProject = Path.Combine(directory.FullName, "notes.txt");
            File.WriteAllText(notAProject, "not a project");
            var missing = Path.Combine(directory.FullName, "missing.aprx");

            await Assert.ThrowsAsync<FileNotFoundException>(() => pro.RunAsync("project.open", Json(new { path = missing })));
            var wrongType = await Assert.ThrowsAsync<ArgumentException>(() => pro.RunAsync("project.open", Json(new { path = notAProject })));

            Assert.StartsWith("Project path must reference an .aprx file.", wrongType.Message, StringComparison.Ordinal);
            Assert.Equal(0, pro.Dispatcher.UiCalls);
            Assert.Empty(pro.State.Calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Open_runs_on_the_UI_thread_with_the_full_path_and_reports_the_new_project()
    {
        using var pro = new FakePro();
        var directory = Directory.CreateTempSubdirectory("ArcGISProMCP-Open-");
        try
        {
            var path = Path.Combine(directory.FullName, "City.aprx");
            File.WriteAllText(path, string.Empty);

            var result = await pro.RunAsync("project.open", Json(new { path }));

            Assert.True(result.Success);
            Assert.Equal(1, pro.Dispatcher.UiCalls);
            Assert.Equal([$"project.open {path}"], pro.State.Calls);
            var data = result.Data!.Value;
            Assert.True(data.GetProperty("opened").GetBoolean());
            Assert.Equal(path, data.GetProperty("path").GetString());
            Assert.Equal("City", data.GetProperty("Name").GetString());
            Assert.Equal("rev-1", result.WorkspaceRevision);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Save_waits_for_the_clean_snapshot_before_publishing_its_revision()
    {
        using var pro = new FakePro();
        // SaveAsync returns while ArcGIS still reports the project dirty for a few samples.
        foreach (var dirty in new[] { true, true, true, false }) pro.State.DirtySamples.Enqueue(dirty);

        var result = await pro.RunAsync("project.save");

        Assert.True(result.Success);
        Assert.Equal(["project.save"], pro.State.Calls);
        Assert.False(pro.State.IsDirty);
        Assert.True(result.Data!.Value.GetProperty("saved").GetBoolean());
        Assert.Equal("Fixture", result.Data!.Value.GetProperty("Name").GetString());
        Assert.Equal(4 + 1, pro.Workspace.SnapshotCount); // four samples until clean, one settled snapshot
    }

    [Fact]
    public async Task Save_fails_when_the_project_never_reaches_a_clean_state()
    {
        using var pro = new FakePro();
        pro.State.IsDirty = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("project.save"));

        Assert.Equal("ArcGIS Pro did not reach a clean project state after SaveAsync.", exception.Message);
        Assert.Equal(ProjectSaveOperation.CleanStateAttempts + 1, pro.Workspace.SnapshotCount);
        Assert.Equal("rev-0", pro.Workspace.Revision);
    }

    [Fact]
    public async Task Save_surfaces_the_host_error_when_no_project_is_open()
    {
        using var pro = new FakePro();
        pro.State.ProjectUri = null;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("project.save"));

        Assert.Equal("No ArcGIS Pro project is open.", exception.Message);
    }

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);
}
