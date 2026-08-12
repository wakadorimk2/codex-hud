using System.Windows;

namespace CodexHud.Infrastructure;

public static class LampPlacement
{
    public static Point Calculate(Rect workArea, double lampWidth, double lampHeight, double margin)
    {
        return new Point(
            workArea.Right - lampWidth - margin,
            workArea.Bottom - lampHeight - margin);
    }
}
