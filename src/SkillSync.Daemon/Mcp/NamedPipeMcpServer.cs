using System.IO.Pipes;
using System.Text;

namespace SkillSync.Daemon.Mcp;

public sealed class NamedPipeMcpServer : BackgroundService
{
    public const string PipeName = "skillhub-mcp-v1";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly McpJsonRpcHandler _rpcHandler;
    private readonly ILogger<NamedPipeMcpServer> _logger;

    public NamedPipeMcpServer(McpJsonRpcHandler rpcHandler, ILogger<NamedPipeMcpServer> logger)
    {
        _rpcHandler = rpcHandler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NamedPipeMcpServer listening on pipe '{PipeName}'", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var serverStream = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await serverStream.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientConnectionAsync(serverStream, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await serverStream.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accepting Named Pipe connection.");
                await serverStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientConnectionAsync(NamedPipeServerStream pipeStream, CancellationToken ct)
    {
        await using (pipeStream)
        {
            using var reader = new StreamReader(pipeStream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipeStream, Utf8NoBom, bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            try
            {
                while (!ct.IsCancellationRequested && pipeStream.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var response = await _rpcHandler.ProcessMessageAsync(line, ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(response))
                    {
                        await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
                        await writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Client disconnected
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unhandled error in NamedPipe MCP session.");
            }
        }
    }
}

