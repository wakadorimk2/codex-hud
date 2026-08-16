using System.Diagnostics;
using System.Windows.Threading;
using CodexHud.Domain;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace CodexHud.Rendering;

public sealed class SkiaLampView : SKElement
{
    private readonly SkiaLampRenderer _renderer = new();
    private readonly DispatcherTimer _animationTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LampState _state = LampState.Idle;
    private LampState _fromState = LampState.Idle;
    private long _stateChangedTimestamp;

    public SkiaLampView()
    {
        _stateChangedTimestamp = Stopwatch.GetTimestamp();
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _animationTimer.Tick += OnAnimationTick;
        PaintSurface += OnPaintSurface;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LampState State
    {
        get => _state;
        set => SetVisualState(value);
    }

    public void SetVisualState(LampState state)
    {
        if (_state == state)
        {
            return;
        }

        _fromState = _state;
        _state = state;
        _stateChangedTimestamp = Stopwatch.GetTimestamp();
        UpdateAnimationTimer();
        InvalidateVisual();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var elapsed = GetElapsedSinceStateChange();
        var transitionProgress = Math.Clamp(
            (float)(elapsed.TotalSeconds / 0.24),
            0f,
            1f);
        var phase = (float)(_clock.Elapsed.TotalSeconds * 0.72);

        _renderer.Render(
            e.Surface.Canvas,
            e.Info.Width,
            e.Info.Height,
            _fromState,
            _state,
            transitionProgress,
            phase);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (GetElapsedSinceStateChange().TotalSeconds >= 0.24
            && _fromState != _state)
        {
            _fromState = _state;
        }

        UpdateAnimationTimer();
        InvalidateVisual();
    }

    private void UpdateAnimationTimer()
    {
        var transitionPending = _fromState != _state;
        var transitionActive = transitionPending
            && GetElapsedSinceStateChange().TotalSeconds < 0.24;
        var continuousAnimationActive = _state is LampState.Active or LampState.Listening;
        if (!transitionActive && !continuousAnimationActive)
        {
            _animationTimer.Stop();
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        _animationTimer.Start();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _animationTimer.Stop();
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateAnimationTimer();
        InvalidateVisual();
    }

    private TimeSpan GetElapsedSinceStateChange()
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - _stateChangedTimestamp;
        return TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
    }
}
