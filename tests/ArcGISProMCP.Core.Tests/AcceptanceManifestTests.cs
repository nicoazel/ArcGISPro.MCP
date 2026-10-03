using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// Validates committed live acceptance evidence (docs/acceptance/&lt;yyyy-MM-dd&gt;-&lt;sha7&gt;/),
/// as written by tools/run-acceptance.ps1 -Commit, against the manifest schema and its
/// SHA256SUMS. Passes vacuously while no evidence has been committed.
/// </summary>
public sealed class AcceptanceManifestTests
{
    private static readonly string[] StepNames =
        ["preflight", "verify", "pro-install", "host-probe", "smoke", "feature-gp-arcpy", "stress", "operations"];

    private static readonly string[] StepStatuses = ["passed", "failed", "skipped", "blocked"];

    private static JsonElement Sha256() => JsonSchemas.String(pattern: "^[0-9a-f]{64}$");

    private static JsonElement Dll() => JsonSchemas.Object(
        [
            ("name", JsonSchemas.String(minLength: 1, maxLength: 128, pattern: "\\.dll$")),
            ("sha256", Sha256()),
        ],
        ["name", "sha256"]);

    private static JsonElement Section() => JsonSchemas.Object(
        [
            ("name", JsonSchemas.Enum(StepNames)),
            ("status", JsonSchemas.Enum(StepStatuses)),
            ("mutatesProject", JsonSchemas.Boolean()),
            ("requiresAutonomousMode", JsonSchemas.Boolean()),
            ("startedAtUtc", JsonSchemas.String(minLength: 1)),
            ("durationSeconds", JsonSchemas.Number(minimum: 0)),
            ("detail", JsonSchemas.String(maxLength: 4096)),
            ("evidence", JsonSchemas.Array(JsonSchemas.String(minLength: 1), maxItems: 256)),
        ],
        ["name", "status", "mutatesProject", "requiresAutonomousMode", "startedAtUtc", "durationSeconds", "detail", "evidence"]);

