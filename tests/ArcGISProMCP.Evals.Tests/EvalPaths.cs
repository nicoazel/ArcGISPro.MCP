using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

internal static class EvalPaths
{
    public static string RepositoryRoot { get; } = EvalEnvironment.FindRepositoryRoot();

    public static string Evals => Path.Combine(RepositoryRoot, "evals");

    /// <summary>Every add-in operation descriptor, dumped from the built add-in (owned by Operations.Tests).</summary>
    public const string DescriptorDumpRelative = "tests/ArcGISProMCP.Operations.Tests/Fixtures/operation-descriptors.json";

    public static string DescriptorDump => Path.Combine(RepositoryRoot, DescriptorDumpRelative);

    public static string Baseline => Path.Combine(Evals, "baseline.json");

    public static string Tasks(string name) => Path.Combine(Evals, "tasks", name);

    /// <summary>The synthetic toolbox root shared with Core.Tests (copied to the output directory).</summary>
    public static string FixtureToolboxRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "toolboxes");

    public static string InstalledToolboxRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "ArcGIS", "Pro", "Resources", "ArcToolBox", "toolboxes");
}
