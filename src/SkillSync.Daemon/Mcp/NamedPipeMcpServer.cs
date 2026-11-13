using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Text;
using PipeOptions = System.IO.Pipes.PipeOptions;
using SkillSync.Daemon.Desktop;

namespace SkillSync.Daemon.Mcp;

public sealed class NamedPipeMcpServer : BackgroundService
{
    internal const string ShowWindowCommand = "{\"jsonrpc\":\"2.0\",\"method\":\"skillhub/window/show\"}";
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SkillHubWindowController _window;
    private readonly LocalDaemonIdentity _identity;
    private readonly McpJsonRpcHandler _rpcHandler;
    private readonly ILogger<NamedPipeMcpServer> _logger;

    public NamedPipeMcpServer(McpJsonRpcHandler rpcHandler, ILogger<NamedPipeMcpServer> logger, SkillHubWindowController window,
        SkillSync.Daemon.Watchers.ProviderDiscoveryService discovery)
    {
        _rpcHandler = rpcHandler;
        _logger = logger;
        _window = window;
        _identity = new LocalDaemonIdentity(discovery.UserHomeRoot);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clients = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                clients.RemoveAll(task => task.IsCompleted);
                if (clients.Count >= 16)
                {
                    await Task.WhenAny(clients).WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(_identity.PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                    clients.Add(HandleConnectionAsync(server, stoppingToken));
                    server = null;
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(exception, "Could not accept a local MCP pipe connection.");
                    await Task.Delay(250, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    if (server is not null)
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            var reader = PipeReader.Create(pipe, new StreamPipeReaderOptions(leaveOpen: true));
            try
            {
                using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                var session = new McpSession();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using var send = new SemaphoreSlim(1);
                var requests = new List<Task>();
                async Task DispatchAsync(string line)
                {
                    var response = await _rpcHandler.ProcessMessageAsync(line, session, lifetime.Token).ConfigureAwait(false);
                    if (response is null) return;
                    await send.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try { await writer.WriteLineAsync(response.AsMemory(), lifetime.Token).ConfigureAwait(false); }
                    finally { send.Release(); }
                }

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idle.CancelAfter(TimeSpan.FromMinutes(30));
                        var line = await ReadLineAsync(reader, idle.Token).ConfigureAwait(false);
                        if (line is null) break;
                        foreach (var complete in requests.Where(task => task.IsCompleted).ToArray())
                            await complete.ConfigureAwait(false);
                        requests.RemoveAll(task => task.IsCompletedSuccessfully);
                        if (line == LocalDaemonIdentity.Hello || line == ShowWindowCommand)
                        {
                            if (session.Version is not null || requests.Count > 0) break;
                            if (line == ShowWindowCommand)
                            {
                                _window.RequestShowWindow();
                                break;
                            }
                            await writer.WriteLineAsync(LocalDaemonIdentity.Protocol.AsMemory(), ct).ConfigureAwait(false);
                            continue;
                        }
                        if (requests.Count >= 16 && !McpJsonRpcHandler.IsCancellationNotification(line))
                            throw new InvalidDataException("Too many outstanding MCP requests on this connection.");
                        requests.Add(DispatchAsync(line));
                    }
                }
                finally
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(requests).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or DecoderFallbackException or InvalidOperationException)
            {
                _logger.LogDebug(exception, "The local MCP pipe connection ended.");
            }
            finally
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ReadLineAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            var delimiter = buffer.PositionOf((byte)'\n');
            var line = delimiter is null ? buffer : buffer.Slice(0, delimiter.Value);
            if (line.Length > MaxMessageBytes)
            {
                reader.AdvanceTo(buffer.End);
                throw new InvalidDataException("MCP pipe message exceeds the supported size.");
            }

            if (delimiter is not null || result.IsCompleted)
            {
                var response = delimiter is null && line.IsEmpty ? null : Utf8.GetString(line.ToArray()).TrimEnd('\r');
                reader.AdvanceTo(delimiter is null ? buffer.End : buffer.GetPosition(1, delimiter.Value));
                return response;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}
