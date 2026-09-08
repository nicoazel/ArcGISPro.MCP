using System.Windows.Controls;

namespace ArcGISProMCP.AddIn;

/// <summary>
/// View-only shell for the MCP dockpane. All state and behavior are supplied by bindings.
/// </summary>
public partial class McpDockPaneView : UserControl
{
    public McpDockPaneView()
    {
        InitializeComponent();
    }
}