    internal static JsonElement ManifestSchema() => JsonSchemas.Object(
        [
            ("schemaVersion", JsonSchemas.Integer(minimum: 1, maximum: 1)),
            ("date", JsonSchemas.String(pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}$")),
            ("generatedAtUtc", JsonSchemas.String(minLength: 1)),
            ("sha", JsonSchemas.String(pattern: "^[0-9a-f]{40}$")),
            ("dirty", JsonSchemas.Boolean()),
            ("describe", JsonSchemas.String()),
            ("tag", JsonSchemas.String(minLength: 1)),
            ("version", JsonSchemas.String(pattern: "^[0-9A-Za-z][0-9A-Za-z.-]*$")),
            ("dotnet", JsonSchemas.String(minLength: 1)),
            ("powershell", JsonSchemas.String()),
            ("operator", JsonSchemas.String(minLength: 1, maxLength: 256)),
            ("pro", JsonSchemas.Object(
                [
                    ("installDir", JsonSchemas.String()),
                    ("realVersion", JsonSchemas.String(minLength: 1)),
                    ("exeProductVersion", JsonSchemas.String(minLength: 1)),
                    ("runningProcessIds", JsonSchemas.Array(JsonSchemas.Integer(minimum: 1))),
                    ("hostProcessId", JsonSchemas.Integer(minimum: 1)),
                    ("operationCount", JsonSchemas.Integer(minimum: 0)),
                    ("addInId", JsonSchemas.String(pattern: "^\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\}$")),
                ],
                ["realVersion", "exeProductVersion", "runningProcessIds", "addInId"])),
            ("package", JsonSchemas.Object(
                [("path", JsonSchemas.String(minLength: 1)), ("sha256", Sha256())],
                ["path", "sha256"])),
            ("dlls", JsonSchemas.Object(
                [("built", JsonSchemas.Array(Dll())), ("loaded", JsonSchemas.Array(Dll()))],
                ["built", "loaded"])),
            ("sections", JsonSchemas.Array(Section(), minItems: 1, maxItems: 32)),
            ("autonomousMode", JsonSchemas.Boolean()),
            ("allPassed", JsonSchemas.Boolean()),
            ("visuallyInspected", JsonSchemas.Array(JsonSchemas.String(minLength: 1, maxLength: 1024), maxItems: 64)),
            ("evidence", JsonSchemas.Array(JsonSchemas.String(minLength: 1), maxItems: 256)),
            ("evidenceSkipped", JsonSchemas.Array(JsonSchemas.String(minLength: 1), maxItems: 256)),
            // Written by run-acceptance.ps1 -Commit once it rewrote evidence paths to the committed
            // layout; older folders (2026-09-28-5f34f16) list working-evidence paths and omit it.
            ("evidencePathsRelative", JsonSchemas.Boolean()),
            ("visualNotesSource", JsonSchemas.String(minLength: 1, maxLength: 1024)),
        ],
        ["schemaVersion", "date", "sha", "dirty", "version", "dotnet", "operator", "pro", "package", "dlls", "sections", "autonomousMode", "allPassed"]);

    [Fact]
    public void Sample_manifest_is_valid()
    {
        var sample = JsonSchemas.Parse(File.ReadAllText(SamplePath()));
        Assert.Empty(OperationArgumentValidator.Validate(sample, ManifestSchema()));
        Assert.Empty(CommittedManifestProblems(sample, "2026-09-26-059d0cb"));
    }

    [Theory]
    [InlineData("sha", "\"059D0CB\"")]
    [InlineData("date", "\"26/09/2026\"")]
    [InlineData("schemaVersion", "2")]
    [InlineData("dirty", "\"false\"")]
    [InlineData("unexpected", "true")]
    public void Schema_rejects_malformed_top_level_values(string property, string json)
    {
        var sample = SampleNode();
        sample[property] = JsonNode.Parse(json);
        Assert.NotEmpty(OperationArgumentValidator.Validate(ToElement(sample), ManifestSchema()));
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("package")]
    [InlineData("sections")]
    [InlineData("pro")]
    public void Schema_requires_core_evidence_fields(string property)
    {
        var sample = SampleNode();
        sample.Remove(property);
        Assert.NotEmpty(OperationArgumentValidator.Validate(ToElement(sample), ManifestSchema()));
    }

    [Fact]
    public void Schema_rejects_unknown_section_status_and_name()
    {
        var badStatus = SampleNode();
        badStatus["sections"]![0]!["status"] = "ok";
        Assert.NotEmpty(OperationArgumentValidator.Validate(ToElement(badStatus), ManifestSchema()));

        var badName = SampleNode();
        badName["sections"]![0]!["name"] = "everything";
        Assert.NotEmpty(OperationArgumentValidator.Validate(ToElement(badName), ManifestSchema()));
    }

    [Fact]
    public void Committed_checks_reject_dirty_failed_mismatched_or_misfiled_evidence()
    {
        var dirty = SampleNode();
        dirty["dirty"] = true;
        Assert.NotEmpty(CommittedManifestProblems(ToElement(dirty), "2026-09-26-059d0cb"));

        var failed = SampleNode();
        failed["sections"]![1]!["status"] = "failed";
        Assert.NotEmpty(CommittedManifestProblems(ToElement(failed), "2026-09-26-059d0cb"));

        var mismatch = SampleNode();
        mismatch["dlls"]!["loaded"]![0]!["sha256"] = new string('0', 64);
        Assert.NotEmpty(CommittedManifestProblems(ToElement(mismatch), "2026-09-26-059d0cb"));

        var notAutonomous = SampleNode();
        notAutonomous["autonomousMode"] = false;
        Assert.NotEmpty(CommittedManifestProblems(ToElement(notAutonomous), "2026-09-26-059d0cb"));

        Assert.NotEmpty(CommittedManifestProblems(ToElement(SampleNode()), "2026-09-27-059d0cb"));
    }

    [Fact]
    public void Checksum_verification_detects_tampering_and_unlisted_files()
    {
        var folder = Directory.CreateTempSubdirectory("acceptance-sums-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "manifest.json"), "{}\n");
            Directory.CreateDirectory(Path.Combine(folder.FullName, "smoke"));
            File.WriteAllText(Path.Combine(folder.FullName, "smoke", "result.json"), "{\"ok\":true}\n");
            File.WriteAllText(Path.Combine(folder.FullName, "SHA256SUMS"),
                $"{HashFile(Path.Combine(folder.FullName, "manifest.json"))}  manifest.json\n" +
                $"{HashFile(Path.Combine(folder.FullName, "smoke", "result.json"))}  smoke/result.json\n");
            Assert.Empty(ChecksumProblems(folder.FullName));

            File.WriteAllText(Path.Combine(folder.FullName, "extra.txt"), "unlisted\n");
            Assert.Contains(ChecksumProblems(folder.FullName), p => p.Contains("extra.txt", StringComparison.Ordinal));
            File.Delete(Path.Combine(folder.FullName, "extra.txt"));

            File.AppendAllText(Path.Combine(folder.FullName, "smoke", "result.json"), " ");
            Assert.Contains(ChecksumProblems(folder.FullName), p => p.Contains("smoke/result.json", StringComparison.Ordinal));

            File.WriteAllText(Path.Combine(folder.FullName, "SHA256SUMS"), $"{new string('0', 64)}  ../outside.json\n");
            Assert.NotEmpty(ChecksumProblems(folder.FullName));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void Committed_acceptance_evidence_is_valid()
    {
        var root = Path.Combine(RepositoryRoot(), "docs", "acceptance");
        if (!Directory.Exists(root)) return;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(folder);
            var manifestPath = Path.Combine(folder, "manifest.json");
            Assert.True(File.Exists(manifestPath), $"{name}: manifest.json is missing.");
            Assert.True(File.Exists(Path.Combine(folder, "summary.md")), $"{name}: summary.md is missing.");
            Assert.True(File.Exists(Path.Combine(folder, "SHA256SUMS")), $"{name}: SHA256SUMS is missing.");

            var manifest = JsonSchemas.Parse(File.ReadAllText(manifestPath));
            var issues = OperationArgumentValidator.Validate(manifest, ManifestSchema());
            Assert.True(issues.Count == 0, $"{name}: " + string.Join("; ", issues.Select(i => $"{i.Path} {i.Message}")));
            var problems = CommittedManifestProblems(manifest, name);
            Assert.True(problems.Count == 0, $"{name}: " + string.Join("; ", problems));
            var paths = EvidencePathProblems(manifest, folder);
            Assert.True(paths.Count == 0, $"{name}: " + string.Join("; ", paths));
            var sums = ChecksumProblems(folder);
            Assert.True(sums.Count == 0, $"{name}: " + string.Join("; ", sums));
        }
    }

    /// <summary>Rules beyond the schema that committed (as opposed to working) evidence must meet.</summary>
    private static List<string> CommittedManifestProblems(JsonElement manifest, string folderName)
    {
        var problems = new List<string>();
        var sha = manifest.GetProperty("sha").GetString()!;
        var expectedFolder = $"{manifest.GetProperty("date").GetString()}-{sha[..7]}";
        if (!string.Equals(folderName, expectedFolder, StringComparison.Ordinal))
            problems.Add($"folder should be named {expectedFolder}");
        if (manifest.GetProperty("dirty").GetBoolean())
            problems.Add("evidence was recorded from a dirty working tree");
        if (!manifest.GetProperty("allPassed").GetBoolean())
            problems.Add("allPassed is false");

        var sections = manifest.GetProperty("sections").EnumerateArray().ToArray();
        foreach (var section in sections)
        {
            var status = section.GetProperty("status").GetString();
            if (status is "failed" or "blocked")
                problems.Add($"section {section.GetProperty("name").GetString()} is {status}");
        }
        foreach (var required in new[] { "verify", "pro-install", "host-probe" })
            if (!sections.Any(s => s.GetProperty("name").GetString() == required && s.GetProperty("status").GetString() == "passed"))
                problems.Add($"required step {required} did not pass");
        if (sections.Any(s => s.GetProperty("requiresAutonomousMode").GetBoolean() && s.GetProperty("status").GetString() == "passed") &&
            !manifest.GetProperty("autonomousMode").GetBoolean())
            problems.Add("an autonomous-mode section passed but autonomousMode is false");

        var built = manifest.GetProperty("dlls").GetProperty("built").EnumerateArray().ToArray();
        var loaded = manifest.GetProperty("dlls").GetProperty("loaded").EnumerateArray()
            .ToDictionary(d => d.GetProperty("name").GetString()!, d => d.GetProperty("sha256").GetString(), StringComparer.OrdinalIgnoreCase);
        if (built.Length == 0) problems.Add("no built DLL hashes");
        foreach (var dll in built)
        {
            var dllName = dll.GetProperty("name").GetString()!;
            if (!loaded.TryGetValue(dllName, out var loadedHash) ||
                !string.Equals(loadedHash, dll.GetProperty("sha256").GetString(), StringComparison.Ordinal))
                problems.Add($"{dllName}: loaded hash does not match the built package");
        }
        return problems;
    }

    [Fact]
    public void Evidence_paths_must_exist_when_the_manifest_declares_them_relative()
    {
        var folder = Directory.CreateTempSubdirectory("acceptance-paths-");
        try
        {
            var manifest = SampleNode();
            manifest["evidencePathsRelative"] = true;
            manifest["evidence"] = new JsonArray("host-probe/state.json", "images/layout-tod.png");
            manifest["visuallyInspected"] = new JsonArray(
                "images/layout-tod.png: URBAN TEST 01 layout renders",
                "not committed: stress/green/final-layout.png: kept under artifacts/ only",
                "All three layouts: no blank frames");
            foreach (var section in manifest["sections"]!.AsArray())
                section!["evidence"] = new JsonArray();
            manifest["sections"]![3]!["evidence"] = new JsonArray("host-probe/state.json");

            var missing = EvidencePathProblems(ToElement(manifest), folder.FullName);
            Assert.Contains(missing, p => p.Contains("host-probe/state.json", StringComparison.Ordinal));
            Assert.Contains(missing, p => p.Contains("images/layout-tod.png", StringComparison.Ordinal));
            Assert.DoesNotContain(missing, p => p.Contains("stress/green", StringComparison.Ordinal));
            Assert.DoesNotContain(missing, p => p.Contains("All three layouts", StringComparison.Ordinal));

            Directory.CreateDirectory(Path.Combine(folder.FullName, "host-probe"));
            Directory.CreateDirectory(Path.Combine(folder.FullName, "images"));
            File.WriteAllText(Path.Combine(folder.FullName, "host-probe", "state.json"), "{}\n");
            File.WriteAllBytes(Path.Combine(folder.FullName, "images", "layout-tod.png"), [0x89, 0x50]);
            Assert.Empty(EvidencePathProblems(ToElement(manifest), folder.FullName));

            // A working-evidence path the -Commit rewrite missed is caught.
            manifest["sections"]![0]!["evidence"] = new JsonArray("preflight.json");
            Assert.Contains(EvidencePathProblems(ToElement(manifest), folder.FullName), p => p.Contains("preflight.json", StringComparison.Ordinal));

            manifest["evidence"] = new JsonArray("../outside.json");
            Assert.Contains(EvidencePathProblems(ToElement(manifest), folder.FullName), p => p.Contains("../outside.json", StringComparison.Ordinal));
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void Manifests_without_relative_evidence_paths_are_exempt()
    {
        // The first committed folder (2026-09-28-5f34f16) predates the rule and is immutable.
        var sample = SampleNode();
        Assert.Null(sample["evidencePathsRelative"]);
        Assert.Empty(EvidencePathProblems(ToElement(sample), Path.GetTempPath()));

        sample["evidencePathsRelative"] = false;
        Assert.Empty(EvidencePathProblems(ToElement(sample), Path.GetTempPath()));
    }

    /// <summary>
    /// For manifests with <c>evidencePathsRelative: true</c>: every evidence path, and the path before
    /// ": " in each visual-inspection note (unless the note starts with "not committed: "), names a
    /// file inside the folder. The path-like rule matches Test-EvidencePathLike in run-acceptance.ps1.
    /// </summary>
    private static List<string> EvidencePathProblems(JsonElement manifest, string folder)
    {
        var problems = new List<string>();
        if (!manifest.TryGetProperty("evidencePathsRelative", out var relative) || relative.ValueKind != JsonValueKind.True)
            return problems;

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        void Check(string where, string path)
        {
            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (path.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(path) ||
                !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{where}: '{path}' is not a relative path inside the folder");
            else if (!File.Exists(full))
                problems.Add($"{where}: '{path}' is not in the folder");
        }

        foreach (var section in manifest.GetProperty("sections").EnumerateArray())
            foreach (var path in section.GetProperty("evidence").EnumerateArray())
                Check($"sections[{section.GetProperty("name").GetString()}].evidence", path.GetString()!);
        if (manifest.TryGetProperty("evidence", out var evidence))
            foreach (var path in evidence.EnumerateArray())
                Check("evidence", path.GetString()!);
        if (manifest.TryGetProperty("visuallyInspected", out var inspected))
        {
            foreach (var note in inspected.EnumerateArray().Select(n => n.GetString()!))
            {
                if (note.StartsWith("not committed: ", StringComparison.Ordinal)) continue;
                var separator = note.IndexOf(": ", StringComparison.Ordinal);
                if (separator <= 0) continue;
                var token = note[..separator];
                if (System.Text.RegularExpressions.Regex.IsMatch(token, @"[\\/]|\.[A-Za-z0-9]{1,5}$"))
                    Check("visuallyInspected", token);
            }
        }
        return problems;
    }

    /// <summary>Every file except SHA256SUMS is listed once, stays inside the folder and hashes correctly.</summary>
    private static List<string> ChecksumProblems(string folder)
    {
        var problems = new List<string>();
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(folder, "SHA256SUMS")).Where(l => l.Length > 0))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
            {
                problems.Add($"malformed line: {line}");
                continue;
            }
            var hash = line[..64];
            var relative = line[(separator + 2)..];
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.Contains('\\', StringComparison.Ordinal))
            {
                problems.Add($"path escapes the evidence folder: {relative}");
                continue;
            }
            if (!listed.Add(relative)) problems.Add($"listed twice: {relative}");
            if (!File.Exists(full)) problems.Add($"listed but missing: {relative}");
            else if (!string.Equals(HashFile(full), hash, StringComparison.Ordinal)) problems.Add($"hash mismatch: {relative}");
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != "SHA256SUMS" && !listed.Contains(relative)) problems.Add($"not listed in SHA256SUMS: {relative}");
        }
        return problems;
    }

