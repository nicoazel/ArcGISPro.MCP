using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Testing;
using ModelContextProtocol.Protocol;

namespace ArcGISProMCP.Server.Tests.EndToEnd;

/// <summary>
/// An E3 golden trajectory (evals/trajectories/*.json): the ordered MCP calls a well-behaved agent
/// makes for one task, the person's panel decisions, and the fake project state it must leave.
/// </summary>
internal sealed record Trajectory(
    string Id,
    string Task,
    IReadOnlyList<JsonElement> Steps,
    JsonElement Expect,
    string Scenario = EndToEndServer.DefaultScenario,
    bool Autonomous = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "trajectories");

    public static Trajectory Load(string path) =>
        JsonSerializer.Deserialize<Trajectory>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is empty.");

    public static Trajectory Parse(string json) =>
        JsonSerializer.Deserialize<Trajectory>(json, JsonOptions) ?? throw new InvalidDataException("Empty trajectory.");
}

/// <summary>
/// Grades one trajectory. schemaValidArgs: share of tool calls whose arguments validate against the
/// tool's inputSchema (and, for operation calls, whose operation arguments validate against the
/// registry schema). approvalDiscipline: share of confirmation-gated registry_invoke calls preceded
/// by an approval_request for the same operation, identical arguments and the same revision (a call
/// that declares expectError is a refusal probe and is exempt). taskSuccess: no failure of any
/// kind, including an unexpected isError and every final-state expectation.
/// </summary>
internal sealed record TrajectoryResult(
    string Id,
    int ToolCalls,
    double SchemaValidArgs,
    double ApprovalDiscipline,
    bool TaskSuccess,
    IReadOnlyList<string> Failures)
{
    public override string ToString()
    {
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{Id}: calls={ToolCalls} schemaValidArgs={SchemaValidArgs:0.000} approvalDiscipline={ApprovalDiscipline:0.000} taskSuccess={TaskSuccess}");
        return Failures.Count == 0
            ? summary
            : summary + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", Failures);
    }
}

