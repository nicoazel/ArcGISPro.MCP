using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Operations.Tests.Fakes;

/// <summary>The ArcGIS thread a fake service member is running on.</summary>
internal enum FakeThread
{
    None,
    MainCim,
    Ui
}

/// <summary>
/// Runs dispatched work inline but records which ArcGIS thread it stands in for, so fake services
/// can fail loudly when an operation calls them without marshalling to the thread the SDK needs.
/// </summary>
internal sealed class FakeDispatcher : IOperationDispatcher
{
    private static readonly AsyncLocal<FakeThread> Current = new();

    public static FakeThread CurrentThread => Current.Value;

    public int MainCimCalls { get; private set; }

    public int UiCalls { get; private set; }

    public static void Require(FakeThread thread, string member)
    {
        if (Current.Value != thread)
            throw new InvalidOperationException($"{member} must run on the {thread} thread but ran on {Current.Value}.");
    }

    public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MainCimCalls++;
        return Task.FromResult(Run(FakeThread.MainCim, action));
    }

    public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MainCimCalls++;
        Run(FakeThread.MainCim, () =>
        {
            action();
            return true;
        });
        return Task.CompletedTask;
    }

    public async Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UiCalls++;
        var previous = Current.Value;
        Current.Value = FakeThread.Ui;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Current.Value = previous;
        }
    }

    private static T Run<T>(FakeThread thread, Func<T> action)
    {
        var previous = Current.Value;
        Current.Value = thread;
        try
        {
            return action();
        }
        finally
        {
            Current.Value = previous;
        }
    }
}
