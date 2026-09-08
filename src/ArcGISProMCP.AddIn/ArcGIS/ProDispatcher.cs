using System.Windows;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.ArcGIS;

internal sealed class ProDispatcher : IOperationDispatcher
{
    public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return QueuedTask.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return action(); });
    }

    public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return QueuedTask.Run(() => { cancellationToken.ThrowIfCancellationRequested(); action(); });
    }

    public async Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) return await action().ConfigureAwait(true);

        var operation = dispatcher.InvokeAsync(() => { cancellationToken.ThrowIfCancellationRequested(); return action(); });
        return await operation.Task.Unwrap().ConfigureAwait(false);
    }
}
