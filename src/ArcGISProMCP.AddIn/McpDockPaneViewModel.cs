using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using ArcGISProMCP.AddIn.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ArcGISProMCP.AddIn
{

internal class McpDockPaneViewModel : DockPane
{
    internal const string DockPaneId = "ArcGISProMCP_AddIn_DockPane";

    private readonly IPanelStateSource _source;
    private readonly AsyncCommand _connectCommand;
    private readonly AsyncCommand _disconnectCommand;
    private readonly AsyncCommand _refreshCommand;
    private readonly AsyncCommand _openProjectCommand;
    private readonly AsyncCommand _captureVisualCommand;
    private readonly AsyncCommand _revealVisualCommand;
    private readonly AsyncCommand _clearVisualCueCommand;
    private readonly AsyncCommand _cancelOperationCommand;
    private readonly AsyncCommand _openSettingsCommand;
    private bool _isActive;
    private string _errorMessage = string.Empty;

    protected McpDockPaneViewModel()
        : this(PanelStateSourceProvider.Create())
    {
    }

    internal McpDockPaneViewModel(IPanelStateSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        _connectCommand = Command(_source.ConnectAsync, () => Connection?.CanConnect == true);
        _disconnectCommand = Command(_source.DisconnectAsync, () => Connection?.CanDisconnect == true);
        _refreshCommand = Command(_source.RefreshAsync);
        _openProjectCommand = Command(_source.OpenProjectAsync);
        _captureVisualCommand = Command(_source.CaptureVisualAsync, () => VisualEvidence?.IsCapturing != true);
        _revealVisualCommand = Command(_source.RevealVisualAsync, () => VisualEvidence?.HasEvidence == true);
        _clearVisualCueCommand = Command(_source.ClearVisualCueAsync, () => VisualEvidence?.HasActiveCue == true);
        _cancelOperationCommand = Command(_source.CancelCurrentOperationAsync, () => CanCancelOperation);
        _openSettingsCommand = Command(_source.OpenSettingsAsync);

        Connection = new ConnectionHeaderViewModel(_connectCommand, _disconnectCommand);
        Workspace = new WorkspaceContextViewModel(
            Command(
                cancellationToken => Workspace?.SelectedMap is { } map
                    ? _source.ActivateMapAsync(map.Id, cancellationToken)
                    : Task.CompletedTask,
                () => Workspace?.SelectedMap is not null),
            Command(
                cancellationToken => Workspace?.SelectedLayout is { } layout
                    ? _source.ActivateLayoutAsync(layout.Id, cancellationToken)
                    : Task.CompletedTask,
                () => Workspace?.SelectedLayout is not null));
        Approvals = new ApprovalQueueViewModel(ResolveApprovalAsync, ReportError);
        VisualEvidence = new VisualPreviewViewModel(
            _captureVisualCommand,
            _revealVisualCommand,
            _clearVisualCueCommand);
        Activity = new ActivityFeedViewModel();
        Workflows = new WorkflowShelfViewModel(
            Command(_source.SaveWorkflowAsync),
            Command(
                cancellationToken => Workflows?.SelectedWorkflow is { } workflow
                    ? _source.ReplayWorkflowAsync(workflow.Id, cancellationToken)
                    : Task.CompletedTask,
                () => Workflows?.SelectedWorkflow?.CanReplay == true));

        Apply(_source.Current);
    }

    public ConnectionHeaderViewModel Connection { get; }
    public WorkspaceContextViewModel Workspace { get; }
    public ApprovalQueueViewModel Approvals { get; }
    public VisualPreviewViewModel VisualEvidence { get; }
    public ActivityFeedViewModel Activity { get; }
    public WorkflowShelfViewModel Workflows { get; }

    public ICommand RefreshCommand => _refreshCommand;
    public ICommand OpenProjectCommand => _openProjectCommand;
    public ICommand CancelOperationCommand => _cancelOperationCommand;
    public ICommand OpenSettingsCommand => _openSettingsCommand;

    public bool CanCancelOperation { get; private set; }
    public int SkillCount { get; private set; }
    public string FooterStatus { get; private set; } = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value, () => ErrorMessage))
                NotifyPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    internal static void Show()
    {
        FrameworkApplication.DockPaneManager.Find(DockPaneId)?.Activate();
    }

    protected override void OnShow(bool isVisible)
    {
        base.OnShow(isVisible);

        if (isVisible && !_isActive)
        {
            _isActive = true;
            _source.StateChanged += OnStateChanged;
            _refreshCommand.Execute(null);
        }
        else if (!isVisible && _isActive)
        {
            _isActive = false;
            _source.StateChanged -= OnStateChanged;
        }
    }

    private AsyncCommand Command(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null) => new(execute, ReportError, canExecute);

    private Task ResolveApprovalAsync(
        string approvalId,
        ApprovalDecision decision,
        CancellationToken cancellationToken) =>
        _source.ResolveApprovalAsync(approvalId, decision, cancellationToken);

    private void OnStateChanged(object? sender, PanelStateSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        _ = dispatcher.InvokeAsync(() => Apply(snapshot));
    }

    private void Apply(PanelStateSnapshot snapshot)
    {
        ErrorMessage = string.Empty;
        Connection.Apply(snapshot.Connection);
        Workspace.Apply(snapshot.Workspace);
        Approvals.Apply(snapshot.Approvals);
        VisualEvidence.Apply(snapshot.VisualEvidence);
        Activity.Apply(snapshot.Activity);
        Workflows.Apply(snapshot.Workflows);

        CanCancelOperation = snapshot.CanCancelOperation;
        SkillCount = snapshot.SkillCount;
        FooterStatus = snapshot.FooterStatus;
        NotifyPropertyChanged(nameof(CanCancelOperation));
        NotifyPropertyChanged(nameof(SkillCount));
        NotifyPropertyChanged(nameof(FooterStatus));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _connectCommand.RaiseCanExecuteChanged();
        _disconnectCommand.RaiseCanExecuteChanged();
        _refreshCommand.RaiseCanExecuteChanged();
        _openProjectCommand.RaiseCanExecuteChanged();
        _captureVisualCommand.RaiseCanExecuteChanged();
        _revealVisualCommand.RaiseCanExecuteChanged();
        _clearVisualCueCommand.RaiseCanExecuteChanged();
        _cancelOperationCommand.RaiseCanExecuteChanged();
        _openSettingsCommand.RaiseCanExecuteChanged();
        Workspace.RaiseCommandStates();
        Workflows.RaiseCommandStates();
    }

    private void ReportError(Exception exception)
    {
        ErrorMessage = exception.Message;
    }
}
}