internal static class TrajectoryRunner
{
    public static async Task<TrajectoryResult> RunAsync(Trajectory trajectory, CancellationToken cancellationToken)
    {
        await using var server = await EndToEndServer.StartAsync(
            new FakeHostOptions(Autonomous: trajectory.Autonomous), trajectory.Scenario, cancellationToken).ConfigureAwait(false);
        var tools = await server.ToolsAsync(cancellationToken).ConfigureAwait(false);
        var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var approvals = new List<(string OperationId, JsonElement Arguments, string? Revision)>();
        var failures = new List<string>();
        int toolCalls = 0, validCalls = 0, gatedInvokes = 0, disciplinedInvokes = 0;

        for (var index = 0; index < trajectory.Steps.Count; index++)
        {
            var step = trajectory.Steps[index];
            var label = $"step {index + 1}";
            if (step.TryGetProperty("approve", out var approve))
            {
                // The person at the ArcGIS Pro panel approves the pending request.
                var pending = server.Runtime.Approvals.GetPending().Where(request => request.OperationId == approve.GetString()).ToArray();
                if (pending.Length != 1)
                    failures.Add($"{label}: expected one pending {approve.GetString()} request, found {pending.Length}.");
                else
                    server.ApproveAsPerson(approve.GetString()!);
                continue;
            }

            if (step.TryGetProperty("prompt", out var prompt))
            {
                await RunPromptAsync(server, step, prompt.GetString()!, label, failures, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var name = step.GetProperty("tool").GetString()!;
            label += $" ({name})";
            var arguments = Substitute(step.TryGetProperty("arguments", out var template) ? template : JsonSerializer.SerializeToElement(new { }), variables, label, failures);
            toolCalls++;
            if (!tools.TryGetValue(name, out var tool))
            {
                failures.Add($"{label}: the server has no such tool.");
                continue;
            }

            var issues = ToolSchema.Validate(tool.InputSchema, arguments).ToList();
            issues.AddRange(OperationArgumentIssues(server, name, arguments));
            if (issues.Count == 0) validCalls++;
            else failures.Add($"{label}: invalid arguments: {string.Join("; ", issues)}");

            var expectError = step.TryGetProperty("expectError", out var expectedError) ? expectedError.GetString() : null;
            if (name == "approval_request")
                approvals.Add((String(arguments, "operationId")!, Member(arguments, "arguments"), String(arguments, "expectedRevision")));
            if (name == "registry_invoke" && IsGatedInvoke(server, arguments) && expectError is null)
            {
                gatedInvokes++;
                var operationId = String(arguments, "operationId");
                var operationArguments = Member(arguments, "arguments");
                var revision = String(arguments, "expectedRevision");
                if (approvals.Any(request => request.OperationId == operationId &&
                                             JsonElement.DeepEquals(request.Arguments, operationArguments) &&
                                             request.Revision == revision))
                    disciplinedInvokes++;
                else
                    failures.Add($"{label}: {operationId} was invoked without a preceding approval_request for the same arguments and revision.");
            }

            var outcome = await server.CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            if (outcome.IsError != (expectError is not null))
                failures.Add(outcome.IsError
                    ? $"{label}: unexpected isError {outcome.ErrorCode}: {outcome.Envelope.GetProperty("error").GetProperty("message").GetString()}"
                    : $"{label}: expected error {expectError} but the call succeeded.");
            else if (expectError is not null && outcome.ErrorCode != expectError)
                failures.Add($"{label}: expected error {expectError}, got {outcome.ErrorCode}.");

            var result = outcome.Result;
            if (step.TryGetProperty("expectResult", out var expectations))
            {
                foreach (var expectation in expectations.EnumerateObject())
                {
                    var actual = JsonPath.Select(result, expectation.Name);
                    if (actual is null || !JsonElement.DeepEquals(actual.Value, expectation.Value))
                        failures.Add($"{label}: {expectation.Name} was {actual?.GetRawText() ?? "missing"}, expected {expectation.Value.GetRawText()}.");
                }
            }
            if (step.TryGetProperty("capture", out var captures))
            {
                foreach (var capture in captures.EnumerateObject())
                {
                    if (JsonPath.Select(result, capture.Value.GetString()!) is { } value)
                        variables[capture.Name] = value.Clone();
                    else
                        failures.Add($"{label}: nothing at {capture.Value.GetString()} to capture as {capture.Name}.");
                }
            }
        }

        CheckFinalState(server.Runtime, trajectory.Expect, variables, failures);
        return new TrajectoryResult(
            trajectory.Id,
            toolCalls,
            toolCalls == 0 ? 1 : (double)validCalls / toolCalls,
            gatedInvokes == 0 ? 1 : (double)disciplinedInvokes / gatedInvokes,
            failures.Count == 0,
            failures);
    }

    private static async Task RunPromptAsync(EndToEndServer server, JsonElement step, string name, string label, List<string> failures, CancellationToken cancellationToken)
    {
        label += $" (prompt {name})";
        var prompts = await server.Client.ListPromptsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (prompts.All(candidate => candidate.Name != name))
        {
            failures.Add($"{label}: prompts/list does not offer it.");
            return;
        }
        var arguments = step.TryGetProperty("arguments", out var values)
            ? values.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.GetString(), StringComparer.Ordinal)
            : [];
        var result = await server.Client.GetPromptAsync(name, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = string.Join("\n", result.Messages.Select(message => message.Content).OfType<TextContentBlock>().Select(block => block.Text));
        if (step.TryGetProperty("expectText", out var expected))
        {
            foreach (var fragment in expected.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!text.Contains(fragment, StringComparison.Ordinal))
                    failures.Add($"{label}: the prompt text does not mention '{fragment}'.");
            }
        }
    }

    /// <summary>Operation arguments inside registry_validate/registry_invoke/approval_request against the registry schema.</summary>
    private static IEnumerable<string> OperationArgumentIssues(EndToEndServer server, string tool, JsonElement arguments)
    {
        if (tool is not ("registry_validate" or "registry_invoke" or "approval_request")) yield break;
        var operationId = String(arguments, "operationId");
        if (operationId is null || !server.Runtime.Pro.Registry.TryGet(operationId, out var operation))
        {
            yield return $"unknown operation '{operationId}'";
            yield break;
        }
        foreach (var issue in OperationArgumentValidator.Validate(Member(arguments, "arguments"), operation.Descriptor.InputSchema))
            yield return $"arguments{issue.Path[1..]}: {issue.Message}";
    }

    /// <summary>The executor's rule: confirmation-required, Destructive and ExternalSideEffect operations need review.</summary>
    private static bool IsGatedInvoke(EndToEndServer server, JsonElement arguments)
    {
        if (arguments.TryGetProperty("dryRun", out var dryRun) && dryRun.ValueKind == JsonValueKind.True) return false;
        return String(arguments, "operationId") is { } id &&
               server.Runtime.Pro.Registry.TryGet(id, out var operation) &&
               (operation.Descriptor.RequiresConfirmation ||
                operation.Descriptor.Risk is OperationRisk.Destructive or OperationRisk.ExternalSideEffect);
    }

    private static void CheckFinalState(FakeHostRuntime runtime, JsonElement expect, Dictionary<string, JsonElement> variables, List<string> failures)
    {
        if (expect.ValueKind != JsonValueKind.Object) return;
        foreach (var property in expect.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "rowCounts":
                    foreach (var layer in value.EnumerateObject())
                    {
                        var count = runtime.Table(layer.Name).Rows.Count;
                        if (count != layer.Value.GetInt32()) failures.Add($"final: {layer.Name} has {count} rows, expected {layer.Value.GetInt32()}.");
                    }
                    break;
                case "absent":
                    foreach (var layer in value.EnumerateObject())
                    foreach (var id in layer.Value.EnumerateArray().Select(item => item.GetInt64()))
                    {
                        if (runtime.Table(layer.Name).Rows.Any(row => row.ObjectId == id)) failures.Add($"final: {layer.Name} still has ObjectID {id}.");
                    }
                    break;
                case "values":
                    foreach (var check in value.EnumerateArray())
                    {
                        var layer = check.GetProperty("layer").GetString()!;
                        var id = check.GetProperty("objectId").GetInt64();
                        var field = check.GetProperty("field").GetString()!;
                        var expected = check.GetProperty("equals");
                        var row = runtime.Table(layer).Rows.SingleOrDefault(candidate => candidate.ObjectId == id);
                        var actual = row is null ? null : Convert.ToString(row.Values.GetValueOrDefault(field), CultureInfo.InvariantCulture);
                        var wanted = expected.ValueKind == JsonValueKind.String ? expected.GetString() : expected.GetRawText();
                        if (actual != wanted) failures.Add($"final: {layer} {id} {field} is '{actual}', expected '{wanted}'.");
                    }
                    break;
                case "dirty":
                    if (runtime.State.IsDirty != value.GetBoolean()) failures.Add($"final: project dirty is {runtime.State.IsDirty}, expected {value.GetBoolean()}.");
                    break;
                case "layers":
                    foreach (var name in value.EnumerateArray().Select(item => item.GetString()!))
                    {
                        if (runtime.FindLayer(name) is null) failures.Add($"final: layer '{name}' is missing.");
                    }
                    break;
                case "selection":
                    foreach (var layer in value.EnumerateObject())
                    {
                        var actual = runtime.Table(layer.Name).Selection.Order().ToArray();
                        var wanted = layer.Value.EnumerateArray().Select(item => item.GetInt64()).Order().ToArray();
                        if (!actual.SequenceEqual(wanted)) failures.Add($"final: {layer.Name} selection is [{string.Join(", ", actual)}], expected [{string.Join(", ", wanted)}].");
                    }
                    break;
                case "hostCalls":
                    foreach (var call in value.EnumerateArray().Select(item => item.GetString()!))
                    {
                        if (!runtime.State.Calls.Contains(call)) failures.Add($"final: the host never saw '{call}'.");
                    }
                    break;
                case "gpTools":
                    var tools = runtime.Pro.Geoprocessing.Calls.Select(call => call.Tool).ToArray();
                    var wantedTools = value.EnumerateArray().Select(item => item.GetString()!).ToArray();
                    if (!tools.SequenceEqual(wantedTools)) failures.Add($"final: geoprocessing ran [{string.Join(", ", tools)}], expected [{string.Join(", ", wantedTools)}].");
                    break;
                case "captures":
                    foreach (var capture in value.EnumerateObject())
                    {
                        var actual = variables.TryGetValue(capture.Name, out var captured) ? captured : (JsonElement?)null;
                        if (actual is null || !JsonElement.DeepEquals(actual.Value, capture.Value))
                            failures.Add($"final: captured {capture.Name} is {actual?.GetRawText() ?? "missing"}, expected {capture.Value.GetRawText()}.");
                    }
                    break;
                default:
                    failures.Add($"final: unknown expectation '{property.Name}'.");
                    break;
            }
        }
    }

