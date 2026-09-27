using System.Security.Cryptography;
using System.Text;
using ArcGISProMCP.Core.Execution;

namespace ArcGISProMCP.Operations.Tests.Fakes;

/// <summary>
/// A throwaway ArcGIS Pro Python layout: an empty python.exe (it cannot start), the arcpy package
/// marker, a script root and a working root, so ArcPy settings validate without ArcGIS Pro.
/// </summary>
internal sealed class ArcPyRuntimeFixture : IDisposable
{
    public ArcPyRuntimeFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"ArcGISProMCP-OpsArcPy-{Guid.NewGuid():N}");
        var proBin = Path.Combine(Root, "ArcGIS", "Pro", "bin");
        var python = Path.Combine(proBin, "Python", "envs", "arcgispro-py3", "python.exe");
        var marker = Path.Combine(Path.GetDirectoryName(python)!, "Lib", "site-packages", "arcpy", "__init__.py");
        ScriptRoot = Path.Combine(Root, "scripts");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        Directory.CreateDirectory(ScriptRoot);
        File.WriteAllText(python, string.Empty);
        File.WriteAllText(marker, string.Empty);
        Settings = ArcPyExecutionSettings.Create(true, python, proBin, ScriptRoot, Path.Combine(Root, "runs"));
    }

    public string Root { get; }

    public string ScriptRoot { get; }

    public ArcPyExecutionSettings Settings { get; }

    /// <summary>Writes a script under the script root and returns its SHA-256 (upper-case hex).</summary>
    public string WriteScript(string relativePath, string content)
    {
        var path = Path.Combine(ScriptRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

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
