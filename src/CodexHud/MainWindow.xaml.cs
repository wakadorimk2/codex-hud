using System.Windows;
using System.Windows.Controls;
using CodexHud.Domain;
using CodexHud.Infrastructure;
using CodexHud.Rendering;

namespace CodexHud;

public partial class MainWindow : Window
{
    private const double MarginDip = 16;
    private readonly LampPositionStore _positionStore = new();
    private readonly Dictionary<string, Border> _lampCells = new(StringComparer.Ordinal);
    private bool _positionEditing;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        EditFrame.MouseLeftButtonDown += OnEditFrameMouseLeftButtonDown;
    }

    public void SetSessions(IReadOnlyList<SessionLampState> sessions)
    {
        var snapshot = sessions.ToArray();
        var activeSessionIds = snapshot
            .Select(session => session.SessionId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var sessionId in _lampCells.Keys
                     .Where(sessionId => !activeSessionIds.Contains(sessionId))
                     .ToArray())
        {
            LampPanel.Children.Remove(_lampCells[sessionId]);
            _lampCells.Remove(sessionId);
        }

        var layout = LampGroupLayout.Calculate(SystemParameters.WorkArea, snapshot.Length);
        LampPanel.Width = layout.Width;
        LampPanel.Height = layout.Height;

        for (var index = 0; index < snapshot.Length; index++)
        {
            var session = snapshot[index];
            if (!_lampCells.TryGetValue(session.SessionId, out var cell))
            {
                var lamp = new SkiaLampView();
                cell = new Border
                {
                    Width = LampGroupLayout.CellSize,
                    Height = LampGroupLayout.CellSize,
                    Child = lamp
                };
                _lampCells.Add(session.SessionId, cell);
            }

            if (cell.Child is SkiaLampView lampView)
            {
                lampView.State = session.State;
            }

            var row = index / layout.Columns;
            var column = index % layout.Columns;
            var itemsInRow = Math.Min(
                layout.Columns,
                snapshot.Length - row * layout.Columns);
            var rightGap = column < itemsInRow - 1
                ? LampGroupLayout.Gap
                : 0;
            var bottomGap = row < layout.Rows - 1
                ? LampGroupLayout.Gap
                : 0;
            cell.Margin = new Thickness(0, 0, rightGap, bottomGap);
        }

        LampPanel.Children.Clear();
        foreach (var session in snapshot)
        {
            LampPanel.Children.Add(_lampCells[session.SessionId]);
        }

        Width = Math.Max(1, layout.Width);
        Height = Math.Max(1, layout.Height);
        LampGroupSurface.Visibility = snapshot.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        EditFrame.Visibility = _positionEditing && snapshot.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (IsLoaded)
        {
            ClampPositionToWorkArea(save: true);
        }
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
        EditFrame.Visibility = _positionEditing && LampPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_positionEditing)
        {
            SavePosition();
        }
    }

    private void OnEditFrameMouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
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

    private void ClampPositionToWorkArea(bool save)
    {
        var position = LampPlacement.Clamp(
            SystemParameters.WorkArea,
            new Point(Left, Top),
            Width,
            Height);
        Left = position.X;
        Top = position.Y;
        if (save)
        {
            _positionStore.TrySave(position);
        }
    }

    private void SavePosition()
    {
        ClampPositionToWorkArea(save: true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SavePosition();
        WindowInterop.ReleaseHudWindow(this);
    }
}
