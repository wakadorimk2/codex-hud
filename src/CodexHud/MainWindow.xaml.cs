using System.Windows;
using CodexHud.Infrastructure;

namespace CodexHud;

public partial class MainWindow : Window
{
    private const double MarginDip = 16;
    private readonly LampPositionStore _positionStore = new();
    private bool _positionEditing;
    private bool _dragging;
    private Vector _dragOffset;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        EditFrame.MouseLeftButtonDown += OnEditFrameMouseLeftButtonDown;
        EditFrame.MouseMove += OnEditFrameMouseMove;
        EditFrame.MouseLeftButtonUp += OnEditFrameMouseLeftButtonUp;
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
            StopDragging();
            SavePosition();
        }
    }

    private void OnEditFrameMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_positionEditing)
        {
            return;
        }

        var mousePosition = e.GetPosition(null);
        _dragOffset = mousePosition - new Point(Left, Top);
        _dragging = true;
        EditFrame.CaptureMouse();
        e.Handled = true;
    }

    private void OnEditFrameMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_positionEditing || !_dragging)
        {
            return;
        }

        var mousePosition = e.GetPosition(null);
        var position = new Point(
            mousePosition.X - _dragOffset.X,
            mousePosition.Y - _dragOffset.Y);
        var clampedPosition = LampPlacement.Clamp(
            SystemParameters.WorkArea,
            position,
            Width,
            Height);
        Left = clampedPosition.X;
        Top = clampedPosition.Y;
        e.Handled = true;
    }

    private void OnEditFrameMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        StopDragging();
        SavePosition();
        e.Handled = true;
    }

    private void StopDragging()
    {
        _dragging = false;
        if (EditFrame.IsMouseCaptured)
        {
            EditFrame.ReleaseMouseCapture();
        }
    }

    private void SavePosition()
    {
        var position = LampPlacement.Clamp(
            SystemParameters.WorkArea,
            new Point(Left, Top),
            Width,
            Height);
        _positionStore.TrySave(position);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopDragging();
        SavePosition();
        WindowInterop.ReleaseHudWindow(this);
    }
}
