using System.IO;
using System.Runtime.InteropServices;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed class WindowsSessionActivitySource : ISessionActivitySource
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteBusyTimeoutMilliseconds = 75;

    private readonly string _sessionsRoot;
    private readonly string _databasePath;

    public WindowsSessionActivitySource(string? codexDirectory = null)
    {
        var directory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(codexDirectory)
                ? CodexSessionCatalogPaths.GetDefaultCodexDirectory()
                : codexDirectory);
        _sessionsRoot = Path.Combine(directory, "sessions");
        _databasePath = Path.Combine(directory, "state_5.sqlite");
    }

    public string DatabasePath => _databasePath;

    public bool TryGetRecentActivities(
        DateTimeOffset cutoffUtc,
        int maximumRows,
        out IReadOnlyList<SessionActivity> activities)
    {
        activities = Array.Empty<SessionActivity>();
        if (maximumRows <= 0 || !File.Exists(_databasePath))
        {
            return false;
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var openResult = sqlite3_open_v2(
                _databasePath,
                out database,
                SqliteOpenReadOnly,
                IntPtr.Zero);
            if (openResult != SqliteOk || database == IntPtr.Zero)
            {
                return false;
            }

            _ = sqlite3_busy_timeout(database, SqliteBusyTimeoutMilliseconds);
            var cutoffMilliseconds = cutoffUtc.ToUniversalTime().ToUnixTimeMilliseconds();
            var rowLimit = Math.Clamp(maximumRows, 1, 64);
            var sql = $"SELECT id, rollout_path, updated_at_ms FROM threads WHERE archived = 0 AND thread_source = 'user' AND updated_at_ms >= {cutoffMilliseconds} ORDER BY updated_at_ms DESC LIMIT {rowLimit}";
            var prepareResult = sqlite3_prepare_v2(
                database,
                sql,
                -1,
                out statement,
                IntPtr.Zero);
            if (prepareResult != SqliteOk || statement == IntPtr.Zero)
            {
                return false;
            }

            var rows = new List<SessionActivity>();
            while (true)
            {
                var stepResult = sqlite3_step(statement);
                if (stepResult == SqliteDone)
                {
                    break;
                }

                if (stepResult != SqliteRow)
                {
                    return false;
                }

                var rawId = ReadText(sqlite3_column_text(statement, 0));
                var rawPath = ReadText(sqlite3_column_text(statement, 1));
                var updatedAtMilliseconds = sqlite3_column_int64(statement, 2);
                if (!TryValidateActivity(
                        rawId,
                        rawPath,
                        updatedAtMilliseconds,
                        out var activity))
                {
                    continue;
                }

                rows.Add(activity);
            }

            activities = rows;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (SEHException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                _ = sqlite3_finalize(statement);
            }

            if (database != IntPtr.Zero)
            {
                _ = sqlite3_close(database);
            }
        }
    }

    private bool TryValidateActivity(
        string? rawId,
        string? rawPath,
        long updatedAtMilliseconds,
        out SessionActivity activity)
    {
        activity = null!;
        if (string.IsNullOrWhiteSpace(rawId)
            || string.IsNullOrWhiteSpace(rawPath)
            || updatedAtMilliseconds <= 0)
        {
            return false;
        }

        var sessionId = SessionIdHasher.Hash(rawId);
        if (string.Equals(sessionId, "session-unknown", StringComparison.Ordinal))
        {
            return false;
        }

        string rolloutPath;
        try
        {
            rolloutPath = Path.GetFullPath(
                Path.IsPathRooted(rawPath)
                    ? rawPath
                    : Path.Combine(_sessionsRoot, rawPath));
        }
        catch (ArgumentException)
        {
            return false;
        }

        var root = _sessionsRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!rolloutPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !CodexSessionFileDiscovery.TryGetFileNameSessionId(
                rolloutPath,
                out var fileSessionId)
            || !string.Equals(fileSessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        activity = new SessionActivity(
            sessionId,
            rolloutPath,
            DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMilliseconds));
        return true;
    }

    private static string? ReadText(IntPtr value)
    {
        return value == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUTF8(value);
    }

    [DllImport(
        "winsqlite3.dll",
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "sqlite3_open_v2")]
    private static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr database,
        int flags,
        IntPtr vfs);

    [DllImport(
        "winsqlite3.dll",
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "sqlite3_prepare_v2")]
    private static extern int sqlite3_prepare_v2(
        IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int byteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);
}

internal sealed class EmptySessionActivitySource : ISessionActivitySource
{
    public bool TryGetRecentActivities(
        DateTimeOffset cutoffUtc,
        int maximumRows,
        out IReadOnlyList<SessionActivity> activities)
    {
        activities = Array.Empty<SessionActivity>();
        return false;
    }
}
