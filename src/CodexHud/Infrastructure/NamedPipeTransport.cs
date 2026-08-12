using System.IO;
using System.IO.Pipes;
using System.Text;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public interface IHookMessageTransport
{
    Task<bool> SendAsync(HookObservation observation, CancellationToken cancellationToken = default);
}

public sealed class NamedPipeStateSender : IHookMessageTransport
{
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
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);

            await using var client = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var message = HookObservationParser.SerializeTransportMessage(observation) + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(message);
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
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
