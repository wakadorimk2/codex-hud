using System.Windows;
using Point = System.Windows.Point;

namespace CodexHud.Infrastructure;

public static class LampPlacement
{
    public static Point Calculate(Rect workArea, double lampWidth, double lampHeight, double margin)
    {
        return new Point(
            workArea.Right - lampWidth - margin,
            workArea.Bottom - lampHeight - margin);
    }

    public static Point Clamp(Rect workArea, Point position, double lampWidth, double lampHeight)
    {
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - lampWidth);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - lampHeight);

        return new Point(
            Math.Clamp(position.X, workArea.Left, maximumLeft),
            Math.Clamp(position.Y, workArea.Top, maximumTop));
    }
}
