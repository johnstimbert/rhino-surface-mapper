using System.Windows;

namespace RhinoSurfaceMapper.Desktop;

/// <summary>
/// Placeholder main window for Phase 0. The real shell (<c>BlazorWebView</c> + map canvas)
/// arrives in Phase 3; for now this plain window only proves the WPF host starts and a
/// <see cref="Window"/> can be shown once logging/DI are wired.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes the window's XAML-defined components.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
