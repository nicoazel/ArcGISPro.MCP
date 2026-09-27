using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ArcGISProMCP.Core.Execution;

public sealed record ArcPyPreparedScript(
    string RelativePath,
    byte[] Content,
    string Sha256,
    ImmutableArray<string> Arguments,
    TimeSpan Timeout);

public sealed record ArcPyInspectedScript(
    string RelativePath,
    byte[] Content,
    string Sha256);

public static class ArcPyExecutionPolicy
{
    private const int MaximumArguments = 64;
    private const int MaximumArgumentCharacters = 8_192;
    private const int MaximumTotalArgumentCharacters = 32_768;

    public static ArcPyPreparedScript PrepareScript(
        ArcPyExecutionSettings settings,
        string relativeScriptPath,
        string expectedSha256,
        IEnumerable<string>? arguments,
        int? timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateHash(expectedSha256);
        var normalizedArguments = ValidateArguments(arguments);
        var timeout = ValidateTimeout(settings, timeoutSeconds);
        var inspected = InspectScript(settings, relativeScriptPath);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedSha256),
                Convert.FromHexString(inspected.Sha256)))
        {
            throw new ArcPyPolicyException(
                "arcpy_script_changed",
                "The script content does not match the SHA-256 value approved for execution.");
        }

        return new ArcPyPreparedScript(
            inspected.RelativePath,
            inspected.Content,
            inspected.Sha256,
            normalizedArguments,
            timeout);
    }

    public static ArcPyInspectedScript InspectScript(
        ArcPyExecutionSettings settings,
        string relativeScriptPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
            throw new ArcPyPolicyException("arcpy_disabled", "ArcPy execution is disabled by local configuration.");
        var sourcePath = ResolveScriptPath(settings.ScriptRoot, relativeScriptPath);
        ValidateRuntime(settings);
        EnsureNoReparsePoints(settings.ScriptRoot, sourcePath);
        var content = ReadBoundedScript(sourcePath, settings.MaximumScriptBytes);
        return new ArcPyInspectedScript(
            Path.GetRelativePath(settings.ScriptRoot, sourcePath),
            content,
            Convert.ToHexString(SHA256.HashData(content)));
    }

    public static void ValidateRuntime(ArcPyExecutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!File.Exists(settings.PythonExecutable))
            throw new ArcPyPolicyException("arcpy_runtime_unavailable", "The configured ArcGIS Pro Python executable was not found.");

        var pythonDirectory = Path.GetDirectoryName(settings.PythonExecutable)!;
        var sitePackages = Path.Combine(pythonDirectory, "Lib", "site-packages");
        var packageMarker = Path.Combine(sitePackages, "arcpy", "__init__.py");
        var bootstrapMarker = Path.Combine(sitePackages, "arcpy_init.py");
        if (!File.Exists(packageMarker) && !File.Exists(bootstrapMarker))
            throw new ArcPyPolicyException("arcpy_runtime_unavailable", "The configured Python environment does not contain ArcPy.");
    }

    private static string ResolveScriptPath(string scriptRoot, string relativeScriptPath)
    {
        if (string.IsNullOrWhiteSpace(relativeScriptPath) || Path.IsPathFullyQualified(relativeScriptPath))
            throw new ArcPyPolicyException("arcpy_script_rejected", "scriptPath must be a relative path below the configured script root.");
        if (relativeScriptPath.Contains('\0', StringComparison.Ordinal))
            throw new ArcPyPolicyException("arcpy_script_rejected", "scriptPath contains an invalid character.");

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(Path.Combine(scriptRoot, relativeScriptPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArcPyPolicyException("arcpy_script_rejected", "scriptPath is not a valid relative path.");
        }

        var relative = Path.GetRelativePath(scriptRoot, sourcePath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArcPyPolicyException("arcpy_script_rejected", "scriptPath escapes the configured script root.");
        }
        if (!string.Equals(Path.GetExtension(sourcePath), ".py", StringComparison.OrdinalIgnoreCase))
            throw new ArcPyPolicyException("arcpy_script_rejected", "Only .py script files can be executed.");
        if (!File.Exists(sourcePath))
            throw new ArcPyPolicyException("arcpy_script_not_found", "The requested script does not exist in the configured script root.");

        return sourcePath;
    }

    private static void EnsureNoReparsePoints(string scriptRoot, string sourcePath)
    {
        var current = Path.TrimEndingDirectorySeparator(scriptRoot);
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new ArcPyPolicyException("arcpy_script_rejected", "The configured script root cannot be a reparse point.");

        var relative = Path.GetRelativePath(current, sourcePath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArcPyPolicyException("arcpy_script_rejected", "Scripts reached through links or reparse points cannot be executed.");
        }
    }

    private static byte[] ReadBoundedScript(string path, int maximumBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximumBytes)
            throw new ArcPyPolicyException("arcpy_script_too_large", "The requested script exceeds the configured size limit.");

        using var content = new MemoryStream((int)Math.Min(input.Length, maximumBytes));
        var buffer = new byte[16_384];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > maximumBytes)
                throw new ArcPyPolicyException("arcpy_script_too_large", "The requested script exceeds the configured size limit.");
            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }

    private static ImmutableArray<string> ValidateArguments(IEnumerable<string>? arguments)
    {
        if (arguments is null) return [];
        var values = arguments.ToImmutableArray();
        if (values.Length > MaximumArguments)
            throw new ArcPyPolicyException("arcpy_arguments_rejected", $"No more than {MaximumArguments} script arguments are allowed.");

        var total = 0;
        foreach (var argument in values)
        {
            if (argument is null || argument.Contains('\0', StringComparison.Ordinal) || argument.Length > MaximumArgumentCharacters)
                throw new ArcPyPolicyException("arcpy_arguments_rejected", "A script argument is null, contains a null character, or is too long.");
            total = checked(total + argument.Length);
            if (total > MaximumTotalArgumentCharacters)
                throw new ArcPyPolicyException("arcpy_arguments_rejected", "The combined script arguments exceed the configured safety limit.");
        }

        return values;
    }

    private static TimeSpan ValidateTimeout(ArcPyExecutionSettings settings, int? timeoutSeconds)
    {
        var timeout = timeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(timeoutSeconds.Value)
            : settings.MaximumTimeout;
        if (timeout < TimeSpan.FromSeconds(1) || timeout > settings.MaximumTimeout)
            throw new ArcPyPolicyException("arcpy_timeout_rejected", $"timeoutSeconds must be between 1 and {(int)settings.MaximumTimeout.TotalSeconds}.");
        return timeout;
    }

    private static void ValidateHash(string expectedSha256)
    {
        if (expectedSha256 is null || expectedSha256.Length != 64)
            throw new ArcPyPolicyException("arcpy_hash_rejected", "scriptSha256 must contain exactly 64 hexadecimal characters.");
        try
        {
            _ = Convert.FromHexString(expectedSha256);
        }
        catch (FormatException)
        {
            throw new ArcPyPolicyException("arcpy_hash_rejected", "scriptSha256 must contain exactly 64 hexadecimal characters.");
        }
    }
}

public sealed class ArcPyPolicyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
