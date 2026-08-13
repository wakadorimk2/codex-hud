using System.IO;
using System.Text.Json;
using System.Windows;

namespace CodexHud.Infrastructure;

public sealed class LampPositionStore
{
    private readonly string _path;

    public LampPositionStore(string? path = null)
    {
        _path = path ?? GetDefaultPath();
    }

    public Point? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var savedPosition = JsonSerializer.Deserialize<SavedPosition>(
                File.ReadAllText(_path));
            if (savedPosition is null
                || !double.IsFinite(savedPosition.Left)
                || !double.IsFinite(savedPosition.Top))
            {
                return null;
            }

            return new Point(savedPosition.Left, savedPosition.Top);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool TrySave(Point position)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new SavedPosition(position.X, position.Y));
            File.WriteAllText(_path, json);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetDefaultPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "CodexHud", "position.json");
    }

    private sealed record SavedPosition(double Left, double Top);
}
