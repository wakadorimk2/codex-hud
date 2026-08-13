using System.Windows;
using CodexHud.Infrastructure;

namespace CodexHud;

public partial class MainWindow : Window
{
    private const double MarginDip = 16;
    private readonly LampPositionStore _positionStore = new();
    private bool _positionEditing;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        EditFrame.MouseLeftButtonDown += OnEditFrameMouseLeftButtonDown;
    }

    public void SetState(Domain.LampState state)
    {
        LampSurface.State = state;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowInterop.ConfigureHudWindow(this, TogglePositionEditing);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var position = _positionStore.Load()
            ?? LampPlacement.Calculate(SystemParameters.WorkArea, Width, Height, MarginDip);
        position = LampPlacement.Clamp(SystemParameters.WorkArea, position, Width, Height);
        Left = position.X;
        Top = position.Y;
    }

    private void TogglePositionEditing()
    {
        _positionEditing = !_positionEditing;
        WindowInterop.SetPositionEditing(this, _positionEditing);
        EditFrame.Visibility = _positionEditing
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_positionEditing)
        {
            SavePosition();
        }
    }

    private void OnEditFrameMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_positionEditing)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The window can receive a delayed mouse event while edit mode changes.
        }

        SavePosition();
        e.Handled = true;
    }

    private void SavePosition()
    {
        var position = LampPlacement.Clamp(
            SystemParameters.WorkArea,
            new Point(Left, Top),
            Width,
            Height);
        Left = position.X;
        Top = position.Y;
        _positionStore.TrySave(position);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SavePosition();
        WindowInterop.ReleaseHudWindow(this);
    }
}
