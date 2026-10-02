using System.Windows;

namespace RhinoSurfaceMapper.Desktop;

/// <summary>
/// Hosts the read-only map canvas: a single <c>BlazorWebView</c> (declared in
/// <c>MainWindow.xaml</c>) rooted at <c>RhinoSurfaceMapper.UI.Components.Layout.Routes</c>, which
/// in turn composes <c>MainLayout</c> around <c>MapCanvas</c>. No navigation, dialogs, or options
/// panel yet — those are later phases.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes the window's XAML-defined components, including the <c>BlazorWebView</c>.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
