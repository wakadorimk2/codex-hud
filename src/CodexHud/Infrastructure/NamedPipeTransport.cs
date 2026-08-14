using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public interface IHookMessageTransport
{
    Task<bool> SendAsync(HookObservation observation, CancellationToken cancellationToken = default);
}

public sealed class NamedPipeStateSender : IHookMessageTransport
{
    private static readonly TimeSpan SessionStartConnectTimeout =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectRetryDelay =
        TimeSpan.FromMilliseconds(25);

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public NamedPipeStateSender(
        string pipeName = NamedPipeStateServer.DefaultPipeName,
        TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(750);
    }

    public async Task<bool> SendAsync(
        HookObservation observation,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow
            + (observation.Event == HookEventKind.SessionStart
                ? SessionStartConnectTimeout
                : _connectTimeout);

        try
        {
            while (true)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                await using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    timeout.CancelAfter(remaining);
                    await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!await WaitBeforeConnectRetryAsync(deadline, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

                    continue;
                }
                catch (TimeoutException)
                {
                    if (!await WaitBeforeConnectRetryAsync(deadline, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

                    continue;
                }
                catch (IOException)
                {
                    if (!await WaitBeforeConnectRetryAsync(deadline, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    if (!await WaitBeforeConnectRetryAsync(deadline, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        return false;
                    }

                    continue;
                }

                try
                {
                    remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    sendTimeout.CancelAfter(remaining);

                    var message = HookObservationParser.SerializeTransportMessage(observation)
                        + Environment.NewLine;
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await client.WriteAsync(bytes, sendTimeout.Token).ConfigureAwait(false);
                    await client.FlushAsync(sendTimeout.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException)
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
            }
        }
        catch (OperationCanceledException)
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
    }

    private static async Task<bool> WaitBeforeConnectRetryAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        await Task.Delay(
            remaining < ConnectRetryDelay ? remaining : ConnectRetryDelay,
            cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.UtcNow < deadline;
    }
}

public sealed class NamedPipeStateServer : IDisposable
{
    public const string DefaultPipeName = "codex-hud-state-v1";

    private readonly string _pipeName;
    private readonly Action<HookObservation> _onObservation;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;

    public NamedPipeStateServer(
        Action<HookObservation> onObservation,
        string pipeName = DefaultPipeName)
    {
        _onObservation = onObservation;
        _pipeName = pipeName;
    }

    public void Start()
    {
        _runTask ??= RunAsync();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _runTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);

                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                var line = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (HookObservationParser.TryParseTransportMessage(line, out var observation)
                    && observation is not null)
                {
                    _onObservation(observation);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!_shutdown.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