    // ------------------------------------------------------------ live operation matrix

    private static readonly string[] CaseStatuses = ["passed", "failed", "skipped"];

    private static JsonElement AnyValue() => JsonSchemas.Parse("{}");

    /// <summary>operations/summary.json as written by tools/run-live-operations.ps1.</summary>
    internal static JsonElement OperationsSummarySchema() => JsonSchemas.Object(
        [
            ("schemaVersion", JsonSchemas.Integer(minimum: 1, maximum: 1)),
            ("startedAtUtc", JsonSchemas.String(minLength: 1)),
            ("finishedAtUtc", JsonSchemas.String(minLength: 1)),
            ("host", JsonSchemas.Object(
                [
                    ("kind", JsonSchemas.Enum("arcgis-pro", "fakehost")),
                    ("processId", AnyValue()),
                    ("operationCount", JsonSchemas.Integer(minimum: 0)),
                ],
                ["kind"])),
            ("capabilities", JsonSchemas.Object(
                [("arcpy", JsonSchemas.Boolean()), ("online", JsonSchemas.Boolean())],
                ["arcpy", "online"])),
            ("operations", JsonSchemas.Array(JsonSchemas.Object(
                [
                    ("id", JsonSchemas.String(minLength: 1, maxLength: 128)),
                    ("cases", JsonSchemas.Array(JsonSchemas.Object(
                        [
                            ("name", JsonSchemas.String(minLength: 1, maxLength: 128)),
                            ("kind", JsonSchemas.Enum("happy", "negative")),
                            ("status", JsonSchemas.Enum(CaseStatuses)),
                            ("errorCode", AnyValue()),
                            ("durationMs", JsonSchemas.Integer(minimum: 0)),
                            ("approval", AnyValue()),
                            ("detail", JsonSchemas.String(maxLength: 4096)),
                            ("via", JsonSchemas.Enum("gateway")),
                        ],
                        ["name", "kind", "status", "errorCode", "durationMs", "approval"]), maxItems: 64)),
                    ("covered", JsonSchemas.Boolean()),
                ],
                ["id", "cases", "covered"]), maxItems: 256)),
            ("coverage", JsonSchemas.Object(
                [
                    ("total", JsonSchemas.Integer(minimum: 0)),
                    ("covered", JsonSchemas.Integer(minimum: 0)),
                    ("uncovered", JsonSchemas.Array(JsonSchemas.String(minLength: 1))),
                ],
                ["total", "covered", "uncovered"])),
            ("cases", JsonSchemas.Object(
                [
                    ("total", JsonSchemas.Integer(minimum: 0)),
                    ("happy", JsonSchemas.Integer(minimum: 0)),
                    ("negative", JsonSchemas.Integer(minimum: 0)),
                    ("passed", JsonSchemas.Integer(minimum: 0)),
                    ("failed", JsonSchemas.Integer(minimum: 0)),
                    ("skipped", JsonSchemas.Integer(minimum: 0)),
                ],
                ["total", "happy", "negative", "passed", "failed", "skipped"])),
            ("approvals", JsonSchemas.Object(
                [
                    ("cards", JsonSchemas.Integer(minimum: 0)),
                    ("requested", JsonSchemas.Integer(minimum: 0)),
                    ("approved", JsonSchemas.Integer(minimum: 0)),
                    ("denied", JsonSchemas.Integer(minimum: 0)),
                    ("expired", JsonSchemas.Integer(minimum: 0)),
                    ("skipped", JsonSchemas.Integer(minimum: 0)),
                ],
                ["cards", "requested", "approved", "denied", "expired", "skipped"])),
            ("messageAudit", JsonSchemas.Object(
                [
                    ("checked", JsonSchemas.Integer(minimum: 0)),
                    ("flagged", JsonSchemas.Array(JsonSchemas.Object(
                        [("case", JsonSchemas.String(minLength: 1)), ("findings", JsonSchemas.Array(JsonSchemas.String(minLength: 1)))],
                        ["case", "findings"]))),
                ],
                ["checked", "flagged"])),
            ("allPassed", JsonSchemas.Boolean()),
            ("aborted", JsonSchemas.String(maxLength: 4096)),
        ],
        ["schemaVersion", "host", "capabilities", "operations", "coverage", "cases", "approvals", "messageAudit", "allPassed"]);

