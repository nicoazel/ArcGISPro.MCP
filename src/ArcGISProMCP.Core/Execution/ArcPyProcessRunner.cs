using System.Diagnostics;
using System.Text;

namespace ArcGISProMCP.Core.Execution;

public enum ArcPyProcessOutcome
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    StartFailed
}

public sealed record ArcPyProcessResult(
    ArcPyProcessOutcome Outcome,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    TimeSpan Duration,
    string ScriptSha256);

public static class ArcPyProcessStartInfoFactory
{
    public static ProcessStartInfo Create(
        ArcPyExecutionSettings settings,
        string stableScriptPath,
        string workingDirectory,
        IEnumerable<string> arguments,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var startInfo = new ProcessStartInfo
        {
            FileName = settings.PythonExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-B");
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(stableScriptPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        CopyIfPresent(startInfo, getEnvironmentVariable, "SystemRoot");
        CopyIfPresent(startInfo, getEnvironmentVariable, "WINDIR");
        CopyIfPresent(startInfo, getEnvironmentVariable, "TEMP");
        CopyIfPresent(startInfo, getEnvironmentVariable, "TMP");
        CopyIfPresent(startInfo, getEnvironmentVariable, "LOCALAPPDATA");
        CopyIfPresent(startInfo, getEnvironmentVariable, "APPDATA");
        CopyIfPresent(startInfo, getEnvironmentVariable, "PROGRAMDATA");
        // ArcGIS Pro's named-user licensing bootstrap requires the Windows user name.
        // Keep this one identity value while still dropping arbitrary inherited secrets.
        CopyIfPresent(startInfo, getEnvironmentVariable, "USERNAME");

        var pythonDirectory = Path.GetDirectoryName(settings.PythonExecutable)!;
        var pathEntries = new[]
        {
            pythonDirectory,
            Path.Combine(pythonDirectory, "Scripts"),
            Path.Combine(pythonDirectory, "Library", "bin"),
            settings.ArcGisProBinDirectory,
            Path.Combine(getEnvironmentVariable("SystemRoot") ?? getEnvironmentVariable("WINDIR") ?? string.Empty, "System32")
        }.Where(entry => !string.IsNullOrWhiteSpace(entry)).Distinct(StringComparer.OrdinalIgnoreCase);
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathEntries);
        startInfo.Environment["CONDA_PREFIX"] = pythonDirectory;
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        return startInfo;
    }

    private static void CopyIfPresent(
        ProcessStartInfo startInfo,
        Func<string, string?> getEnvironmentVariable,
        string name)
    {
        var value = getEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }
}

public sealed class ArcPyProcessRunner
{
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(5);
    private readonly TimeProvider _timeProvider;

