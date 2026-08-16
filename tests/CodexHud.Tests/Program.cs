using System.Diagnostics;
using System.IO;
using System.Text;
using CodexHud.Domain;
using CodexHud.Infrastructure;
using CodexHud.Rendering;
using SkiaSharp;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("six lamp states render with fixed colors", TestRenderer),
            ("JSONL parser accepts explicit activity and rejects non-evidence", TestJsonlProbe),
            ("JSONL discovery creates two unique lamps", TestTwoSessionDiscovery),
            ("lifecycle maps active, listening, idle, completed, and aborted", TestLifecycle),
            ("SQLite activity keeps an old JSONL session active", TestSqliteActivity),
            ("SQLite failure falls back to JSONL", TestSqliteFallback),
            ("SQLite path and ID validation rejects mismatches", TestSqliteValidation),
            ("membership applies window, cap, internal exclusion, and deduplication", TestMembership),
            ("read error is held and file deletion removes a lamp", TestReadErrorAndDeletion),
            ("file replacement resets the JSONL cursor", TestFileReplacement),
            ("session index alone does not create a lamp", TestSessionIndexIsNotSource),
            ("watcher reports changed JSONL paths", TestSessionFileWatcher),
            ("installer and app do not use the Hook path", TestHookPathDisabled),
            ("lamp layout and placement remain stable", TestLayoutAndPlacement)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestRenderer()
    {
        var renderer = new SkiaLampRenderer();
        var states = Enum.GetValues<LampState>();
        foreach (var state in states)
        {
            using var bitmap = RenderBitmap(renderer, state, state, 0.0f, 0.0f);
            Assert.True(HasVisiblePixels(bitmap), $"No visible pixels for {state}.");
        }

        using var completedAtPhaseZero = RenderBitmap(
            renderer,
            LampState.Completed,
            LampState.Completed,
            1.0f,
            0.0f);
        using var completedAtPhaseLater = RenderBitmap(
            renderer,
            LampState.Completed,
            LampState.Completed,
            1.0f,
            0.75f);
        Assert.Equal(
            HashPixels(completedAtPhaseZero),
            HashPixels(completedAtPhaseLater));

        using var activeAtPhaseZero = RenderBitmap(
            renderer,
            LampState.Active,
            LampState.Active,
            1.0f,
            0.0f);
        using var activeAtPhaseLater = RenderBitmap(
            renderer,
            LampState.Active,
            LampState.Active,
            1.0f,
            0.75f);
        Assert.NotEqual(HashPixels(activeAtPhaseZero), HashPixels(activeAtPhaseLater));
        return Task.CompletedTask;
    }

    private static Task TestJsonlProbe()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var rawId = Guid.NewGuid().ToString();
        var path = CreateRollout(
            directory.Sessions,
            rawId,
            now,
            string.Join(
                "\n",
                TaskStarted(rawId),
                "{ malformed",
                EventMessage("unknown_event", rawId),
                TaskComplete(rawId, silent: true),
                TurnAborted(rawId)) + "\n");
        var discovery = new CodexSessionFileDiscovery(directory.Sessions);
        var result = discovery.Discover(now);
        var probe = new CodexSessionEventProbe();
        var observations = probe.Read(result, now);

        Assert.Equal(2, observations.Count);
        Assert.Equal(JsonlActivityKind.TurnStarted, observations[0].Kind);
        Assert.Equal(JsonlActivityKind.TurnAborted, observations[1].Kind);
        Assert.False(observations.Any(observation => observation.IsSilent));
        Assert.True(path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    private static Task TestTwoSessionDiscovery()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var firstPath = CreateRollout(directory.Sessions, Guid.NewGuid().ToString(), now, "{}\n");
        var secondPath = CreateRollout(directory.Sessions, Guid.NewGuid().ToString(), now, "{}\n");
        var engine = CreateEngine(directory.Sessions);

        engine.RefreshActiveSessions(now);
        var sessions = engine.GetVisibleSessions();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, sessions.Select(session => session.SessionId).Distinct().Count());
        Assert.True(sessions.All(session => session.State == LampState.Listening));
        Assert.True(firstPath != secondPath);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestLifecycle()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var rawId = Guid.NewGuid().ToString();
        var path = CreateRollout(directory.Sessions, rawId, now, TaskStarted(rawId) + "\n");
        var engine = CreateEngine(directory.Sessions);

        engine.RefreshActiveSessions(now);
        Assert.Equal(LampState.Active, engine.GetVisibleSessions()[0].State);

        engine.AdvanceLifecycle(now.AddSeconds(13));
        Assert.Equal(LampState.Listening, engine.GetVisibleSessions()[0].State);

        engine.AdvanceLifecycle(now.AddSeconds(91));
        Assert.Equal(LampState.Idle, engine.GetVisibleSessions()[0].State);

        Append(path, TaskComplete(rawId, silent: false) + "\n", now.AddMinutes(1));
        engine.PollPaths(new[] { path }, now.AddMinutes(1));
        Assert.Equal(LampState.Completed, engine.GetVisibleSessions()[0].State);

        Append(path, TurnAborted(rawId) + "\n", now.AddMinutes(2));
        engine.PollPaths(new[] { path }, now.AddMinutes(2));
        Assert.Equal(LampState.Aborted, engine.GetVisibleSessions()[0].State);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestSqliteActivity()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var rawId = Guid.NewGuid().ToString();
        var path = CreateRollout(
            directory.Sessions,
            rawId,
            now.AddMinutes(-20),
            "{}\n");
        var source = new FakeActivitySource(
            succeeded: true,
            new SessionActivity(
                SessionIdHasher.Hash(rawId),
                path,
                now.AddSeconds(-30)));
        var engine = CreateEngine(directory.Sessions, source);

        engine.RefreshActiveSessions(now);
        Assert.Equal(1, engine.GetVisibleSessions().Count);
        Assert.Equal(LampState.Active, engine.GetVisibleSessions()[0].State);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestSqliteFallback()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        CreateRollout(directory.Sessions, Guid.NewGuid().ToString(), now, "{}\n");
        CreateRollout(directory.Sessions, Guid.NewGuid().ToString(), now, "{}\n");
        var engine = CreateEngine(
            directory.Sessions,
            new FakeActivitySource(succeeded: false));

        engine.RefreshActiveSessions(now);
        Assert.Equal(2, engine.GetVisibleSessions().Count);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestSqliteValidation()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var rawId = Guid.NewGuid().ToString();
        var otherId = Guid.NewGuid().ToString();
        var path = CreateRollout(directory.Sessions, rawId, now, "{}\n");
        var source = new FakeActivitySource(
            succeeded: true,
            new SessionActivity(SessionIdHasher.Hash(otherId), path, now));
        var engine = CreateEngine(directory.Sessions, source);

        engine.RefreshActiveSessions(now);
        Assert.Equal(LampState.Listening, engine.GetVisibleSessions()[0].State);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestMembership()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var duplicateId = Guid.NewGuid().ToString();
        CreateRollout(directory.Sessions, duplicateId, now, "{}\n");
        CreateRollout(directory.Sessions, duplicateId, now.AddSeconds(-1), "{}\n", nested: true);

        var internalId = Guid.NewGuid().ToString();
        CreateRollout(
            directory.Sessions,
            internalId,
            now,
            SessionMeta(internalId, subagent: true) + "\n");

        var tooOldId = Guid.NewGuid().ToString();
        CreateRollout(directory.Sessions, tooOldId, now.AddMinutes(-31), "{}\n");

        for (var index = 0; index < 65; index++)
        {
            CreateRollout(
                directory.Sessions,
                Guid.NewGuid().ToString(),
                now.AddSeconds(-index - 2),
                "{}\n");
        }

        var discovery = new CodexSessionFileDiscovery(
            directory.Sessions,
            activeWindowMinutes: 30,
            maximumCandidates: 64);
        var engine = new SessionMonitorEngine(
            discovery,
            maximumSessions: 64);
        engine.RefreshActiveSessions(now);
        var sessions = engine.GetVisibleSessions();
        Assert.Equal(64, sessions.Count);
        Assert.Equal(64, sessions.Select(session => session.SessionId).Distinct().Count());
        Assert.False(sessions.Any(session => session.SessionId == SessionIdHasher.Hash(internalId)));
        Assert.False(sessions.Any(session => session.SessionId == SessionIdHasher.Hash(tooOldId)));
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestReadErrorAndDeletion()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var rawId = Guid.NewGuid().ToString();
        var path = CreateRollout(directory.Sessions, rawId, now, "{}\n");
        var engine = CreateEngine(directory.Sessions);
        using (var locked = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            engine.RefreshActiveSessions(now);
            Assert.Equal(LampState.ReadError, engine.GetVisibleSessions()[0].State);
        }

        engine.AdvanceLifecycle(now.AddSeconds(31));
        Assert.Equal(LampState.Listening, engine.GetVisibleSessions()[0].State);
        File.Delete(path);
        engine.RefreshActiveSessions(now.AddSeconds(32));
        Assert.Equal(0, engine.GetVisibleSessions().Count);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static Task TestFileReplacement()
    {
        using var directory = new TempDirectory();
        var now = TestNow;
        var rawId = Guid.NewGuid().ToString();
        var path = CreateRollout(directory.Sessions, rawId, now, TaskStarted(rawId) + "\n");
        var discovery = new CodexSessionFileDiscovery(directory.Sessions);
        var probe = new CodexSessionEventProbe();
        Assert.Equal(
            JsonlActivityKind.TurnStarted,
            probe.Read(discovery.Discover(now), now)[0].Kind);

        File.WriteAllText(path, TurnAborted(rawId) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, now.AddSeconds(1).UtcDateTime);
        var replacement = probe.Read(discovery.Discover(now.AddSeconds(2)), now.AddSeconds(2));
        Assert.Equal(1, replacement.Count);
        Assert.Equal(JsonlActivityKind.TurnAborted, replacement[0].Kind);
        return Task.CompletedTask;
    }

    private static Task TestSessionIndexIsNotSource()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            Path.Combine(directory.Root, "session_index.jsonl"),
            "{\"id\":\"" + Guid.NewGuid() + "\",\"updated_at\":\"2026-08-16T12:00:00Z\"}\n");
        var engine = CreateEngine(directory.Sessions);
        engine.RefreshActiveSessions(TestNow);
        Assert.Equal(0, engine.GetVisibleSessions().Count);
        engine.Dispose();
        return Task.CompletedTask;
    }

    private static async Task TestSessionFileWatcher()
    {
        using var directory = new TempDirectory();
        var change = new TaskCompletionSource<SessionFileChange>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new CodexSessionFileWatcher(
            directory.Sessions,
            value => change.TrySetResult(value));
        var path = Path.Combine(directory.Sessions, "watch.jsonl");
        File.WriteAllText(path, "{}\n");
        var completed = await Task.WhenAny(change.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(completed == change.Task, "The watcher did not report the file.");
        var result = await change.Task;
        Assert.True(result.Paths.Any(value => value.EndsWith("watch.jsonl", StringComparison.OrdinalIgnoreCase)));
    }

    private static Task TestHookPathDisabled()
    {
        var appPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CodexHud",
            "App.xaml.cs");
        var installerPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "Install-CodexHud.ps1");
        var uninstallerPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "Uninstall-CodexHud.ps1");
        var app = File.ReadAllText(appPath);
        var installer = File.ReadAllText(installerPath);
        var uninstaller = File.ReadAllText(uninstallerPath);
        Assert.True(app.Contains("\"--hook\"", StringComparison.Ordinal));
        Assert.False(app.Contains("HookBridge", StringComparison.Ordinal));
        Assert.False(app.Contains("NamedPipe", StringComparison.Ordinal));
        Assert.False(installer.Contains("hooks.json", StringComparison.OrdinalIgnoreCase));
        Assert.False(uninstaller.Contains("hooks.json", StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    private static Task TestLayoutAndPlacement()
    {
        var layout = LampGroupLayout.Calculate(new System.Windows.Rect(0, 0, 640, 480), 37);
        Assert.Equal(14, layout.Columns);
        Assert.Equal(3, layout.Rows);
        var position = LampPlacement.Clamp(
            new System.Windows.Rect(0, 0, 640, 480),
            new System.Windows.Point(620, 470),
            100,
            50);
        Assert.Equal(540d, position.X);
        Assert.Equal(430d, position.Y);
        return Task.CompletedTask;
    }

    private static SessionMonitorEngine CreateEngine(
        string sessionsRoot,
        ISessionActivitySource? activitySource = null)
    {
        return new SessionMonitorEngine(
            new CodexSessionFileDiscovery(sessionsRoot),
            activitySource: activitySource);
    }

    private static string CreateRollout(
        string sessionsRoot,
        string rawId,
        DateTimeOffset lastWriteUtc,
        string content,
        bool nested = false)
    {
        var directory = nested
            ? Path.Combine(sessionsRoot, "2026", "08", "16")
            : sessionsRoot;
        Directory.CreateDirectory(directory);
        var fileName = $"rollout-2026-08-16T12-00-00-{rawId}.jsonl";
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
        return path;
    }

    private static void Append(string path, string content, DateTimeOffset lastWriteUtc)
    {
        File.AppendAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
    }

    private static string SessionMeta(string rawId, bool subagent)
    {
        return $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{rawId}\",\"source\":{{\"subagent\":{subagent.ToString().ToLowerInvariant()}}}}}}}";
    }

    private static string TaskStarted(string rawId)
    {
        return EventMessage("task_started", rawId);
    }

    private static string TaskComplete(string rawId, bool silent)
    {
        var message = silent ? "" : "done";
        return $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_complete\",\"session_id\":\"{rawId}\",\"last_agent_message\":\"{message}\"}}}}";
    }

    private static string TurnAborted(string rawId)
    {
        return EventMessage("turn_aborted", rawId);
    }

    private static string EventMessage(string type, string rawId)
    {
        return $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"{type}\",\"session_id\":\"{rawId}\"}}}}";
    }

    private static SKBitmap RenderBitmap(
        SkiaLampRenderer renderer,
        LampState fromState,
        LampState toState,
        float transitionProgress,
        float phase)
    {
        var bitmap = new SKBitmap(48, 48, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(
            canvas,
            bitmap.Width,
            bitmap.Height,
            fromState,
            toState,
            transitionProgress,
            phase);
        return bitmap;
    }

    private static bool HasVisiblePixels(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string HashPixels(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        var index = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                bytes[index++] = pixel.Red;
                bytes[index++] = pixel.Green;
                bytes[index++] = pixel.Blue;
                bytes[index++] = pixel.Alpha;
            }
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "README.md"))
                && Directory.Exists(Path.Combine(current, "src")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName ?? string.Empty;
        }

        return Environment.CurrentDirectory;
    }

    private static readonly DateTimeOffset TestNow =
        new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeActivitySource : ISessionActivitySource
    {
        private readonly bool _succeeded;
        private readonly IReadOnlyList<SessionActivity> _activities;

        public FakeActivitySource(bool succeeded, params SessionActivity[] activities)
        {
            _succeeded = succeeded;
            _activities = activities;
        }

        public bool TryGetRecentActivities(
            DateTimeOffset cutoffUtc,
            int maximumRows,
            out IReadOnlyList<SessionActivity> activities)
        {
            activities = _activities;
            return _succeeded;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CodexHudTests",
                Guid.NewGuid().ToString("N"));
            Sessions = Path.Combine(Root, "sessions");
            Directory.CreateDirectory(Sessions);
        }

        public string Root { get; }

        public string Sessions { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static class Assert
    {
        public static void True(bool condition, string message = "Assertion failed.")
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void False(bool condition, string message = "Assertion failed.")
        {
            True(!condition, message);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    $"Expected '{expected}', actual '{actual}'.");
            }
        }

        public static void NotEqual<T>(T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    $"Expected values to differ, both were '{actual}'.");
            }
        }
    }
}
