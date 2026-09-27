using System.Text.RegularExpressions;

namespace ArcGISProMCP.Core.Tests;

public sealed class ProjectOperationSourceTests
{
    [Theory]
    [InlineData("project.open")]
    [InlineData("project.save")]
    public void Project_lifecycle_writes_require_confirmation(string id)
    {
        var descriptor = Descriptor(ReadSource("Operations", "ProjectOperations.cs"), id);

        Assert.Contains("risk: OperationRisk.SafeWrite, requiresConfirmation: true", descriptor, StringComparison.Ordinal);
        Assert.Contains("Requires local approval", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_get_stays_read_only_without_confirmation()
    {
        var descriptor = Descriptor(ReadSource("Operations", "ProjectOperations.cs"), "project.get");

        Assert.DoesNotContain("requiresConfirmation", descriptor, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationRisk.", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_initiated_confirmation_gated_operations_self_approve_through_the_audited_queue()
    {
        var panel = ReadSource("Services", "ProPanelStateSource.cs");

        Assert.Contains("SelfApprovePanelRequestAsync(id, argumentElement, workspace)", panel, StringComparison.Ordinal);
        Assert.Contains("WriteApprovalAuditAsync(request, ApprovalResolution.ApproveOnce, \"panel-action\")", panel, StringComparison.Ordinal);
        Assert.Contains("WriteApprovalAuditAsync(request, resolution, \"panel-card\")", panel, StringComparison.Ordinal);
        Assert.Contains("Kind: OperationAuditKinds.Approval", panel, StringComparison.Ordinal);
        Assert.Contains("approvals.Request(descriptor, arguments, workspace, reuseExisting: false)", panel, StringComparison.Ordinal);
        Assert.Contains("approvals.TryResolve(request.Id, ApprovalResolution.ApproveOnce)", panel, StringComparison.Ordinal);
        Assert.Contains("approvals.GetStatus(request.Id)", panel, StringComparison.Ordinal);
        Assert.Contains("confirmationToken }", panel, StringComparison.Ordinal);
        Assert.Contains("_approvals?.TryCancel(approval.Id)", panel, StringComparison.Ordinal);
        Assert.Contains("RunOperationAsync(\"project.open\"", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void Panel_approval_audit_failures_never_undo_or_mask_the_applied_decision()
    {
        // ProPanelStateSource needs ArcGIS Pro to run, so this stays a source-level check.
        var panel = ReadSource("Services", "ProPanelStateSource.cs");
        var start = panel.IndexOf("private async Task WriteApprovalAuditAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "WriteApprovalAuditAsync not found.");
        var body = panel[start..panel.IndexOf("private static string ArgumentsHash", start, StringComparison.Ordinal)];

        Assert.Contains("catch (Exception exception) when (exception is not OperationCanceledException)", body, StringComparison.Ordinal);
        Assert.Contains("AddActivity(ActivityLevel.Warning, \"Audit write failed\"", body, StringComparison.Ordinal);
    }

    private static string Descriptor(string source, string id)
    {
        var match = Regex.Match(source, $@"OperationDescriptor\.Create\(\s*""{Regex.Escape(id)}""");
        Assert.True(match.Success, $"Descriptor {id} not found.");
        var end = source.IndexOf("protected override", match.Index, StringComparison.Ordinal);
        return source[match.Index..end];
    }

    private static string ReadSource(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName, "src", "ArcGISProMCP.AddIn" }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)} from the test output tree.");
    }
}
