using CodexHud.Domain;
using SkiaSharp;

namespace CodexHud.Rendering;

public sealed class SkiaLampRenderer
{
    private const float OuterAmbientAlpha = 44f;

    public void Render(
        SKCanvas canvas,
        int pixelWidth,
        int pixelHeight,
        LampState fromState,
        LampState toState,
        LampAppearance fromAppearance,
        LampAppearance toAppearance,
        float transitionProgress,
        float phase)
    {
        canvas.Clear(SKColors.Transparent);

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        var dimension = MathF.Min(pixelWidth, pixelHeight);
        var center = new SKPoint(pixelWidth / 2f, pixelHeight / 2f);
        var stateProgress = SmoothStep(Math.Clamp(transitionProgress, 0f, 1f));
        var color = Lerp(
            GetStateColor(fromState, fromAppearance),
            GetStateColor(toState, toAppearance),
            stateProgress);
        var motion = GetMotion(toState, toAppearance, phase);

        DrawAmbient(canvas, center, dimension, color);
        DrawStateGlow(canvas, center, dimension, color, motion);
        DrawMiddleRing(canvas, center, dimension, color, motion);
        DrawCore(canvas, center, dimension, color, motion);
        DrawResidualRing(
            canvas,
            center,
            dimension,
            color,
            toState,
            toAppearance,
            phase);
    }

    private static void DrawAmbient(SKCanvas canvas, SKPoint center, float dimension, SKColor color)
    {
        var radius = dimension * 0.49f;
        using var shader = SKShader.CreateRadialGradient(
            center,
            radius,
            new[]
            {
                color.WithAlpha((byte)(OuterAmbientAlpha * 0.65f)),
                color.WithAlpha((byte)OuterAmbientAlpha),
                color.WithAlpha(0)
            },
            new[] { 0f, 0.58f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = shader
        };

        canvas.DrawCircle(center, radius, paint);
    }

    private static void DrawStateGlow(
        SKCanvas canvas,
        SKPoint center,
        float dimension,
        SKColor color,
        float motion)
    {
        var radius = dimension * (0.36f + motion * 0.02f);
        using var shader = SKShader.CreateRadialGradient(
            center,
            radius,
            new[]
            {
                color.WithAlpha(220),
                color.WithAlpha(126),
                color.WithAlpha(0)
            },
            new[] { 0f, 0.52f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = shader
        };

        canvas.DrawCircle(center, radius, paint);
    }

    private static void DrawMiddleRing(
        SKCanvas canvas,
        SKPoint center,
        float dimension,
        SKColor color,
        float motion)
    {
        var radius = dimension * (0.305f + motion * 0.02f);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, dimension * 0.035f),
            Color = color.WithAlpha(178)
        };

        canvas.DrawCircle(center, radius, paint);
    }

    private static void DrawCore(
        SKCanvas canvas,
        SKPoint center,
        float dimension,
        SKColor color,
        float motion)
    {
        var radius = dimension * (0.225f + motion * 0.014f);
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(center.X - radius * 0.28f, center.Y - radius * 0.28f),
            radius * 1.28f,
            new[]
            {
                SKColors.White.WithAlpha(235),
                color.WithAlpha(248),
                color.WithAlpha(188)
            },
            new[] { 0f, 0.28f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = shader
        };

        canvas.DrawCircle(center, radius, paint);
    }

    private static void DrawResidualRing(
        SKCanvas canvas,
        SKPoint center,
        float dimension,
        SKColor color,
        LampState state,
        LampAppearance appearance,
        float phase)
    {
        if (appearance == LampAppearance.Muted)
        {
            return;
        }

        if (state == LampState.Idle)
        {
            return;
        }

        var radius = dimension * 0.385f;
        var startAngle = phase * (state == LampState.NeedsAttention ? 32f : 48f) - 90f;
        var sweepAngle = state == LampState.NeedsAttention ? 142f : 76f;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = MathF.Max(1f, dimension * 0.045f),
            Color = color.WithAlpha(state == LampState.NeedsAttention ? (byte)224 : (byte)170)
        };

        var bounds = new SKRect(
            center.X - radius,
            center.Y - radius,
            center.X + radius,
            center.Y + radius);
        canvas.DrawArc(bounds, startAngle, sweepAngle, false, paint);
    }

    private static SKColor GetStateColor(LampState state, LampAppearance appearance)
    {
        if (appearance == LampAppearance.Muted)
        {
            return new SKColor(82, 88, 98);
        }

        if (appearance == LampAppearance.PlanQuestion)
        {
            return new SKColor(178, 112, 255);
        }

        return state switch
        {
            LampState.Running => new SKColor(70, 150, 255),
            LampState.NeedsAttention => new SKColor(255, 145, 62),
            _ => new SKColor(82, 88, 98)
        };
    }

    private static float GetMotion(
        LampState state,
        LampAppearance appearance,
        float phase)
    {
        if (state == LampState.Idle || appearance == LampAppearance.Muted)
        {
            return 0f;
        }

        var wave = (MathF.Sin(phase * MathF.PI * 2f) + 1f) * 0.5f;
        if (appearance == LampAppearance.PlanQuestion)
        {
            return 0.08f + wave * 0.06f;
        }

        return state == LampState.NeedsAttention
            ? 0.12f + wave * 0.08f
            : 0.04f + wave * 0.045f;
    }

    private static SKColor Lerp(SKColor from, SKColor to, float progress)
    {
        return new SKColor(
            LerpByte(from.Red, to.Red, progress),
            LerpByte(from.Green, to.Green, progress),
            LerpByte(from.Blue, to.Blue, progress),
            LerpByte(from.Alpha, to.Alpha, progress));
    }

    private static byte LerpByte(byte from, byte to, float progress)
    {
        return (byte)Math.Clamp(from + (to - from) * progress, 0f, 255f);
    }

    private static float SmoothStep(float progress)
    {
        return progress * progress * (3f - 2f * progress);
    }
}
