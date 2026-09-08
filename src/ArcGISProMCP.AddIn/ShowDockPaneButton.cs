using ArcGIS.Desktop.Framework.Contracts;

namespace ArcGISProMCP.AddIn;

internal sealed class ShowDockPaneButton : Button
{
    protected override void OnClick() => McpDockPaneViewModel.Show();
}
