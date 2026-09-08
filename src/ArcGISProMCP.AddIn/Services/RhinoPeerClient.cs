using System.Reflection;
using System.Text.Json;

namespace ArcGISProMCP.AddIn.Services;

internal static class RhinoPeerClient
{
    private const string TypeName = "RhinoInside.ArcGISPro.RhinoMcpPeer";

    public static bool IsAvailable => ResolveStateMethod() is not null;
    public static bool CanInvoke => ResolveInvokeMethod() is not null;

    public static JsonElement? TryGetState()
    {
        var method = ResolveStateMethod();
        if (method is null) return null;
        try
        {
            var json = method.Invoke(null, null) as string;
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException("The Rhino.Inside peer could not provide state.", exception.InnerException);
        }
    }

    public static async Task<JsonElement> InvokeAsync(
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = ResolveInvokeMethod()
            ?? throw new InvalidOperationException(
                "The loaded Rhino.Inside peer does not expose the safe interop contract. Rebuild and reload Rhino.Inside-ArcGIS.");

        Task<string> invocation;
        try
        {
            // The reflected peer contract cannot accept cancellation. Once invoked, drain it to
            // completion so a caller never observes cancellation while Rhino is still mutating.
            cancellationToken.ThrowIfCancellationRequested();
            invocation = method.Invoke(null, [operation, arguments.GetRawText()]) as Task<string>
                ?? throw new InvalidOperationException("The Rhino.Inside peer returned an invalid asynchronous result.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"The Rhino.Inside peer could not begin operation '{operation}'.",
                exception.InnerException);
        }

        string json;
        try
        {
            json = await invocation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The Rhino.Inside peer operation '{operation}' failed: {exception.Message}",
                exception);
        }

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"The Rhino.Inside peer operation '{operation}' returned no result.");

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The Rhino.Inside peer operation '{operation}' returned invalid JSON.",
                exception);
        }
    }

    private static MethodInfo? ResolveStateMethod() => ResolveType()
        ?.GetMethod(
            "GetStateJson",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

    private static MethodInfo? ResolveInvokeMethod() => ResolveType()
        ?.GetMethod(
            "InvokeAsync",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null);

    private static Type? ResolveType() => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType(TypeName, false, false))
        .FirstOrDefault(type => type is not null);
}