    /// <summary>Replaces every string value that is exactly <c>${name}</c> with the captured value.</summary>
    private static JsonElement Substitute(JsonElement template, Dictionary<string, JsonElement> variables, string label, List<string> failures)
    {
        return JsonSerializer.SerializeToElement(Resolve(JsonNode.Parse(template.GetRawText())));

        JsonNode? Resolve(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject jsonObject:
                    return new JsonObject(jsonObject.Select(property => KeyValuePair.Create(property.Key, Resolve(property.Value))));
                case JsonArray jsonArray:
                    return new JsonArray([.. jsonArray.Select(Resolve)]);
                case JsonValue value when value.TryGetValue<string>(out var text) && text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'):
                    var name = text[2..^1];
                    if (variables.TryGetValue(name, out var captured)) return JsonNode.Parse(captured.GetRawText());
                    failures.Add($"{label}: ${{{name}}} was never captured.");
                    return JsonValue.Create(text);
                default:
                    return node?.DeepClone();
            }
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
}

/// <summary>Selects a value by a path such as <c>$.data.rows[0].objectId</c> or <c>$[0].id</c>.</summary>
internal static class JsonPath
{
    public static JsonElement? Select(JsonElement root, string path)
    {
        if (!path.StartsWith('$')) throw new ArgumentException($"Path '{path}' must start with $.", nameof(path));
        var current = root;
        var index = 1;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                var end = path.IndexOfAny(['.', '['], index + 1);
                if (end < 0) end = path.Length;
                var name = path[(index + 1)..end];
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
                index = end;
            }
            else if (path[index] == '[')
            {
                var end = path.IndexOf(']', index);
                var position = int.Parse(path[(index + 1)..end], CultureInfo.InvariantCulture);
                if (current.ValueKind != JsonValueKind.Array || position >= current.GetArrayLength()) return null;
                current = current[position];
                index = end + 1;
            }
            else
            {
                throw new ArgumentException($"Cannot parse path '{path}'.", nameof(path));
            }
        }
        return current;
    }
}

