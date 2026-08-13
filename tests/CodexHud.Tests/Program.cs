using System.IO;
using System.Text.Json;
using System.Windows;
using CodexHud.Domain;
using CodexHud.Infrastructure;
using CodexHud.Rendering;
using SkiaSharp;

namespace CodexHud.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("renderer renders visible pixels for every state", TestRenderer),
            ("state store applies mappings and preserves unknown events", TestStateStore),
            ("state store keeps independent sessions in stable priority order", TestMultipleSessions),
            ("session end shows idle grace and supports prompt cancellation", TestSessionEndGrace),
            ("session state snapshots restore active sessions and omit ended sessions", TestSessionStateSnapshot),
            ("hook parser keeps raw payload out of the sanitized message", TestSanitization),
            ("pipe server and sender transfer sanitized state", TestPipeTransfer),
            ("bridge returns zero when the pipe is stopped", TestBridgeWhenPipeStopped),
            ("lamp placement remains 36 DIP at 100 and 150 percent inputs", TestLampPlacement),
            ("lamp group layout keeps 36 DIP cells and wraps at work area width", TestLampGroupLayout),
            ("lamp position persists and stays inside the work area", TestLampPositionStore)
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

        Console.WriteLine($"Tests: {tests.Length - failures} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestRenderer()
    {
        var renderer = new SkiaLampRenderer();
        foreach (var state in Enum.GetValues<LampState>())
        {
            var imageInfo = new SKImageInfo(72, 72, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(imageInfo);
            renderer.Render(surface.Canvas, 72, 72, state, state, 1f, 0.35f);

            using var bitmap = new SKBitmap(imageInfo);
            Assert.True(
                surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0),
                $"ReadPixels failed for {state}.");
            var hasVisiblePixel = false;
            for (var y = 0; y < bitmap.Height && !hasVisiblePixel; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        hasVisiblePixel = true;
                        break;
                    }
                }
            }

            Assert.True(hasVisiblePixel, $"No visible pixels for {state}.");
        }

        return Task.CompletedTask;
    }

    private static Task TestStateStore()
    {
        return WithTestStore(store =>
        {
            const string sessionId = "session-test";

            store.Apply(new HookObservation(HookEventKind.SessionStart, sessionId, DateTimeOffset.UtcNow));
            Assert.Equal(LampState.Running, store.CurrentState);

            store.Apply(new HookObservation(HookEventKind.PermissionRequest, sessionId, DateTimeOffset.UtcNow));
            Assert.Equal(LampState.NeedsAttention, store.CurrentState);

            store.Apply(new HookObservation(HookEventKind.Unknown, sessionId, DateTimeOffset.UtcNow));
            Assert.Equal(LampState.NeedsAttention, store.CurrentState);

            store.Apply(new HookObservation(HookEventKind.UserPromptSubmit, sessionId, DateTimeOffset.UtcNow));
            Assert.Equal(LampState.Running, store.CurrentState);

            store.Apply(new HookObservation(HookEventKind.SessionEnd, sessionId, DateTimeOffset.UtcNow));
            Assert.Equal(LampState.Idle, store.CurrentState);
            Assert.Equal(LampState.Idle, store.CurrentSessions[0].State);
            return Task.CompletedTask;
        });
    }

    private static Task TestMultipleSessions()
    {
        return WithTestStore(store =>
        {
            const string firstSession = "session-first";
            const string secondSession = "session-second";

            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                firstSession,
                DateTimeOffset.UtcNow));
            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                secondSession,
                DateTimeOffset.UtcNow));

            Assert.Equal(2, store.CurrentSessions.Count);
            Assert.Equal(firstSession, store.CurrentSessions[0].SessionId);
            Assert.Equal(secondSession, store.CurrentSessions[1].SessionId);

            store.Apply(new HookObservation(
                HookEventKind.Stop,
                secondSession,
                DateTimeOffset.UtcNow));
            var attentionFirst = store.CurrentSessions;
            Assert.Equal(secondSession, attentionFirst[0].SessionId);
            Assert.Equal(LampState.NeedsAttention, attentionFirst[0].State);
            Assert.Equal(firstSession, attentionFirst[1].SessionId);

            store.Apply(new HookObservation(
                HookEventKind.UserPromptSubmit,
                firstSession,
                DateTimeOffset.UtcNow));
            store.Apply(new HookObservation(
                HookEventKind.UserPromptSubmit,
                secondSession,
                DateTimeOffset.UtcNow));
            var stableRunningOrder = store.CurrentSessions;
            Assert.Equal(firstSession, stableRunningOrder[0].SessionId);
            Assert.Equal(secondSession, stableRunningOrder[1].SessionId);

            store.Apply(new HookObservation(
                HookEventKind.Unknown,
                "session-unknown-event",
                DateTimeOffset.UtcNow));
            Assert.Equal(2, store.CurrentSessions.Count);
            return Task.CompletedTask;
        });
    }

    private static async Task TestSessionEndGrace()
    {
        await WithTestStore(async store =>
        {
            const string sessionId = "session-grace";
            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                sessionId,
                DateTimeOffset.UtcNow));
            store.Apply(new HookObservation(
                HookEventKind.SessionEnd,
                sessionId,
                DateTimeOffset.UtcNow));

            Assert.Equal(1, store.CurrentSessions.Count);
            Assert.Equal(LampState.Idle, store.CurrentSessions[0].State);

            store.Apply(new HookObservation(
                HookEventKind.UserPromptSubmit,
                sessionId,
                DateTimeOffset.UtcNow));
            await Task.Delay(100);
            Assert.Equal(1, store.CurrentSessions.Count);
            Assert.Equal(LampState.Running, store.CurrentSessions[0].State);

            store.Apply(new HookObservation(
                HookEventKind.SessionEnd,
                sessionId,
                DateTimeOffset.UtcNow));
            await WaitUntil(
                () => store.CurrentSessions.Count == 0,
                TimeSpan.FromMilliseconds(500));
        }, TimeSpan.FromMilliseconds(40));
    }

    private static Task TestSessionStateSnapshot()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-hud-snapshot-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "sessions.json");
        const string runningSession = "session-running";
        const string attentionSession = "session-attention";

        try
        {
            using (var firstStore = new SessionStateStore(
                       new SessionStateSnapshotStore(path)))
            {
                firstStore.Apply(new HookObservation(
                    HookEventKind.SessionStart,
                    runningSession,
                    DateTimeOffset.UtcNow));
                firstStore.Apply(new HookObservation(
                    HookEventKind.Stop,
                    attentionSession,
                    DateTimeOffset.UtcNow));
            }

            var persistedJson = File.ReadAllText(path);
            Assert.True(!persistedJson.Contains("prompt", StringComparison.OrdinalIgnoreCase),
                "Prompt text crossed into the snapshot.");
            Assert.True(!persistedJson.Contains("cwd", StringComparison.OrdinalIgnoreCase),
                "Working directory crossed into the snapshot.");

            using (var restoredStore = new SessionStateStore(
                       new SessionStateSnapshotStore(path)))
            {
                var restored = restoredStore.CurrentSessions;
                Assert.Equal(2, restored.Count);
                Assert.Equal(attentionSession, restored[0].SessionId);
                Assert.Equal(LampState.NeedsAttention, restored[0].State);
                Assert.Equal(runningSession, restored[1].SessionId);

                restoredStore.Apply(new HookObservation(
                    HookEventKind.SessionEnd,
                    runningSession,
                    DateTimeOffset.UtcNow));
            }

            using var afterEndStore = new SessionStateStore(
                new SessionStateSnapshotStore(path));
            Assert.True(
                afterEndStore.CurrentSessions.All(session => session.SessionId != runningSession),
                "Ended session was restored from the snapshot.");
            Assert.Equal(1, afterEndStore.CurrentSessions.Count);
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static Task TestSanitization()
    {
        const string secretPrompt = "private prompt must not cross the bridge";
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "raw-session-id",
            cwd = "C:\\private\\project",
            prompt = secretPrompt
        });

        Assert.True(
            HookObservationParser.TryParseHookPayload(payload, out var observation),
            "Hook payload did not parse.");
        Assert.NotNull(observation);
        Assert.NotEqual("raw-session-id", observation!.SessionId);

        var sanitized = HookObservationParser.SerializeTransportMessage(observation);
        Assert.True(!sanitized.Contains(secretPrompt, StringComparison.Ordinal), "Raw prompt crossed the boundary.");
        Assert.True(!sanitized.Contains("private\\project", StringComparison.Ordinal), "Raw cwd crossed the boundary.");
        return Task.CompletedTask;
    }

    private static async Task TestPipeTransfer()
    {
        await WithTestStore(async store =>
        {
            var pipeName = $"codex-hud-test-{Guid.NewGuid():N}";
            using var server = new NamedPipeStateServer(store.Apply, pipeName);
            server.Start();

            var sender = new NamedPipeStateSender(pipeName, TimeSpan.FromSeconds(1));
            var sent = await sender.SendAsync(
                new HookObservation(HookEventKind.Stop, "session-test", DateTimeOffset.UtcNow));

            Assert.True(sent, "Named Pipe send failed.");
            for (var attempt = 0; attempt < 50
                && store.CurrentState != LampState.NeedsAttention;
                attempt++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(LampState.NeedsAttention, store.CurrentState);
        });
    }

    private static async Task TestBridgeWhenPipeStopped()
    {
        var pipeName = $"codex-hud-stopped-{Guid.NewGuid():N}";
        var transport = new NamedPipeStateSender(pipeName, TimeSpan.FromMilliseconds(50));
        var bridge = new HookBridge(transport, new NoOpHudLauncher());
        var payload = "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"session-test\"}";

        var exitCode = await bridge.RunAsync(new StringReader(payload));
        Assert.Equal(0, exitCode);
    }

    private static Task TestLampPlacement()
    {
        var at100 = LampPlacement.Calculate(new Rect(0, 0, 1920, 1080), 36, 36, 16);
        Assert.Equal(1868d, at100.X);
        Assert.Equal(1028d, at100.Y);

        var at150 = LampPlacement.Calculate(new Rect(0, 0, 1280, 720), 36, 36, 16);
        Assert.Equal(1228d, at150.X);
        Assert.Equal(668d, at150.Y);
        return Task.CompletedTask;
    }

    private static Task TestLampGroupLayout()
    {
        var empty = LampGroupLayout.Calculate(new Rect(0, 0, 1920, 1080), 0);
        Assert.Equal(0, empty.Columns);
        Assert.Equal(0d, empty.Width);

        var wrapped = LampGroupLayout.Calculate(new Rect(0, 0, 100, 100), 3);
        Assert.Equal(2, wrapped.Columns);
        Assert.Equal(2, wrapped.Rows);
        Assert.Equal(80d, wrapped.Width);
        Assert.Equal(80d, wrapped.Height);
        Assert.Equal(36d, LampGroupLayout.CellSize);
        Assert.Equal(8d, LampGroupLayout.Gap);
        return Task.CompletedTask;
    }

    private static Task TestLampPositionStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-hud-test-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "position.json");

        try
        {
            var store = new LampPositionStore(path);
            Assert.True(store.Load() is null, "Unexpected saved position.");
            Assert.True(store.TrySave(new Point(120, 240)), "Position save failed.");

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal(120d, loaded!.Value.X);
            Assert.Equal(240d, loaded.Value.Y);

            var clamped = LampPlacement.Clamp(
                new Rect(0, 0, 500, 400),
                new Point(490, 390),
                36,
                36);
            Assert.Equal(464d, clamped.X);
            Assert.Equal(364d, clamped.Y);
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class NoOpHudLauncher : IHudLauncher
    {
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static async Task WithTestStore(
        Func<SessionStateStore, Task> test,
        TimeSpan? sessionEndGrace = null)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-hud-store-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "sessions.json");

        try
        {
            using var store = new SessionStateStore(
                new SessionStateSnapshotStore(path),
                sessionEndGrace);
            await test(store);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition did not become true before the timeout.");
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void NotEqual<T>(T expected, T actual)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Did not expect {expected}.");
        }
    }

    public static void NotNull<T>(T? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }
}
