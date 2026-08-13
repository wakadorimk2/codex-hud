using System.Windows;

namespace CodexHud.Infrastructure;

public sealed record LampGroupLayout(
    int Columns,
    int Rows,
    double Width,
    double Height)
{
    public const double CellSize = 36;
    public const double Gap = 8;

    public static LampGroupLayout Calculate(Rect workArea, int sessionCount)
    {
        if (sessionCount <= 0)
        {
            return new LampGroupLayout(0, 0, 0, 0);
        }

        var maxColumns = Math.Max(
            1,
            (int)Math.Floor((workArea.Width + Gap) / (CellSize + Gap)));
        var columns = Math.Min(sessionCount, maxColumns);
        var rows = (int)Math.Ceiling((double)sessionCount / columns);
        var width = columns * CellSize + (columns - 1) * Gap;
        var height = rows * CellSize + (rows - 1) * Gap;

        return new LampGroupLayout(columns, rows, width, height);
    }
}