namespace ArcGISProMCP.AddIn.UI
{

internal abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class ConnectionHeaderViewModel : ObservableViewModel
{
    private ConnectionStatus _status;
    private string _statusText = string.Empty;
    private string _endpoint = string.Empty;
    private string _sessionText = string.Empty;
    private bool _isBusy;

    internal ConnectionHeaderViewModel(ICommand connectCommand, ICommand disconnectCommand)
    {
        ConnectCommand = connectCommand;
        DisconnectCommand = disconnectCommand;
    }

    public ConnectionStatus Status { get => _status; private set => Set(ref _status, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Endpoint { get => _endpoint; private set => Set(ref _endpoint, value); }
    public string SessionText { get => _sessionText; private set => Set(ref _sessionText, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool CanConnect => Status is ConnectionStatus.Disconnected or ConnectionStatus.Faulted;
    public bool CanDisconnect => Status is not ConnectionStatus.Disconnected;
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }

    internal void Apply(ConnectionSnapshot snapshot)
    {
        Status = snapshot.Status;
        StatusText = snapshot.StatusText;
        Endpoint = snapshot.Endpoint;
        SessionText = snapshot.SessionText;
        IsBusy = snapshot.IsBusy;
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }
}

internal sealed class WorkspaceContextViewModel : ObservableViewModel
{
    private string _projectName = string.Empty;
    private string _contextSummary = string.Empty;
    private PanelChoice? _selectedMap;
    private PanelChoice? _selectedLayout;

    internal WorkspaceContextViewModel(AsyncCommand activateMapCommand, AsyncCommand activateLayoutCommand)
    {
        ActivateMapCommand = activateMapCommand;
        ActivateLayoutCommand = activateLayoutCommand;
    }

    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }
    public string ContextSummary { get => _contextSummary; private set => Set(ref _contextSummary, value); }
    public ObservableCollection<PanelChoice> Maps { get; } = new();
    public ObservableCollection<PanelChoice> Layouts { get; } = new();
    public PanelChoice? SelectedMap
    {
        get => _selectedMap;
        set
        {
            if (Set(ref _selectedMap, value))
                ActivateMapCommand.RaiseCanExecuteChanged();
        }
    }

    public PanelChoice? SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (Set(ref _selectedLayout, value))
                ActivateLayoutCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand ActivateMapCommand { get; }
    public AsyncCommand ActivateLayoutCommand { get; }

    internal void Apply(WorkspaceSnapshot snapshot)
    {
        ProjectName = snapshot.ProjectName;
        ContextSummary = snapshot.ContextSummary;
        Replace(Mans: Maps, values: snapshot.Maps);
        Replace(Mans: Layouts, values: snapshot.Layouts);
        SelectedMap = Maps.FirstOrDefault(item => item.Id == snapshot.ActiveMapId);
        SelectedLayout = Layouts.FirstOrDefault(item => item.Id == snapshot.ActiveLayoutId);
    }

    internal void RaiseCommandStates()
    {
        ActivateMapCommand.RaiseCanExecuteChanged();
        ActivateLayoutCommand.RaiseCanExecuteChanged();
    }

    private static void Replace(ObservableCollection<PanelChoice> Mans, IEnumerable<PanelChoice> values)
    {
        Mans.Clear();
        foreach (var value in values)
            Mans.Add(value);
    }
}

internal sealed class ApprovalQueueViewModel : ObservableViewModel
{
    private readonly Func<string, ApprovalDecision, CancellationToken, Task> _resolve;
    private readonly Action<Exception> _reportError;

    internal ApprovalQueueViewModel(
        Func<string, ApprovalDecision, CancellationToken, Task> resolve,
        Action<Exception> reportError)
    {
        _resolve = resolve;
        _reportError = reportError;
    }

    public ObservableCollection<ApprovalItemViewModel> Items { get; } = new();
    public int PendingCount => Items.Count;
    public bool HasPending => Items.Count > 0;
    public string Heading => HasPending ? $"Approval required · {PendingCount}" : "Approvals · none pending";

    internal void Apply(IEnumerable<ApprovalSnapshot> approvals)
    {
        Items.Clear();
        foreach (var approval in approvals)
            Items.Add(new ApprovalItemViewModel(approval, _resolve, _reportError));

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(Heading));
    }
}

internal sealed class ApprovalItemViewModel : ObservableViewModel
{
    private bool _isExpanded;

    internal ApprovalItemViewModel(
        ApprovalSnapshot snapshot,
        Func<string, ApprovalDecision, CancellationToken, Task> resolve,
        Action<Exception> reportError)
    {
        Id = snapshot.Id;
        OperationId = snapshot.OperationId;
        OperationVersion = snapshot.OperationVersion;
        ToolName = snapshot.ToolName;
        Summary = snapshot.Summary;
        WorkspaceRevision = snapshot.WorkspaceRevision;
        ArgumentsPreview = snapshot.ArgumentsPreview;
        Risk = snapshot.Risk;
        RequestedAtText = snapshot.RequestedAtText;
        ExpiresAtText = snapshot.ExpiresAtText;
        IsDeciding = snapshot.IsDeciding;
        ApproveCommand = new AsyncCommand(
            token => resolve(Id, ApprovalDecision.ApproveOnce, token),
            reportError,
            () => !IsDeciding);
        RejectCommand = new AsyncCommand(
            token => resolve(Id, ApprovalDecision.Reject, token),
            reportError,
            () => !IsDeciding);
    }

    public string Id { get; }
    public string OperationId { get; }
    public string OperationVersion { get; }
    public string ToolName { get; }
    public string Summary { get; }
    public string WorkspaceRevision { get; }
    public string ArgumentsPreview { get; }
    public ApprovalRisk Risk { get; }
    public string RequestedAtText { get; }
    public string ExpiresAtText { get; }
    public bool IsDeciding { get; }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
}

internal sealed class VisualPreviewViewModel : ObservableViewModel
{
    private string? _imagePath;
    private string _caption = string.Empty;
    private string _capturedAtText = string.Empty;
    private string _contextText = string.Empty;
    private bool _isCapturing;
    private bool _hasActiveCue;

    internal VisualPreviewViewModel(ICommand capture, ICommand reveal, ICommand clearCue)
    {
        CaptureCommand = capture;
        RevealCommand = reveal;
        ClearCueCommand = clearCue;
    }

    public string? ImagePath { get => _imagePath; private set { if (Set(ref _imagePath, value)) OnPropertyChanged(nameof(HasEvidence)); } }
    public string Caption { get => _caption; private set => Set(ref _caption, value); }
    public string CapturedAtText { get => _capturedAtText; private set => Set(ref _capturedAtText, value); }
    public string ContextText { get => _contextText; private set => Set(ref _contextText, value); }
    public bool IsCapturing { get => _isCapturing; private set => Set(ref _isCapturing, value); }
    public bool HasActiveCue { get => _hasActiveCue; private set => Set(ref _hasActiveCue, value); }
    public bool HasEvidence => !string.IsNullOrWhiteSpace(ImagePath);
    public ICommand CaptureCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand ClearCueCommand { get; }

    internal void Apply(VisualEvidenceSnapshot snapshot)
    {
        ImagePath = snapshot.ImagePath;
        Caption = snapshot.Caption;
        CapturedAtText = snapshot.CapturedAtText;
        ContextText = snapshot.ContextText;
        IsCapturing = snapshot.IsCapturing;
        HasActiveCue = snapshot.HasActiveCue;
    }
}

internal sealed class ActivityFeedViewModel : ObservableViewModel
{
    public ObservableCollection<ActivitySnapshot> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;

    internal void Apply(IEnumerable<ActivitySnapshot> items)
    {
        Items.Clear();
        foreach (var item in items.TakeLast(500))
            Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }
}

internal sealed class WorkflowShelfViewModel : ObservableViewModel
{
    private WorkflowSnapshot? _selectedWorkflow;

    internal WorkflowShelfViewModel(AsyncCommand saveCommand, AsyncCommand replayCommand)
    {
        SaveCommand = saveCommand;
        ReplayCommand = replayCommand;
    }

    public ObservableCollection<WorkflowSnapshot> Items { get; } = new();
    public int Count => Items.Count;
    public WorkflowSnapshot? SelectedWorkflow
    {
        get => _selectedWorkflow;
        set
        {
            if (Set(ref _selectedWorkflow, value))
                ReplayCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ReplayCommand { get; }

    internal void Apply(IEnumerable<WorkflowSnapshot> workflows)
    {
        var selectedId = SelectedWorkflow?.Id;
        Items.Clear();
        foreach (var workflow in workflows)
            Items.Add(workflow);
        SelectedWorkflow = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(Count));
    }

    internal void RaiseCommandStates()
    {
        SaveCommand.RaiseCanExecuteChanged();
        ReplayCommand.RaiseCanExecuteChanged();
    }
}

internal sealed class AsyncCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Action<Exception> _reportError;
    private readonly Func<bool> _canExecute;
    private bool _isRunning;

    internal AsyncCommand(
        Func<CancellationToken, Task> execute,
        Action<Exception> reportError,
        Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _canExecute = canExecute ?? (() => true);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && _canExecute();

    public void Execute(object? parameter) => _ = ExecuteAsync();

    internal void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;

        _isRunning = true;
        using var executionCancellation = new CancellationTokenSource();
        RaiseCanExecuteChanged();

        try
        {
            await _execute(executionCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _reportError(exception);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }
}
}
