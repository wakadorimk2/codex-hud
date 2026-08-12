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
            ("hook parser keeps raw payload out of the sanitized message", TestSanitization),
            ("pipe server and sender transfer sanitized state", TestPipeTransfer),
            ("bridge returns zero when the pipe is stopped", TestBridgeWhenPipeStopped),
            ("lamp placement remains 36 DIP at 100 and 150 percent inputs", TestLampPlacement)
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
        var store = new SessionStateStore();
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
        return Task.CompletedTask;
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
        var pipeName = $"codex-hud-test-{Guid.NewGuid():N}";
        var store = new SessionStateStore();
        using var server = new NamedPipeStateServer(store.Apply, pipeName);
        server.Start();

        var sender = new NamedPipeStateSender(pipeName, TimeSpan.FromSeconds(1));
        var sent = await sender.SendAsync(
            new HookObservation(HookEventKind.Stop, "session-test", DateTimeOffset.UtcNow));

        Assert.True(sent, "Named Pipe send failed.");
        for (var attempt = 0; attempt < 50 && store.CurrentState != LampState.NeedsAttention; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(LampState.NeedsAttention, store.CurrentState);
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

    private sealed class NoOpHudLauncher : IHudLauncher
    {
        public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
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