/// <summary>
/// Validates tool arguments against an MCP tool inputSchema as the SDK emits it: type (a name or an
/// array of names), properties, required, enum and items, with description and default as
/// annotations. Arguments the schema does not declare are rejected, since a tool's parameters are
/// fixed. Any other keyword is reported, so a schema change cannot pass unchecked.
/// </summary>
internal static class ToolSchema
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal) { "$schema", "title", "description", "default" };

    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement value)
    {
        var issues = new List<string>();
        Check(schema, value, "$", strict: true, issues);
        return issues;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, bool strict, List<string> issues)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        foreach (var keyword in schema.EnumerateObject())
        {
            if (keyword.Name is not ("type" or "properties" or "required" or "enum" or "items") && !Annotations.Contains(keyword.Name))
                issues.Add($"{path}: unsupported schema keyword '{keyword.Name}'");
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()!).ToArray() : [type.GetString()!];
            if (!allowed.Any(name => Matches(name, value)))
            {
                issues.Add($"{path}: expected {string.Join("|", allowed)}, found {value.ValueKind}");
                return;
            }
        }
        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            issues.Add($"{path}: not one of the enumerated values");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                {
                    if (!value.TryGetProperty(name, out _)) issues.Add($"{path}: missing required '{name}'");
                }
            }
            foreach (var member in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(member.Name, out var memberSchema))
                    Check(memberSchema, member.Value, $"{path}.{member.Name}", strict: false, issues);
                else if (strict)
                    issues.Add($"{path}: unknown argument '{member.Name}'");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) Check(items, item, $"{path}[{index++}]", strict: false, issues);
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => false
    };
}
