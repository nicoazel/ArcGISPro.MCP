using System.Security.Cryptography;
using System.Text;
using ArcGISProMCP.Core.Execution;

namespace ArcGISProMCP.Core.Tests;

public sealed class ArcPyExecutionPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    public void Environment_configuration_requires_explicit_opt_in(string? value, bool expected)
    {
        using var fixture = new ArcPyFixture();
        var variables = fixture.EnvironmentVariables();
        if (value is not null) variables["ARCGIS_PRO_MCP_ENABLE_ARCPY"] = value;

        var settings = ArcPyExecutionSettings.FromEnvironment(
            name => variables.GetValueOrDefault(name),
            fixture.ProInstallRoot);

        Assert.Equal(expected, settings.Enabled);
        Assert.Equal(fixture.PythonExecutable, settings.PythonExecutable, StringComparer.OrdinalIgnoreCase);
        if (expected)
        {
            Assert.Equal(fixture.ScriptRoot, settings.ScriptRoot, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(fixture.WorkingRoot, settings.WorkingRoot, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Disabled_configuration_ignores_malformed_optional_runner_settings()
    {
        using var fixture = new ArcPyFixture();
        var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ARCGIS_PRO_MCP_ENABLE_ARCPY"] = "false",
            ["ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE"] = "relative.exe",
            ["ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS"] = "unbounded"
        };

        var settings = ArcPyExecutionSettings.FromEnvironment(
            name => variables.GetValueOrDefault(name),
            fixture.ProInstallRoot);

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void Optional_loader_fails_closed_without_exposing_configuration_values()
    {
        using var fixture = new ArcPyFixture();
        var variables = fixture.EnvironmentVariables();
        variables["ARCGIS_PRO_MCP_ENABLE_ARCPY"] = "true";
        variables["ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE"] = "secret-relative-path";

        var valid = ArcPyExecutionSettings.TryFromEnvironment(
            out var settings,
            out var error,
            name => variables.GetValueOrDefault(name),
            fixture.ProInstallRoot);

        Assert.False(valid);
        Assert.False(settings.Enabled);
        Assert.NotNull(error);
        Assert.DoesNotContain("secret-relative-path", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_rejects_overlapping_roots_and_unbounded_limits()
    {
        using var fixture = new ArcPyFixture();

        Assert.Throws<ArgumentException>(() => ArcPyExecutionSettings.Create(
            true,
            fixture.PythonExecutable,
            fixture.ProBin,
            fixture.ScriptRoot,
            Path.Combine(fixture.ScriptRoot, "runs")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArcPyExecutionSettings.Create(
            true,
            fixture.PythonExecutable,
            fixture.ProBin,
            fixture.ScriptRoot,
            fixture.WorkingRoot,
            TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArcPyExecutionSettings.Create(
            true,
            fixture.PythonExecutable,
            fixture.ProBin,
            fixture.ScriptRoot,
            fixture.WorkingRoot,
            maximumOutputCharacters: int.MaxValue));
    }

    [Fact]
    public void PrepareScript_pins_content_hash_arguments_and_timeout()
    {
        using var fixture = new ArcPyFixture();
        var content = Encoding.UTF8.GetBytes("import arcpy\nprint(arcpy.GetInstallInfo())\n");
        var scriptPath = fixture.WriteScript("analysis/run.py", content);
        var hash = Convert.ToHexString(SHA256.HashData(content));

        var prepared = ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings,
            "analysis/run.py",
            hash.ToLowerInvariant(),
            ["value with spaces", "; Remove-Item C:\\important"],
            12);

        Assert.Equal(Path.GetRelativePath(fixture.ScriptRoot, scriptPath), prepared.RelativePath);
        Assert.Equal(content, prepared.Content);
        Assert.Equal(hash, prepared.Sha256);
        Assert.Equal(["value with spaces", "; Remove-Item C:\\important"], prepared.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(12), prepared.Timeout);
    }

    [Fact]
    public void InspectScript_returns_the_hash_needed_for_approval_without_executing()
    {
        using var fixture = new ArcPyFixture();
        var content = Encoding.UTF8.GetBytes("import arcpy\nprint('inspect only')\n");
        fixture.WriteScript("inspect.py", content);

        var inspected = ArcPyExecutionPolicy.InspectScript(fixture.Settings, "inspect.py");

        Assert.Equal("inspect.py", inspected.RelativePath);
        Assert.Equal(content, inspected.Content);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), inspected.Sha256);
    }

    [Theory]
    [InlineData("../outside.py")]
    [InlineData("folder/../../outside.py")]
    [InlineData("not-python.txt")]
    public void PrepareScript_rejects_escape_and_non_python_paths(string path)
    {
        using var fixture = new ArcPyFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "outside.py"), "print('outside')");
        fixture.WriteScript("not-python.txt", Encoding.UTF8.GetBytes("not python"));

        var exception = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings,
            path,
            new string('0', 64),
            [],
            10));

        Assert.Equal("arcpy_script_rejected", exception.Code);
    }

    [Fact]
    public void PrepareScript_rejects_absolute_paths()
    {
        using var fixture = new ArcPyFixture();
        var outside = Path.Combine(fixture.Root, "outside.py");
        File.WriteAllText(outside, "print('outside')");

        var exception = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings,
            outside,
            new string('0', 64),
            [],
            10));

        Assert.Equal("arcpy_script_rejected", exception.Code);
    }

    [Fact]
    public void PrepareScript_rejects_changed_content_invalid_hash_and_excessive_timeout()
    {
        using var fixture = new ArcPyFixture();
        fixture.WriteScript("run.py", Encoding.UTF8.GetBytes("print('safe')"));

        var changed = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings,
            "run.py",
            new string('0', 64),
            [],
            10));
        var malformed = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings,
            "run.py",
            "not-a-hash",
            [],
            10));
        var timeout = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings,
            "run.py",
            new string('0', 64),
            [],
            301));

        Assert.Equal("arcpy_script_changed", changed.Code);
        Assert.Equal("arcpy_hash_rejected", malformed.Code);
        Assert.Equal("arcpy_timeout_rejected", timeout.Code);
    }

    [Fact]
    public void PrepareScript_rejects_disabled_or_non_arcpy_runtime()
    {
        using var fixture = new ArcPyFixture();
        var script = Encoding.UTF8.GetBytes("print('safe')");
        fixture.WriteScript("run.py", script);
        var hash = Convert.ToHexString(SHA256.HashData(script));
        var disabled = ArcPyExecutionSettings.Create(
            false,
            fixture.PythonExecutable,
            fixture.ProBin,
            fixture.ScriptRoot,
            fixture.WorkingRoot);

        var disabledFailure = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            disabled, "run.py", hash, [], 10));
        File.Delete(fixture.ArcPyMarker);
        var runtimeFailure = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings, "run.py", hash, [], 10));

        Assert.Equal("arcpy_disabled", disabledFailure.Code);
        Assert.Equal("arcpy_runtime_unavailable", runtimeFailure.Code);
    }

    [Fact]
    public void Scripts_reached_through_a_directory_junction_are_rejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are a Windows feature.");
        using var fixture = new ArcPyFixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        var content = Encoding.UTF8.GetBytes("print('outside the script root')");
        File.WriteAllBytes(Path.Combine(outside, "run.py"), content);
        var junction = Path.Combine(fixture.ScriptRoot, "linked");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", junction, outside },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            mklink.WaitForExit();
        }
        Assert.SkipUnless(Directory.Exists(junction), "A directory junction could not be created.");

        var inspect = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.InspectScript(fixture.Settings, "linked/run.py"));
        var prepare = Assert.Throws<ArcPyPolicyException>(() => ArcPyExecutionPolicy.PrepareScript(
            fixture.Settings, "linked/run.py", Convert.ToHexString(SHA256.HashData(content)), [], 10));

        Assert.Equal("arcpy_script_rejected", inspect.Code);
        Assert.Equal("arcpy_script_rejected", prepare.Code);
        Assert.Contains("reparse points", prepare.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArcGIS_Pro_bootstrap_marker_is_accepted_without_package_directory_marker()
    {
        using var fixture = new ArcPyFixture();
        File.Delete(fixture.ArcPyMarker);
        File.WriteAllText(fixture.ArcPyBootstrapMarker, string.Empty);

        var exception = Record.Exception(() => ArcPyExecutionPolicy.ValidateRuntime(fixture.Settings));

        Assert.Null(exception);
    }

    [Fact]
    public void Process_start_info_uses_argument_list_without_shell_and_a_clean_environment()
    {
        using var fixture = new ArcPyFixture();
        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows",
            ["TEMP"] = @"C:\Temp",
            ["USERNAME"] = "licensed-user",
            ["AWS_SECRET_ACCESS_KEY"] = "must-not-leak",
            ["PYTHONPATH"] = @"C:\untrusted"
        };
        var stableScript = Path.Combine(fixture.WorkingRoot, "run-id", "approved-script.py");
        var workingDirectory = Path.GetDirectoryName(stableScript)!;

        var startInfo = ArcPyProcessStartInfoFactory.Create(
            fixture.Settings,
            stableScript,
            workingDirectory,
            ["plain", "a & whoami", "$(Get-Secret)"],
            name => inherited.GetValueOrDefault(name));

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(fixture.PythonExecutable, startInfo.FileName);
        Assert.Equal(workingDirectory, startInfo.WorkingDirectory);
        Assert.Equal(
            ["-I", "-B", "-u", stableScript, "plain", "a & whoami", "$(Get-Secret)"],
            startInfo.ArgumentList);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PYTHONPATH", startInfo.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("1", startInfo.Environment["PYTHONNOUSERSITE"]);
        Assert.Equal("licensed-user", startInfo.Environment["USERNAME"]);
        Assert.DoesNotContain(@"C:\untrusted", startInfo.Environment["PATH"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_start_failure_has_a_bounded_result_and_cleans_the_stable_copy()
    {
        using var fixture = new ArcPyFixture();
        var content = Encoding.UTF8.GetBytes("print('never starts')");
        var prepared = new ArcPyPreparedScript(
            "run.py",
            content,
            Convert.ToHexString(SHA256.HashData(content)),
            [],
            TimeSpan.FromSeconds(1));

        var result = await new ArcPyProcessRunner().RunAsync(
            fixture.Settings,
            prepared,
            TestContext.Current.CancellationToken);

        Assert.Equal(ArcPyProcessOutcome.StartFailed, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.True(result.StandardError.Length <= fixture.Settings.MaximumOutputCharacters);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkingRoot));
    }

    private sealed class ArcPyFixture : IDisposable
    {
        public ArcPyFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"ArcGISProMCP-ArcPy-{Guid.NewGuid():N}");
            ProInstallRoot = Path.Combine(Root, "ArcGIS", "Pro");
            ProBin = Path.Combine(ProInstallRoot, "bin");
            PythonExecutable = Path.Combine(ProBin, "Python", "envs", "arcgispro-py3", "python.exe");
            ArcPyMarker = Path.Combine(Path.GetDirectoryName(PythonExecutable)!, "Lib", "site-packages", "arcpy", "__init__.py");
            ArcPyBootstrapMarker = Path.Combine(Path.GetDirectoryName(PythonExecutable)!, "Lib", "site-packages", "arcpy_init.py");
            ScriptRoot = Path.Combine(Root, "scripts");
            WorkingRoot = Path.Combine(Root, "runs");
            Directory.CreateDirectory(Path.GetDirectoryName(ArcPyMarker)!);
            Directory.CreateDirectory(ScriptRoot);
            File.WriteAllText(PythonExecutable, string.Empty);
            File.WriteAllText(ArcPyMarker, string.Empty);
            Settings = ArcPyExecutionSettings.Create(
                true,
                PythonExecutable,
                ProBin,
                ScriptRoot,
                WorkingRoot);
        }

        public string Root { get; }

        public string ProInstallRoot { get; }

        public string ProBin { get; }

        public string PythonExecutable { get; }

        public string ArcPyMarker { get; }

        public string ArcPyBootstrapMarker { get; }

        public string ScriptRoot { get; }

        public string WorkingRoot { get; }

        public ArcPyExecutionSettings Settings { get; }

        public string WriteScript(string relativePath, byte[] content)
        {
            var path = Path.Combine(ScriptRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public Dictionary<string, string?> EnvironmentVariables() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE"] = PythonExecutable,
            ["ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT"] = ScriptRoot,
            ["ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT"] = WorkingRoot
        };

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
