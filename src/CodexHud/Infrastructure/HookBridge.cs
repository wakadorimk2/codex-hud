using System.IO;
using System.Diagnostics;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public interface IHudLauncher
{
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);
}

public sealed class HudProcessLauncher : IHudLauncher
{
    public Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return Task.CompletedTask;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true
        })?.Dispose();

        return Task.CompletedTask;
    }
}

public sealed class HookBridge
{
    private readonly IHookMessageTransport _transport;
    private readonly IHudLauncher _launcher;

    public HookBridge(IHookMessageTransport transport, IHudLauncher launcher)
    {
        _transport = transport;
        _launcher = launcher;
    }

    public static HookBridge CreateDefault()
    {
        return new HookBridge(
            new NamedPipeStateSender(),
            new HudProcessLauncher());
    }

    public async Task<int> RunAsync(
        TextReader input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rawPayload = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (!HookObservationParser.TryParseHookPayload(rawPayload, out var observation)
                || observation is null)
            {
                return 0;
            }

            if (observation.Event == HookEventKind.SessionStart)
            {
                await _launcher.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            }

            await _transport.SendAsync(observation, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A hook must not fail the Codex turn when the HUD is unavailable.
        }

        return 0;
    }
}
