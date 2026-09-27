using System.Collections.Immutable;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.Core.Tests;

public sealed class WorkflowOperationPolicyTests : IDisposable
{
    private static readonly string[] UserCodeOperationIds = ["gp.run", "arcpy.run-script"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-policy-" + Guid.NewGuid().ToString("N"));
    private readonly FileWorkflowLibrary _library;

    public WorkflowOperationPolicyTests()
    {
        var registry = new OperationRegistry();
        registry.Register(new PolicyOperation("map.list", executesUserCode: false));
        registry.Register(new PolicyOperation("view.capture", executesUserCode: false));
        registry.Register(new PolicyOperation("gp.run", executesUserCode: true));
        registry.Register(new PolicyOperation("arcpy.run-script", executesUserCode: true));
        _library = new FileWorkflowLibrary(_root, registry);
    }

    [Fact]
    public async Task Workflow_without_allowlist_may_use_ordinary_operations()
    {
        var result = await _library.ValidateAsync(Workflow(["map.list", "view.capture"]), TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Theory]
    [InlineData("gp.run")]
    [InlineData("arcpy.run-script")]
    public async Task User_code_operation_is_rejected_unless_explicitly_allowed(string operationId)
    {
        var result = await _library.ValidateAsync(Workflow(["map.list", operationId]), TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("operation_not_allowed", issue.Code);
        Assert.Equal("s2", issue.StepId);
        Assert.Contains("executes user code", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_code_operation_is_accepted_when_listed_in_allowed_operations()
    {
        var workflow = Workflow(["map.list", "gp.run"], allowed: ["map.list", "GP.RUN"]);

        var result = await _library.ValidateAsync(workflow, TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public async Task Present_allowlist_is_exhaustive_for_ordinary_operations()
    {
        var workflow = Workflow(["map.list", "view.capture"], allowed: ["map.list"]);

        var result = await _library.ValidateAsync(workflow, TestContext.Current.CancellationToken);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("operation_not_allowed", issue.Code);
        Assert.Equal("s2", issue.StepId);
    }

    [Fact]
    public async Task Save_refuses_a_workflow_that_smuggles_user_code()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _library.SaveAsync(Workflow(["gp.run"]), TestContext.Current.CancellationToken));

        Assert.Contains("gp.run", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.workflow.json"));
    }

    [Fact]
    public async Task Allowed_operations_round_trip_and_absent_allowlist_keeps_canonical_json_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _library.SaveAsync(Workflow(["map.list"], id: "policy.plain"), cancellationToken);
        await _library.SaveAsync(Workflow(["gp.run"], id: "policy.allowed", allowed: ["gp.run"]), cancellationToken);

        var plainJson = await File.ReadAllTextAsync(Path.Combine(_root, "policy.plain@1.0.0.workflow.json"), cancellationToken);
        Assert.DoesNotContain("allowedOperations", plainJson, StringComparison.OrdinalIgnoreCase);

        var reloaded = await _library.GetAsync("policy.allowed", "1.0.0", cancellationToken);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded!.AllowedOperations);
        Assert.Contains("gp.run", reloaded.AllowedOperations!);
        Assert.True((await _library.ValidateAsync(reloaded, cancellationToken)).IsValid);
    }

    [Fact]
    public void Every_bundled_skill_workflow_stays_within_the_skill_allowlist()
    {
        var skillFiles = Directory.EnumerateFiles(RepoPath("skills"), "*.skill.json").ToArray();
        Assert.NotEmpty(skillFiles);
        var workflowsById = Directory.EnumerateFiles(RepoPath("workflows"), "*.workflow.json")
            .Select(path => JsonDocument.Parse(File.ReadAllText(path)))
            .ToDictionary(document => document.RootElement.GetProperty("id").GetString()!, StringComparer.Ordinal);
        try
        {
            foreach (var skillFile in skillFiles)
            {
                using var skill = JsonDocument.Parse(File.ReadAllText(skillFile));
                var allowed = skill.RootElement.GetProperty("allowedOperations").EnumerateArray()
                    .Select(value => value.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.NotEmpty(allowed);

                var workflowId = skill.RootElement.GetProperty("workflowId").GetString()!;
                Assert.True(workflowsById.TryGetValue(workflowId, out var workflow), $"Skill {skillFile} links missing workflow {workflowId}.");
                foreach (var step in workflow!.RootElement.GetProperty("steps").EnumerateArray())
                {
                    var operation = step.GetProperty("operation").GetString()!;
                    Assert.True(allowed.Contains(operation), $"{Path.GetFileName(skillFile)} does not allow step operation '{operation}'.");
                }
            }
        }
        finally
        {
            foreach (var document in workflowsById.Values) document.Dispose();
        }
    }

    [Fact]
    public void Bundled_workflows_do_not_run_user_code_without_an_explicit_allowlist()
    {
        foreach (var path in Directory.EnumerateFiles(RepoPath("workflows"), "*.workflow.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var allowed = document.RootElement.TryGetProperty("allowedOperations", out var list)
                ? list.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                var operation = step.GetProperty("operation").GetString()!;
                if (UserCodeOperationIds.Contains(operation, StringComparer.OrdinalIgnoreCase))
                    Assert.True(allowed.Contains(operation), $"{Path.GetFileName(path)} runs '{operation}' without allowing it explicitly.");
            }
        }
    }

    [Fact]
    public void Skill_loader_rejects_manifests_without_an_operation_allowlist()
    {
        var source = File.ReadAllText(Path.Combine(RepoPath("src"), "ArcGISProMCP.Server", "Skills", "SkillCatalog.cs"));

        Assert.Contains("skill.AllowedOperations is null || skill.AllowedOperations.IsEmpty", source, StringComparison.Ordinal);
    }

    private static WorkflowDefinition Workflow(string[] operations, string id = "policy.test", string[]? allowed = null) => new(
        id,
        "1.0.0",
        "Policy test",
        "Exercises workflow operation policy.",
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty,
        [],
        operations.Select((operation, index) => new WorkflowStep(
            $"s{index + 1}",
            operation,
            JsonSerializer.SerializeToElement(new { }),
            [])).ToImmutableArray(),
        AllowedOperations: allowed?.ToImmutableHashSet(StringComparer.Ordinal));

    private static string RepoPath(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArcGISPro.MCP.slnx")))
                return Path.Combine(directory.FullName, name);
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output tree.");
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class PolicyOperation(string id, bool executesUserCode) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            id, id, "Policy test operation.", JsonSchemas.EmptyObject,
            risk: executesUserCode ? OperationRisk.ExternalSideEffect : OperationRisk.ReadOnly,
            requiresConfirmation: executesUserCode,
            executesUserCode: executesUserCode);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Ok(null, "policy"));
    }
}
