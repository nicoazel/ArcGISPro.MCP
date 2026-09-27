using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class ArcPyInspectScriptOperation(ArcPyExecutionSettings settings) : ProOperationBase(OperationDescriptor.Create(
    "arcpy.inspect-script",
    "Inspect an ArcPy script",
    "Returns the size and SHA-256 of a Python script in the locally configured ArcPy script root without executing it.",
    ArcPyOperationSchemas.InspectScriptInput,
    risk: OperationRisk.ReadOnly,
    executionTarget: ExecutionTarget.ExternalWorker,
    capabilities: ["arcpy"],
    tags: ["arcpy", "python", "script", "hash", "inspect"],
    aliases: ["hash arcpy script", "inspect python script"],
    examples: ["Inspect analysis.py and use the returned SHA-256 when requesting approval for arcpy.run-script."],
    related: ["arcpy.run-script"],
    typicalDuration: "milliseconds"))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspected = ArcPyExecutionPolicy.InspectScript(settings, RequiredString(arguments, "scriptPath"));
            var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Ok(Json(new
            {
                scriptPath = inspected.RelativePath,
                inspected.Sha256,
                sizeBytes = inspected.Content.Length
            }), snapshot.Revision);
        }
        catch (ArcPyPolicyException exception)
        {
            var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            return OperationResult.Fail(exception.Code, exception.Message, snapshot.Revision);
        }
    }
}

internal sealed class ArcPyRunScriptOperation(
    ArcPyExecutionSettings settings,
    ArcPyProcessRunner? runner = null) : ProOperationBase(OperationDescriptor.Create(
    "arcpy.run-script",
    "Run an approved ArcPy script",
    "Runs a hash-pinned Python script from the locally configured ArcPy script root in the ArcGIS Pro Python environment.",
    ArcPyOperationSchemas.RunScriptInput,
    risk: OperationRisk.ExternalSideEffect,
    requiresConfirmation: true,
    executionTarget: ExecutionTarget.ExternalWorker,
    capabilities: ["arcpy"],
    tags: ["arcpy", "python", "script", "automation"],
    aliases: ["run arcpy script", "execute python script"],
    examples: ["Run analysis.py after obtaining its SHA-256 and approving the exact path, hash, arguments, and workspace revision."],
    related: ["gp.run"],
    typicalDuration: "seconds-to-minutes",
    executesUserCode: true))
{
    private readonly ArcPyProcessRunner _runner = runner ?? new ArcPyProcessRunner();

    protected override async Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var scriptPath = RequiredString(arguments, "scriptPath");
        var scriptSha256 = RequiredString(arguments, "scriptSha256");
        var scriptArguments = arguments.TryGetProperty("arguments", out var argumentValues)
            ? argumentValues.EnumerateArray().Select(value => value.GetString()!).ToArray()
            : [];
        var timeoutSeconds = arguments.TryGetProperty("timeoutSeconds", out var timeoutValue)
            ? timeoutValue.GetInt32()
            : (int?)null;

        ArcPyPreparedScript prepared;
        try
        {
            prepared = ArcPyExecutionPolicy.PrepareScript(
                settings,
                scriptPath,
                scriptSha256,
                scriptArguments,
                timeoutSeconds);
        }
        catch (ArcPyPolicyException exception)
        {
            var rejectedSnapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            return OperationResult.Fail(exception.Code, exception.Message, rejectedSnapshot.Revision);
        }

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.ApplicationStopping);
        var processResult = await _runner.RunAsync(settings, prepared, executionCancellation.Token).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var data = Json(new
        {
            outcome = OutcomeName(processResult.Outcome),
            processResult.ExitCode,
            stdout = processResult.StandardOutput,
            stderr = processResult.StandardError,
            stdoutTruncated = processResult.StandardOutputTruncated,
            stderrTruncated = processResult.StandardErrorTruncated,
            durationMilliseconds = Math.Round(processResult.Duration.TotalMilliseconds),
            scriptSha256 = processResult.ScriptSha256,
            timeoutSeconds = (int)prepared.Timeout.TotalSeconds
        });

        return processResult.Outcome == ArcPyProcessOutcome.Succeeded
            ? OperationResult.Ok(data, snapshot.Revision)
            : OperationResult.Fail(
                ErrorCode(processResult.Outcome),
                OutcomeMessage(processResult.Outcome, processResult.ExitCode),
                snapshot.Revision) with
            { Data = data };
    }

    private static string OutcomeName(ArcPyProcessOutcome outcome) => outcome switch
    {
        ArcPyProcessOutcome.Succeeded => "succeeded",
        ArcPyProcessOutcome.Failed => "failed",
        ArcPyProcessOutcome.TimedOut => "timed_out",
        ArcPyProcessOutcome.Cancelled => "cancelled",
        ArcPyProcessOutcome.StartFailed => "start_failed",
        _ => "unknown"
    };

    private static string ErrorCode(ArcPyProcessOutcome outcome) => outcome switch
    {
        ArcPyProcessOutcome.Failed => "arcpy_exit_nonzero",
        ArcPyProcessOutcome.TimedOut => "arcpy_timed_out",
        ArcPyProcessOutcome.Cancelled => "arcpy_cancelled",
        ArcPyProcessOutcome.StartFailed => "arcpy_start_failed",
        _ => "arcpy_execution_failed"
    };

    private static string OutcomeMessage(ArcPyProcessOutcome outcome, int? exitCode) => outcome switch
    {
        ArcPyProcessOutcome.Failed => $"ArcPy script exited with code {exitCode}.",
        ArcPyProcessOutcome.TimedOut => "ArcPy script exceeded its approved timeout and its process tree was terminated.",
        ArcPyProcessOutcome.Cancelled => "ArcPy script was cancelled and its process tree was terminated.",
        ArcPyProcessOutcome.StartFailed => "ArcGIS Pro Python could not be started.",
        _ => "ArcPy script did not complete successfully."
    };
}