    public ArcPyProcessRunner(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ArcPyProcessResult> RunAsync(
        ArcPyExecutionSettings settings,
        ArcPyPreparedScript script,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(script);
        var startedAt = _timeProvider.GetTimestamp();
        if (cancellationToken.IsCancellationRequested)
            return Result(ArcPyProcessOutcome.Cancelled, null, string.Empty, string.Empty, false, false, startedAt, script.Sha256);

        Directory.CreateDirectory(settings.WorkingRoot);
        if ((File.GetAttributes(settings.WorkingRoot) & FileAttributes.ReparsePoint) != 0)
            throw new ArcPyPolicyException("arcpy_working_root_rejected", "The configured ArcPy working root cannot be a reparse point.");
        var runDirectory = Path.Combine(settings.WorkingRoot, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);
        var stableScriptPath = Path.Combine(runDirectory, "approved-script.py");
        try
        {
            await WriteStableScriptAsync(stableScriptPath, script.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryCleanRunDirectory(runDirectory, stableScriptPath);
            return Result(ArcPyProcessOutcome.Cancelled, null, string.Empty, string.Empty, false, false, startedAt, script.Sha256);
        }
        catch
        {
            TryCleanRunDirectory(runDirectory, stableScriptPath);
            throw;
        }

        using var process = new Process
        {
            StartInfo = ArcPyProcessStartInfoFactory.Create(
                settings,
                stableScriptPath,
                runDirectory,
                script.Arguments)
        };
        var standardOutput = new BoundedTextBuffer(settings.MaximumOutputCharacters);
        var standardError = new BoundedTextBuffer(settings.MaximumOutputCharacters);
        Task? outputTask = null;
        Task? errorTask = null;

        try
        {
            try
            {
                if (!process.Start())
                    return Result(ArcPyProcessOutcome.StartFailed, null, string.Empty, "ArcGIS Pro Python did not start.", false, false, startedAt, script.Sha256);
                process.StandardInput.Close();
                outputTask = DrainAsync(process.StandardOutput, standardOutput);
                errorTask = DrainAsync(process.StandardError, standardError);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _ = exception;
                return Result(ArcPyProcessOutcome.StartFailed, null, string.Empty, "ArcGIS Pro Python did not start.", false, false, startedAt, script.Sha256);
            }

            ArcPyProcessOutcome? interruptedOutcome = null;
            using (var timeout = new CancellationTokenSource(script.Timeout))
            using (var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                try
                {
                    await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    interruptedOutcome = cancellationToken.IsCancellationRequested
                        ? ArcPyProcessOutcome.Cancelled
                        : ArcPyProcessOutcome.TimedOut;
                    TryKillProcessTree(process);
                    await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
                }
            }

            await DrainAfterExitAsync(outputTask, errorTask).ConfigureAwait(false);
            var output = standardOutput.Snapshot();
            var error = standardError.Snapshot();
            int? exitCode = process.HasExited ? process.ExitCode : null;
            var outcome = interruptedOutcome ?? (exitCode == 0 ? ArcPyProcessOutcome.Succeeded : ArcPyProcessOutcome.Failed);
            return Result(outcome, exitCode, output.Text, error.Text, output.Truncated, error.Truncated, startedAt, script.Sha256);
        }
        finally
        {
            if (!HasExited(process)) TryKillProcessTree(process);
            TryCleanRunDirectory(runDirectory, stableScriptPath);
        }
    }

    private static async Task WriteStableScriptAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainAsync(StreamReader reader, BoundedTextBuffer output)
    {
        var buffer = new char[4_096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return;
            output.Append(buffer.AsSpan(0, read));
        }
    }

    private static async Task DrainAfterExitAsync(Task? outputTask, Task? errorTask)
    {
        if (outputTask is null || errorTask is null) return;
        var drain = Task.WhenAll(outputTask, errorTask);
        _ = await Task.WhenAny(drain, Task.Delay(CleanupWait)).ConfigureAwait(false);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            _ = await Task.WhenAny(
                process.WaitForExitAsync(CancellationToken.None),
                Task.Delay(CleanupWait)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
    }

    private static void TryCleanRunDirectory(string runDirectory, string stableScriptPath)
    {
        try
        {
            if (File.Exists(stableScriptPath)) File.Delete(stableScriptPath);
            Directory.Delete(runDirectory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ArcPyProcessResult Result(
        ArcPyProcessOutcome outcome,
        int? exitCode,
        string standardOutput,
        string standardError,
        bool standardOutputTruncated,
        bool standardErrorTruncated,
        long startedAt,
        string scriptSha256) =>
        new(
            outcome,
            exitCode,
            standardOutput,
            standardError,
            standardOutputTruncated,
            standardErrorTruncated,
            _timeProvider.GetElapsedTime(startedAt),
            scriptSha256);

    private sealed class BoundedTextBuffer(int maximumCharacters)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new(Math.Min(maximumCharacters, 16_384));
        private long _totalCharacters;

        public void Append(ReadOnlySpan<char> value)
        {
            lock (_gate)
            {
                _totalCharacters += value.Length;
                var remaining = maximumCharacters - _text.Length;
                if (remaining > 0) _text.Append(value[..Math.Min(remaining, value.Length)]);
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_gate)
            {
                return (_text.ToString(), _totalCharacters > _text.Length);
            }
        }
    }
}