    private sealed record Descriptor(string Id, bool RequiresConfirmation);

    private static Descriptor[] Descriptors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-descriptors.json")));
        return [.. document.RootElement.EnumerateObject()
            .Select(entry => new Descriptor(entry.Name, entry.Value.GetProperty("requiresConfirmation").GetBoolean()))
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Rules beyond the schema for committed operations evidence: a live ArcGIS Pro host, every
    /// selected case passed, and exactly the registered operations covered by a passing happy path.
    /// </summary>
    private static List<string> OperationsSummaryProblems(JsonElement summary)
    {
        var problems = new List<string>();
        var ids = Descriptors().Select(descriptor => descriptor.Id).ToHashSet(StringComparer.Ordinal);
        if (summary.GetProperty("host").GetProperty("kind").GetString() != "arcgis-pro")
            problems.Add("operations evidence must come from ArcGIS Pro, not a FakeHost");
        if (!summary.GetProperty("allPassed").GetBoolean())
            problems.Add("operations allPassed is false");

        var operations = summary.GetProperty("operations").EnumerateArray().ToArray();
        var listed = operations.Select(operation => operation.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids.Except(listed, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            problems.Add($"operation {id} is not listed");
        foreach (var id in listed.Except(ids, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            problems.Add($"operation {id} is not a registered operation");

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var id = operation.GetProperty("id").GetString()!;
            var cases = operation.GetProperty("cases").EnumerateArray().ToArray();
            foreach (var failed in cases.Where(c => c.GetProperty("status").GetString() == "failed"))
                problems.Add($"{id} case {failed.GetProperty("name").GetString()} failed");
            var happyPassed = cases.Any(c => c.GetProperty("kind").GetString() == "happy" && c.GetProperty("status").GetString() == "passed");
            if (operation.GetProperty("covered").GetBoolean() != happyPassed)
                problems.Add($"{id} covered flag does not match its passing happy cases");
            if (happyPassed) covered.Add(id);
        }
        foreach (var id in ids.Except(covered, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            problems.Add($"operation {id} has no passing happy-path case");

        var coverage = summary.GetProperty("coverage");
        if (coverage.GetProperty("total").GetInt32() != ids.Count)
            problems.Add($"coverage.total is {coverage.GetProperty("total").GetInt32()}, expected {ids.Count}");
        if (coverage.GetProperty("covered").GetInt32() != covered.Count)
            problems.Add($"coverage.covered is {coverage.GetProperty("covered").GetInt32()}, but {covered.Count} operations have a passing happy case");
        return problems;
    }

    /// <summary>A summary in which every registered operation passed one happy and, if gated, one card case.</summary>
    private static JsonObject CompleteOperationsSummary()
    {
        var descriptors = Descriptors();
        var operations = new JsonArray();
        foreach (var descriptor in descriptors)
        {
            operations.Add(new JsonObject
            {
                ["id"] = descriptor.Id,
                ["cases"] = new JsonArray(new JsonObject
                {
                    ["name"] = descriptor.Id.Replace('.', '-') + "-happy",
                    ["kind"] = "happy",
                    ["status"] = "passed",
                    ["errorCode"] = null,
                    ["durationMs"] = 12,
                    ["approval"] = descriptor.RequiresConfirmation ? "approved" : null,
                }),
                ["covered"] = true,
            });
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["startedAtUtc"] = "2026-10-02T10:00:00.0000000Z",
            ["finishedAtUtc"] = "2026-10-02T10:20:00.0000000Z",
            ["host"] = new JsonObject { ["kind"] = "arcgis-pro", ["processId"] = 4242, ["operationCount"] = descriptors.Length },
            ["capabilities"] = new JsonObject { ["arcpy"] = true, ["online"] = true },
            ["operations"] = operations,
            ["coverage"] = new JsonObject { ["total"] = descriptors.Length, ["covered"] = descriptors.Length, ["uncovered"] = new JsonArray() },
            ["cases"] = new JsonObject { ["total"] = descriptors.Length, ["happy"] = descriptors.Length, ["negative"] = 0, ["passed"] = descriptors.Length, ["failed"] = 0, ["skipped"] = 0 },
            ["approvals"] = new JsonObject { ["cards"] = 8, ["requested"] = 8, ["approved"] = 7, ["denied"] = 1, ["expired"] = 0, ["skipped"] = 0 },
            ["messageAudit"] = new JsonObject { ["checked"] = 29, ["flagged"] = new JsonArray() },
            ["allPassed"] = true,
        };
    }

    [Fact]
    public void Operations_summary_rules_accept_full_coverage_and_reject_gaps()
    {
        var complete = CompleteOperationsSummary();
        Assert.Empty(OperationArgumentValidator.Validate(ToElement(complete), OperationsSummarySchema()));
        Assert.Empty(OperationsSummaryProblems(ToElement(complete)));

        var fakeHost = CompleteOperationsSummary();
        fakeHost["host"]!["kind"] = "fakehost";
        Assert.Contains(OperationsSummaryProblems(ToElement(fakeHost)), p => p.Contains("FakeHost", StringComparison.Ordinal));

        var missing = CompleteOperationsSummary();
        missing["operations"]!.AsArray().RemoveAt(0);
        Assert.Contains(OperationsSummaryProblems(ToElement(missing)), p => p.Contains("arcpy.inspect-script", StringComparison.Ordinal));

        var skipped = CompleteOperationsSummary();
        var firstCase = skipped["operations"]![1]!["cases"]![0]!;
        firstCase["status"] = "skipped";
        Assert.Contains(OperationsSummaryProblems(ToElement(skipped)), p => p.Contains("arcpy.run-script", StringComparison.Ordinal));

        var failed = CompleteOperationsSummary();
        failed["operations"]![2]!["cases"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "basemap-set-unknown", ["kind"] = "negative", ["status"] = "failed",
            ["errorCode"] = "operation_failed", ["durationMs"] = 5, ["approval"] = null,
        });
        Assert.Contains(OperationsSummaryProblems(ToElement(failed)), p => p.Contains("basemap-set-unknown", StringComparison.Ordinal));

        var badStatus = CompleteOperationsSummary();
        badStatus["operations"]![0]!["cases"]![0]!["status"] = "ok";
        Assert.NotEmpty(OperationArgumentValidator.Validate(ToElement(badStatus), OperationsSummarySchema()));
    }

    [Fact]
    public void Committed_operations_summaries_cover_every_registered_operation()
    {
        var root = Path.Combine(RepositoryRoot(), "docs", "acceptance");
        if (!Directory.Exists(root)) return;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(folder, "operations", "summary.json");
            if (!File.Exists(path)) continue;
            var name = Path.GetFileName(folder);
            var summary = JsonSchemas.Parse(File.ReadAllText(path));
            var issues = OperationArgumentValidator.Validate(summary, OperationsSummarySchema());
            Assert.True(issues.Count == 0, $"{name}: " + string.Join("; ", issues.Select(i => $"{i.Path} {i.Message}")));
            var problems = OperationsSummaryProblems(summary);
            Assert.True(problems.Count == 0, $"{name}: " + string.Join("; ", problems));
            Assert.True(File.Exists(Path.Combine(folder, "operations", "errors.md")), $"{name}: operations/errors.md is missing.");
        }
    }

    private sealed record PlanCase(string Key, string Operation, string Kind, string? Card, string? ExpectErrorCode, string[] Requires);

    private static PlanCase[] LivePlanCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "live-operations-plan.json")));
        static string? Optional(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return [.. document.RootElement.GetProperty("phases").EnumerateArray()
            .SelectMany(phase => phase.GetProperty("cases").EnumerateArray())
            .Select(item => new PlanCase(
                item.GetProperty("key").GetString()!,
                item.GetProperty("operation").GetString()!,
                item.GetProperty("kind").GetString()!,
                Optional(item, "card"),
                Optional(item, "expectErrorCode"),
                item.TryGetProperty("requires", out var requires) ? [.. requires.EnumerateArray().Select(r => r.GetString()!)] : []))];
    }

    [Fact]
    public void Live_operation_plan_covers_every_descriptor_and_gated_operation()
    {
        var cases = LivePlanCases();
        var descriptors = Descriptors();
        var ids = descriptors.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(cases.GroupBy(c => c.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Empty(cases.Where(c => !ids.Contains(c.Operation)).Select(c => $"{c.Key}: {c.Operation}"));
        Assert.All(cases, c => Assert.Contains(c.Kind, new[] { "happy", "negative" }));
        Assert.All(cases, c => Assert.All(c.Requires, r => Assert.Contains(r, new[] { "arcpy", "online" })));
        Assert.Empty(descriptors.Where(d => !cases.Any(c => c.Operation == d.Id && c.Kind == "happy")).Select(d => d.Id));

        foreach (var gated in descriptors.Where(d => d.RequiresConfirmation))
        {
            Assert.True(cases.Any(c => c.Operation == gated.Id && c.Kind == "happy" && c.Card == "approve"),
                $"{gated.Id} has no approve card");
            Assert.True(cases.Any(c => c.Operation == gated.Id && c.Card is null && c.ExpectErrorCode == "confirmation_required"),
                $"{gated.Id} has no no-token confirmation_required case");
        }
        Assert.All(cases.Where(c => c.Card is not null), c => Assert.Contains(c.Operation, descriptors.Where(d => d.RequiresConfirmation).Select(d => d.Id)));
        Assert.Equal(7, cases.Count(c => c.Card == "approve"));
        var deny = Assert.Single(cases, c => c.Card == "deny");
        Assert.Equal("confirmation_required", deny.ExpectErrorCode);
        // project.open replaces the project, so it is the final call of the run.
        Assert.Equal("project.open", cases[^1].Operation);
        Assert.Equal("approve", cases[^1].Card);
    }

    [Fact]
    public void Every_live_plan_case_is_implemented_by_the_runner()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "run-live-operations.ps1"));
        var implemented = System.Text.RegularExpressions.Regex.Matches(script, @"(?m)^Register-Case '([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Empty(implemented.GroupBy(key => key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
        var planned = LivePlanCases().Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(planned.Except(implemented, StringComparer.Ordinal));
        Assert.Empty(implemented.Except(planned, StringComparer.Ordinal));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string SamplePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "acceptance", "sample-manifest.json");

    private static JsonObject SampleNode() => JsonNode.Parse(File.ReadAllText(SamplePath()))!.AsObject();

    private static JsonElement ToElement(JsonNode node) => JsonSchemas.Parse(node.ToJsonString());

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ArcGISPro.MCP.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the ArcGISPro.MCP repository root.");
    }
}
