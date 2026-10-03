using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;
using ArcGISProMCP.Testing;

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
        Assert.Equal("Fixture", data.GetProperty("project").GetProperty("name").GetString());
        Assert.True(data.GetProperty("project").GetProperty("isOpen").GetBoolean());
        Assert.Equal("rev-0", data.GetProperty("revision").GetString());
        Assert.True(data.TryGetProperty("capturedAt", out _));
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
            Assert.Equal("City", data.GetProperty("name").GetString());
            Assert.Equal("rev-1", result.WorkspaceRevision);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Open_refuses_while_feature_edits_are_pending_instead_of_letting_ArcGIS_prompt()
    {
        using var pro = new FakePro();
        pro.State.HasEdits = true;
        var path = TempProject(out var directory);
        try
        {
            var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("project.open", Json(new { path })));

            Assert.Equal("pending_edits", exception.Code);
            Assert.Contains("project.save", exception.Message, StringComparison.Ordinal);
            Assert.Empty(pro.State.Calls);
            Assert.Equal("Fixture", pro.State.ProjectName);
            Assert.True(pro.State.HasEdits);
            Assert.Equal("rev-0", pro.Workspace.Revision);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Open_refuses_pending_edits_through_the_executor_precondition_before_the_token_is_checked()
    {
        using var pro = new FakePro();
        pro.State.HasEdits = true;
        var path = TempProject(out var directory);
        try
        {
            Assert.IsAssignableFrom<IExecutionPrecondition>(pro.Operation("project.open"));

            var result = await pro.InvokeAsync("project.open", Json(new { path }));

            Assert.False(result.Success);
            Assert.Equal("pending_edits", result.ErrorCode);
            Assert.Contains("did not spend", result.Message, StringComparison.Ordinal);
            Assert.Empty(pro.State.Calls);
            Assert.Equal("Fixture", pro.State.ProjectName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Open_refuses_while_the_project_has_unsaved_changes()
    {
        // Live (ArcGIS Pro 3.7.1): a dirty project, including a freshly opened untouched one, makes
        // Project.OpenAsync stop on the modal "Save changes to <project>?" prompt.
        using var pro = new FakePro();
        pro.State.IsDirty = true;
        var path = TempProject(out var directory);
        try
        {
            var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("project.open", Json(new { path })));

            Assert.Equal("unsaved_project_changes", exception.Code);
            Assert.Contains("Save changes?", exception.Message, StringComparison.Ordinal);
            Assert.Contains("project.save", exception.Message, StringComparison.Ordinal);
            Assert.Empty(pro.State.Calls);
            Assert.Equal("Fixture", pro.State.ProjectName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Open_refuses_unsaved_changes_through_the_executor_precondition_before_the_token_is_checked()
    {
        using var pro = new FakePro();
        pro.State.IsDirty = true;
        var path = TempProject(out var directory);
        try
        {
            var result = await pro.InvokeAsync("project.open", Json(new { path }));

            Assert.False(result.Success);
            Assert.Equal("unsaved_project_changes", result.ErrorCode);
            Assert.Contains("did not spend", result.Message, StringComparison.Ordinal);
            Assert.Empty(pro.State.Calls);
            Assert.Equal("rev-0", pro.Workspace.Revision);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Pending_edits_take_precedence_over_unsaved_project_changes()
    {
        using var pro = new FakePro();
        pro.State.HasEdits = true;
        pro.State.IsDirty = true;
        var path = TempProject(out var directory);
        try
        {
            var direct = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("project.open", Json(new { path })));
            var invoked = await pro.InvokeAsync("project.open", Json(new { path }));

            Assert.Equal("pending_edits", direct.Code);
            Assert.Equal("pending_edits", invoked.ErrorCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_feature_edit_then_save_then_open_succeeds()
    {
        using var pro = new FakePro();
        pro.State.TrackDirty = true;
        pro.State.AddMap("City", "Map", FakeLayer.Feature("Parcels"));
        var path = TempProject(out var directory);
        try
        {
            var created = await pro.RunAsync("feature.create", """{"layer": "Parcels", "geometry": {"type": "point", "x": 5, "y": 6}, "attributes": {"zone": "R2", "FLOORS": 3}}""");
            Assert.True(created.Success, created.Message);
            Assert.True(pro.State.HasEdits);

            var refused = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("project.open", Json(new { path })));
            Assert.Equal("pending_edits", refused.Code);

            var saved = await pro.RunAsync("project.save");
            Assert.True(saved.Data!.Value.GetProperty("editsSaved").GetBoolean());
            Assert.False(pro.State.HasEdits);

            var opened = await pro.RunAsync("project.open", Json(new { path }));
            Assert.True(opened.Success);
            Assert.Equal("City", pro.State.ProjectName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Save_saves_pending_feature_edits_before_the_project()
    {
        using var pro = new FakePro();
        pro.State.HasEdits = true;

        var result = await pro.RunAsync("project.save");

        Assert.True(result.Success);
        Assert.Equal(["project.save-edits", "project.save"], pro.State.Calls);
        Assert.False(pro.State.HasEdits);
        Assert.True(result.Data!.Value.GetProperty("saved").GetBoolean());
        Assert.True(result.Data!.Value.GetProperty("editsSaved").GetBoolean());
    }

    [Fact]
    public async Task Save_without_pending_edits_saves_only_the_project_and_reports_it()
    {
        using var pro = new FakePro();

        var result = await pro.RunAsync("project.save");

        Assert.True(result.Success);
        Assert.Equal(["project.save"], pro.State.Calls);
        Assert.False(result.Data!.Value.GetProperty("editsSaved").GetBoolean());
    }

    [Fact]
    public async Task Save_stops_before_the_project_when_ArcGIS_cannot_save_the_edits()
    {
        using var pro = new FakePro();
        pro.State.HasEdits = true;
        pro.State.FailEditSave = true;

        var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("project.save"));

        Assert.Equal("edits_not_saved", exception.Code);
        Assert.Equal(["project.save-edits"], pro.State.Calls);
        Assert.True(pro.State.HasEdits);
        Assert.Equal("rev-0", pro.Workspace.Revision);
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
        Assert.Equal("Fixture", result.Data!.Value.GetProperty("name").GetString());
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

    private static string TempProject(out DirectoryInfo directory)
    {
        directory = Directory.CreateTempSubdirectory("ArcGISProMCP-Open-");
        var path = Path.Combine(directory.FullName, "City.aprx");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);
}
