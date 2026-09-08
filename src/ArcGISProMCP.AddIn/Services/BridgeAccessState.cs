namespace ArcGISProMCP.AddIn.Services;

internal sealed class BridgeAccessState
{
    private int _enabled = 1;
    internal bool Enabled
    {
        get => Volatile.Read(ref _enabled) == 1;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }
}
