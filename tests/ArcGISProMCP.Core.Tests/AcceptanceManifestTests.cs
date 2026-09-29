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
        ["preflight", "verify", "pro-install", "host-probe", "smoke", "feature-gp-arcpy", "stress"];

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
