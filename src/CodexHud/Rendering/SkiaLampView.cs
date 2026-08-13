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
    private LampAppearance _appearance = LampAppearance.Default;
    private LampAppearance _fromAppearance = LampAppearance.Default;
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
        set => SetVisualState(value, _appearance);
    }

    public LampAppearance Appearance
    {
        get => _appearance;
        set => SetVisualState(_state, value);
    }

    public void SetVisualState(LampState state, LampAppearance appearance)
    {
        if (_state == state && _appearance == appearance)
        {
            return;
        }

        _fromState = _state;
        _fromAppearance = _appearance;
        _state = state;
        _appearance = appearance;
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
        var phaseSpeed = _appearance == LampAppearance.PlanQuestion
            ? 0.36
            : 0.72;
        var phase = (float)(_clock.Elapsed.TotalSeconds * phaseSpeed);

        _renderer.Render(
            e.Surface.Canvas,
            e.Info.Width,
            e.Info.Height,
            _fromState,
            _state,
            _fromAppearance,
            _appearance,
            transitionProgress,
            phase);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (GetElapsedSinceStateChange().TotalSeconds >= 0.24
            && (_fromState != _state || _fromAppearance != _appearance))
        {
            _fromState = _state;
            _fromAppearance = _appearance;
        }

        UpdateAnimationTimer();
        InvalidateVisual();
    }

    private void UpdateAnimationTimer()
    {
        var transitionPending = _fromState != _state
            || _fromAppearance != _appearance;
        var transitionActive = transitionPending
            && GetElapsedSinceStateChange().TotalSeconds < 0.24;
        var continuousAnimationActive = _state != LampState.Idle
            && _appearance != LampAppearance.Muted;
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
