using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGISProMCP.Core.Workflows;

public sealed record SkillManifest(
    string Id,
    string Version,
    string Title,
    string Summary,
    ImmutableHashSet<string> Tags,
    ImmutableHashSet<string> RequiredCapabilities,
    ImmutableHashSet<string> AllowedOperations,
    ImmutableArray<string> Preconditions,
    ImmutableArray<string> VisualChecks,
    ImmutableArray<string> RecoveryGuidance,
    string WorkflowId);

public sealed record WorkflowDefinition(
    string Id,
    string Version,
    string Title,
    string Summary,
    ImmutableHashSet<string> Tags,
    ImmutableHashSet<string> RequiredCapabilities,
    ImmutableArray<WorkflowParameter> Parameters,
    ImmutableArray<WorkflowStep> Steps,
    string? ContentHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ImmutableHashSet<string>? AllowedOperations = null);

public sealed record WorkflowParameter(
    string Name,
    string Type,
    bool Required,
    JsonElement? DefaultValue,
    string? Description);

public sealed record WorkflowStep(
    string Id,
    string Operation,
    JsonElement Arguments,
    ImmutableArray<string> DependsOn,
    bool ContinueOnError = false,
    string? ExpectedObservation = null);

public sealed record WorkflowValidationIssue(string Code, string Message, string? StepId = null);

public sealed record WorkflowValidationResult(bool IsValid, ImmutableArray<WorkflowValidationIssue> Issues);

public sealed record WorkflowRunSummary(
    string RunId,
    string WorkflowId,
    string WorkflowVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string Outcome,
    int SucceededSteps,
    int FailedSteps,
    string? UserCorrection = null);

public sealed record WorkflowRanking(
    string WorkflowId,
    string Version,
    int SuccessfulRuns,
    int FailedRuns,
    int UserCorrections,
    double Score);
