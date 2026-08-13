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
            ("legacy session snapshots without timestamps are excluded", TestLegacySessionSnapshot),
            ("session catalog probe sanitizes IDs and marks archived sessions", TestSessionCatalogProbe),
            ("catalog read failure keeps saved sessions", TestCatalogReadFailure),
            ("session catalog cleanup removes archived and stale sessions", TestSessionCatalogCleanup),
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
            var firstObservedAtUtc = DateTimeOffset.Parse("2026-08-13T08:00:00Z");
            var secondObservedAtUtc = DateTimeOffset.Parse("2026-08-13T09:00:00Z");

            store.Apply(new HookObservation(HookEventKind.SessionStart, sessionId, firstObservedAtUtc));
            Assert.Equal(LampState.Running, store.CurrentState);
            Assert.Equal(firstObservedAtUtc, store.CurrentSessions[0].LastObservedAtUtc);

            store.Apply(new HookObservation(HookEventKind.PermissionRequest, sessionId, secondObservedAtUtc));
            Assert.Equal(LampState.NeedsAttention, store.CurrentState);
            Assert.Equal(secondObservedAtUtc, store.CurrentSessions[0].LastObservedAtUtc);

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

    private static Task TestLegacySessionSnapshot()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-hud-legacy-snapshot-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "sessions.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                "[{\"sessionId\":\"session-legacy\",\"state\":\"Running\",\"firstSeenOrder\":1}]");

            using var store = new SessionStateStore(
                new SessionStateSnapshotStore(path));
            Assert.Equal(0, store.CurrentSessions.Count);
            Assert.True(
                File.ReadAllText(path).Trim() == "[]",
                "Timestamp-less legacy session was not removed from the snapshot.");
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

    private static Task TestSessionCatalogProbe()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"codex-hud-catalog-{Guid.NewGuid():N}");
        var archiveDirectory = Path.Combine(directory, "archived_sessions");
        var indexPath = Path.Combine(directory, "session_index.jsonl");
        const string archivedRawId = "11111111-1111-1111-1111-111111111111";
        const string activeRawId = "22222222-2222-2222-2222-222222222222";

        try
        {
            Directory.CreateDirectory(archiveDirectory);
            File.WriteAllText(
                indexPath,
                string.Join(
                    Environment.NewLine,
                    JsonSerializer.Serialize(new
                    {
                        id = archivedRawId,
                        thread_name = "private thread",
                        updated_at = "2026-08-13T08:00:00Z"
                    }),
                    JsonSerializer.Serialize(new
                    {
                        id = activeRawId,
                        thread_name = "active thread",
                        updated_at = "2026-08-13T09:00:00Z"
                    })));
            File.WriteAllText(
                Path.Combine(
                    archiveDirectory,
                    $"rollout-2026-08-13T00-00-00-{archivedRawId}.jsonl"),
                string.Empty);

            var probe = new CodexSessionCatalogProbe(directory);
            Assert.True(probe.TryRead(out var entries), "Catalog probe failed.");
            Assert.Equal(2, entries.Count);

            var archivedSessionId = HashSessionIdForTest(archivedRawId);
            var activeSessionId = HashSessionIdForTest(activeRawId);
            var archived = entries.Single(entry => entry.SessionId == archivedSessionId);
            var active = entries.Single(entry => entry.SessionId == activeSessionId);

            Assert.True(archived.IsArchived, "Archived session was not marked.");
            Assert.True(!active.IsArchived, "Active session was marked archived.");
            Assert.Equal(
                DateTimeOffset.Parse("2026-08-13T08:00:00Z"),
                archived.LastUpdatedAtUtc);
            Assert.Equal(
                DateTimeOffset.Parse("2026-08-13T09:00:00Z"),
                active.LastUpdatedAtUtc);

            var sanitized = JsonSerializer.Serialize(entries);
            Assert.True(
                !sanitized.Contains(archivedRawId, StringComparison.Ordinal),
                "Archived raw session ID crossed the catalog boundary.");
            Assert.True(
                !sanitized.Contains(activeRawId, StringComparison.Ordinal),
                "Active raw session ID crossed the catalog boundary.");
            Assert.True(
                !new CodexSessionCatalogProbe(
                    Path.Combine(directory, "missing"))
                    .TryRead(out _),
                "Missing catalog unexpectedly succeeded.");
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

    private static Task TestSessionCatalogCleanup()
    {
        return WithTestStore(store =>
        {
            var now = DateTimeOffset.Parse("2026-08-13T12:00:00Z");
            const string archivedSession = "session-archived";
            const string staleSession = "session-stale";
            const string recentSession = "session-recent";
            const string catalogNewerSession = "session-catalog-newer";
            const string unknownSession = "session-unknown-catalog";
            const string absentRunningSession = "session-absent-running";

            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                archivedSession,
                now - TimeSpan.FromHours(1)));
            store.Apply(new HookObservation(
                HookEventKind.Stop,
                staleSession,
                now - TimeSpan.FromHours(1) - TimeSpan.FromMinutes(1)));
            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                recentSession,
                now - TimeSpan.FromMinutes(59)));
            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                catalogNewerSession,
                now - TimeSpan.FromHours(2)));
            store.Apply(new HookObservation(
                HookEventKind.PermissionRequest,
                unknownSession,
                now - TimeSpan.FromMinutes(1)));
            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                absentRunningSession,
                now - TimeSpan.FromMinutes(2)));

            var catalogEntries = new[]
            {
                new SessionCatalogEntry(
                    archivedSession,
                    now - TimeSpan.FromHours(1),
                    IsArchived: true),
                new SessionCatalogEntry(
                    staleSession,
                    now - TimeSpan.FromHours(1) - TimeSpan.FromMinutes(1),
                    IsArchived: false),
                new SessionCatalogEntry(
                    recentSession,
                    now - TimeSpan.FromMinutes(59),
                    IsArchived: false),
                new SessionCatalogEntry(
                    catalogNewerSession,
                    now - TimeSpan.FromMinutes(30),
                    IsArchived: false)
            };

            var removed = store.ReconcileCatalog(catalogEntries, now);

            Assert.Equal(4, removed);
            Assert.Equal(2, store.CurrentSessions.Count);
            Assert.True(
                store.CurrentSessions.All(session =>
                    session.SessionId == recentSession
                    || session.SessionId == catalogNewerSession),
                "Unexpected sessions remained after catalog cleanup.");

            store.Apply(new HookObservation(
                HookEventKind.SessionStart,
                unknownSession,
                now));
            Assert.True(
                store.CurrentSessions.Any(session => session.SessionId == unknownSession),
                "Hook did not recreate the catalog-absent session.");

            var removedAfterHook = store.ReconcileCatalog(catalogEntries, now);
            Assert.Equal(1, removedAfterHook);
            Assert.Equal(2, store.CurrentSessions.Count);
            return Task.CompletedTask;
        });
    }

    private static Task TestCatalogReadFailure()
    {
        return WithTestStore(store =>
        {
            const string recentAbsentSession = "session-catalog-read-failure";
            store.Apply(new HookObservation(
                HookEventKind.Stop,
                recentAbsentSession,
                DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)));

            var missingCatalog = new CodexSessionCatalogProbe(
                Path.Combine(Path.GetTempPath(), $"codex-hud-missing-catalog-{Guid.NewGuid():N}"));
            Assert.True(
                !missingCatalog.TryRead(out _),
                "Missing catalog unexpectedly succeeded.");
            Assert.Equal(1, store.CurrentSessions.Count);
            Assert.Equal(recentAbsentSession, store.CurrentSessions[0].SessionId);
            return Task.CompletedTask;
        });
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

    private static string HashSessionIdForTest(string rawSessionId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "SessionStart",
            session_id = rawSessionId
        });
        Assert.True(
            HookObservationParser.TryParseHookPayload(payload, out var observation),
            "Could not hash a test session ID.");
        Assert.NotNull(observation);
        return observation!.SessionId;
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
