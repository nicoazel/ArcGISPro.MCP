using System.Globalization;

namespace ArcGISProMCP.Core.Execution;

public sealed class ArcPyExecutionSettings
{
    private const int DefaultMaximumTimeoutSeconds = 300;
    private const int DefaultMaximumOutputCharacters = 65_536;
    private const int DefaultMaximumScriptBytes = 1_048_576;

    private ArcPyExecutionSettings(
        bool enabled,
        string pythonExecutable,
        string arcGisProBinDirectory,
        string scriptRoot,
        string workingRoot,
        TimeSpan maximumTimeout,
        int maximumOutputCharacters,
        int maximumScriptBytes)
    {
        Enabled = enabled;
        PythonExecutable = pythonExecutable;
        ArcGisProBinDirectory = arcGisProBinDirectory;
        ScriptRoot = scriptRoot;
        WorkingRoot = workingRoot;
        MaximumTimeout = maximumTimeout;
        MaximumOutputCharacters = maximumOutputCharacters;
        MaximumScriptBytes = maximumScriptBytes;
    }

    public bool Enabled { get; }

    public string PythonExecutable { get; }

    public string ArcGisProBinDirectory { get; }

    public string ScriptRoot { get; }

    public string WorkingRoot { get; }

    public TimeSpan MaximumTimeout { get; }

    public int MaximumOutputCharacters { get; }

    public int MaximumScriptBytes { get; }

    public static ArcPyExecutionSettings Create(
        bool enabled,
        string pythonExecutable,
        string arcGisProBinDirectory,
        string scriptRoot,
        string workingRoot,
        TimeSpan? maximumTimeout = null,
        int maximumOutputCharacters = DefaultMaximumOutputCharacters,
        int maximumScriptBytes = DefaultMaximumScriptBytes)
    {
        var normalizedPython = NormalizeAbsolutePath(pythonExecutable, nameof(pythonExecutable));
        var normalizedProBin = NormalizeAbsolutePath(arcGisProBinDirectory, nameof(arcGisProBinDirectory));
        var normalizedScriptRoot = NormalizeAbsolutePath(scriptRoot, nameof(scriptRoot));
        var normalizedWorkingRoot = NormalizeAbsolutePath(workingRoot, nameof(workingRoot));
        var timeout = maximumTimeout ?? TimeSpan.FromSeconds(DefaultMaximumTimeoutSeconds);

        if (!string.Equals(Path.GetFileName(normalizedPython), "python.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The ArcPy interpreter must be an explicit path to python.exe.", nameof(pythonExecutable));
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(maximumTimeout), "The maximum timeout must be between one second and fifteen minutes.");
        if (maximumOutputCharacters is < 1_024 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters), "Captured output must be limited to between 1024 and 1048576 characters per stream.");
        if (maximumScriptBytes is < 1_024 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(maximumScriptBytes), "Script size must be limited to between 1024 and 16777216 bytes.");
        if (PathsOverlap(normalizedScriptRoot, normalizedWorkingRoot))
            throw new ArgumentException("The ArcPy script and working roots must be separate directory trees.", nameof(workingRoot));

        return new ArcPyExecutionSettings(
            enabled,
            normalizedPython,
            normalizedProBin,
            normalizedScriptRoot,
            normalizedWorkingRoot,
            timeout,
            maximumOutputCharacters,
            maximumScriptBytes);
    }

    public static ArcPyExecutionSettings FromEnvironment(
        Func<string, string?>? getEnvironmentVariable = null,
        string? arcGisProInstallDirectory = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var enabled = IsExplicitlyEnabled(getEnvironmentVariable("ARCGIS_PRO_MCP_ENABLE_ARCPY"));
        var installRoot = NormalizeAbsolutePath(
            arcGisProInstallDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ArcGIS",
                "Pro"),
            nameof(arcGisProInstallDirectory));
        var proBin = Path.Combine(installRoot, "bin");
        var python = ValueOrDefault(
            getEnvironmentVariable("ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE"),
            Path.Combine(proBin, "Python", "envs", "arcgispro-py3", "python.exe"));
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMCP");
        if (!enabled)
        {
            return Create(
                false,
                Path.Combine(proBin, "Python", "envs", "arcgispro-py3", "python.exe"),
                proBin,
                Path.Combine(localRoot, "arcpy-scripts"),
                Path.Combine(localRoot, "arcpy-runs"));
        }

        var scriptRoot = ValueOrDefault(
            getEnvironmentVariable("ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT"),
            Path.Combine(localRoot, "arcpy-scripts"));
        var workingRoot = ValueOrDefault(
            getEnvironmentVariable("ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT"),
            Path.Combine(localRoot, "arcpy-runs"));
        var timeoutSeconds = ParseBoundedInteger(
            getEnvironmentVariable("ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS"),
            DefaultMaximumTimeoutSeconds,
            1,
            900,
            "ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS");
        var outputCharacters = ParseBoundedInteger(
            getEnvironmentVariable("ARCGIS_PRO_MCP_ARCPY_MAX_OUTPUT_CHARS"),
            DefaultMaximumOutputCharacters,
            1_024,
            1_048_576,
            "ARCGIS_PRO_MCP_ARCPY_MAX_OUTPUT_CHARS");
        var scriptBytes = ParseBoundedInteger(
            getEnvironmentVariable("ARCGIS_PRO_MCP_ARCPY_MAX_SCRIPT_BYTES"),
            DefaultMaximumScriptBytes,
            1_024,
            16_777_216,
            "ARCGIS_PRO_MCP_ARCPY_MAX_SCRIPT_BYTES");

        return Create(
            enabled,
            python,
            proBin,
            scriptRoot,
            workingRoot,
            TimeSpan.FromSeconds(timeoutSeconds),
            outputCharacters,
            scriptBytes);
    }

    public static bool TryFromEnvironment(
        out ArcPyExecutionSettings settings,
        out string? configurationError,
        Func<string, string?>? getEnvironmentVariable = null,
        string? arcGisProInstallDirectory = null)
    {
        try
        {
            settings = FromEnvironment(getEnvironmentVariable, arcGisProInstallDirectory);
            configurationError = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            var disabledRoot = Path.Combine(AppContext.BaseDirectory, "disabled-arcpy");
            settings = Create(
                false,
                Path.Combine(disabledRoot, "python.exe"),
                Path.Combine(disabledRoot, "pro-bin"),
                Path.Combine(disabledRoot, "scripts"),
                Path.Combine(disabledRoot, "runs"));
            configurationError = "ArcPy configuration was rejected; the optional ArcPy capability remains disabled.";
            return false;
        }
    }

    private static bool IsExplicitlyEnabled(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal) ||
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static int ParseBoundedInteger(string? value, int defaultValue, int minimum, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException($"{name} must be an integer between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static string ValueOrDefault(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified path is required.", parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }

    private static bool PathsOverlap(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    private static bool IsWithin(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
