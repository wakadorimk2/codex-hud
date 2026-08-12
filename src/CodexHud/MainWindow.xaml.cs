using System.Windows;
using CodexHud.Infrastructure;

namespace CodexHud;

public partial class MainWindow : Window
{
    private const double MarginDip = 16;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public void SetState(Domain.LampState state)
    {
        LampSurface.State = state;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowInterop.ConfigureClickThrough(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var position = LampPlacement.Calculate(
            SystemParameters.WorkArea,
            Width,
            Height,
            MarginDip);
        Left = position.X;
        Top = position.Y;
    }
}
