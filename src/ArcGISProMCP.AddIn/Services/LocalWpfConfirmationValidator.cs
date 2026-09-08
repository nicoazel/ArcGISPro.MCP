using System.Text.Json;
using System.Windows;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.AddIn.Services;

/// <summary>
/// Requires a fresh, local Yes/No decision for every confirmed invocation.
/// The caller must explicitly request an interactive prompt; tokens never cache approval.
/// </summary>
internal sealed class LocalWpfConfirmationValidator(
    IOperationDispatcher dispatcher,
    IWorkspaceStateProvider workspaceState) : IConfirmationValidator
{
    private const string InteractiveToken = "interactive";
    private const int MaximumArgumentCharacters = 2_000;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private int _promptActive;

    public async ValueTask<bool> IsValidAsync(
        string token,
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(token, InteractiveToken, StringComparison.Ordinal)) return false;

        var beforePrompt = await workspaceState.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(beforePrompt.Revision, workspace.Revision, StringComparison.Ordinal)) return false;

        // Reject concurrent confirmation attempts instead of stacking modal dialogs.
        if (Interlocked.CompareExchange(ref _promptActive, 1, 0) != 0) return false;

        try
        {
            var message = BuildMessage(descriptor, arguments, workspace);
            var approved = await dispatcher.OnUiThreadAsync(
                () => Task.FromResult(ShowConfirmation(message)),
                CancellationToken.None).ConfigureAwait(false);

            // The prompt itself is deliberately not abandoned: hold the single-prompt guard until
            // the actual modal closes, then deny if the originating request was cancelled.
            if (cancellationToken.IsCancellationRequested || !approved) return false;

            // A modal prompt creates a time window in which project or view state can change.
            // Never apply approval to a different workspace revision than the one displayed.
            var afterPrompt = await workspaceState.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            return !cancellationToken.IsCancellationRequested &&
                string.Equals(afterPrompt.Revision, workspace.Revision, StringComparison.Ordinal);
        }
        finally
        {
            Volatile.Write(ref _promptActive, 0);
        }
    }

    private static bool ShowConfirmation(string message)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(
                message,
                "Confirm ArcGIS Pro MCP operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No,
                MessageBoxOptions.None)
            : MessageBox.Show(
                owner,
                message,
                "Confirm ArcGIS Pro MCP operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No,
                MessageBoxOptions.None);
        return result == MessageBoxResult.Yes;
    }

    private static string BuildMessage(
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace)
    {
        var argumentText = JsonSerializer.Serialize(arguments, IndentedJson);
        if (argumentText.Length > MaximumArgumentCharacters)
            argumentText = argumentText[..MaximumArgumentCharacters] + Environment.NewLine + "… (arguments truncated)";

        return $"""
            ArcGIS Pro MCP is requesting permission to run one operation.

            Operation: {descriptor.Title}
            ID: {descriptor.Id}
            Risk: {descriptor.Risk}
            Workspace revision: {workspace.Revision}

            Arguments:
            {argumentText}

            Run this operation once?
            """;
    }
}
