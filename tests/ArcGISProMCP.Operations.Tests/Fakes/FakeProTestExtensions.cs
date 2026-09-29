using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Operations.Tests.Fakes;

/// <summary>Binds <see cref="FakePro"/> calls to the running test's cancellation token.</summary>
internal static class FakeProTestExtensions
{
    /// <summary>Runs one operation directly, bypassing executor policy (revisions, confirmation).</summary>
    public static Task<OperationResult> RunAsync(this FakePro pro, string id, string argumentsJson = "{}") =>
        pro.RunDirectAsync(id, argumentsJson, TestContext.Current.CancellationToken);

    /// <summary>Runs one operation through the real executor with the current revision and a valid approval.</summary>
    public static Task<OperationResult> InvokeAsync(this FakePro pro, string id, string argumentsJson = "{}") =>
        pro.InvokeApprovedAsync(id, argumentsJson, TestContext.Current.CancellationToken);
}
