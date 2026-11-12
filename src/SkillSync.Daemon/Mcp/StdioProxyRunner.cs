using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon.Mcp;

public static class StdioProxyRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync(string[] originalArgs, CancellationToken ct = default)
    {
        Console.InputEncoding = Utf8NoBom;
        Console.OutputEncoding = Utf8NoBom;

        await using var pipeClient = await TryConnectOrSpawnDaemonAsync(ct).ConfigureAwait(false);

        if (pipeClient is not null && pipeClient.IsConnected)
        {
            await BridgeStdioOverPipeAsync(pipeClient, ct).ConfigureAwait(false);
            return 0;
        }

        // Fallback: if Named Pipe connection could not be established, run in-process MCP handler directly against Canonical store
        await Console.Error.WriteLineAsync("[skillsync] Daemon pipe unavailable; running direct stdio MCP mode.").ConfigureAwait(false);
        var discovery = new ProviderDiscoveryService();
        await using var engine = new SyncEngine(discovery);
        await BootstrapManager.EnsureBootstrapSkillSeededAsync(engine, ct).ConfigureAwait(false);
        await engine.SynchronizeAllAsync(ct).ConfigureAwait(false);

        var rpcHandler = new McpJsonRpcHandler(engine);
        using var stdinReader = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        await using var stdoutWriter = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        while (!ct.IsCancellationRequested)
        {
            var line = await stdinReader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await rpcHandler.ProcessMessageAsync(line, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response))
            {
                await stdoutWriter.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
                await stdoutWriter.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task<NamedPipeClientStream?> TryConnectOrSpawnDaemonAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", NamedPipeMcpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout: 300, ct).ConfigureAwait(false);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        // Attempt to spawn `skillsync daemon` in the background
        TrySpawnBackgroundDaemon();

        for (var i = 0; i < 10; i++)
        {
            var retryPipe = new NamedPipeClientStream(".", NamedPipeMcpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await retryPipe.ConnectAsync(timeout: 300, ct).ConfigureAwait(false);
                return retryPipe;
            }
            catch
            {
                await retryPipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static void TrySpawnBackgroundDaemon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = "daemon --background",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };

            // If running via `dotnet SkillSync.Daemon.dll`, pass the entry assembly path first
            if (processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
                processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(entryAssembly))
                {
                    startInfo.Arguments = $"\"{entryAssembly}\" daemon --background";
                }
            }

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[skillsync] Warning: failed to auto-spawn background daemon: {ex.Message}");
        }
    }

    private static async Task BridgeStdioOverPipeAsync(NamedPipeClientStream pipeClient, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;

        using var stdinReader = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        await using var stdoutWriter = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        using var pipeReader = new StreamReader(pipeClient, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var pipeWriter = new StreamWriter(pipeClient, Utf8NoBom, bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var stdinToPipe = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && pipeClient.IsConnected)
                {
                    var line = await stdinReader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    await pipeWriter.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                    await pipeWriter.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
        }, token);

        var pipeToStdout = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested && pipeClient.IsConnected)
                {
                    var line = await pipeReader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    await stdoutWriter.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                    await stdoutWriter.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
            }
        }, token);

        await Task.WhenAny(stdinToPipe, pipeToStdout).ConfigureAwait(false);
    }
}

