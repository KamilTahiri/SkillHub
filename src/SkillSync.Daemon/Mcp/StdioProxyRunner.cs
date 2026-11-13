using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SkillSync.Daemon.Mcp;

public static class StdioProxyRunner
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var homeIndex = Array.FindIndex(args, value => value.Equals("--home", StringComparison.OrdinalIgnoreCase));
        var identity = new LocalDaemonIdentity(homeIndex >= 0 && homeIndex + 1 < args.Length ? args[homeIndex + 1] : null);
        var portIndex = Array.FindIndex(args, value => value.Equals("--port", StringComparison.OrdinalIgnoreCase));
        int? port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsedPort)
            && parsedPort is >= 0 and <= 65535 ? parsedPort : null;
        try
        {
            await using var pipe = await ConnectOrStartAsync(identity, port, ct).ConfigureAwait(false);
            if (pipe is null)
            {
                await Console.Error.WriteLineAsync("SkillHub's local daemon is unavailable. Open SkillHub and retry.").ConfigureAwait(false);
                return 1;
            }
            return await BridgeAsync(pipe, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (IOException exception)
        {
            await Console.Error.WriteLineAsync("SkillHub MCP connection closed: " + exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectOrStartAsync(LocalDaemonIdentity identity, int? port, CancellationToken ct)
    {
        var connected = await ProbeAsync(identity, 200, ct).ConfigureAwait(false);
        if (connected is not null) return connected;
        TryStart(identity, port);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            connected = await ProbeAsync(identity, 250, ct).ConfigureAwait(false);
            if (connected is not null) return connected;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return null;
    }

    internal static async Task<NamedPipeClientStream?> ProbeAsync(LocalDaemonIdentity identity, int timeout, CancellationToken ct)
    {
        var stream = identity.ConnectStream();
        var keep = false;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(timeout);
            await stream.ConnectAsync(timeout, budget.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(stream, Utf8, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
            using var reader = new StreamReader(stream, Utf8, leaveOpen: true);
            await writer.WriteLineAsync(LocalDaemonIdentity.Hello.AsMemory(), budget.Token).ConfigureAwait(false);
            keep = await reader.ReadLineAsync(budget.Token).ConfigureAwait(false) == LocalDaemonIdentity.Protocol;
            return keep ? stream : null;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException ||
            exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (!keep) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void TryStart(LocalDaemonIdentity identity, int? port)
    {
        var process = Environment.ProcessPath;
        if (process is null) return;
        try
        {
            // Windows can inherit the caller's original handles even when the child has redirected streams.
            foreach (var streamId in new[] { -10, -11, -12 })
            {
                var handle = GetStdHandle(streamId);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1) && !SetHandleInformation(handle, 1, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            // Give the daemon separate streams so it cannot keep the MCP client's stdio pipes open.
            var info = new ProcessStartInfo(process)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SkillHub.dll"));
            }
            info.ArgumentList.Add("daemon");
            info.ArgumentList.Add("--background");
            if (identity.Home is { } home)
            {
                info.ArgumentList.Add("--home");
                info.ArgumentList.Add(home);
            }
            if (port is not null || identity.Home is not null)
            {
                info.ArgumentList.Add("--port");
                info.ArgumentList.Add((port ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            using var started = Process.Start(info);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not start SkillHub: " + exception.Message);
        }
    }

    private static async Task<int> BridgeAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8);
        await using var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8) { NewLine = "\n", AutoFlush = true };
        using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        var pending = 0;
        var inputEnded = 0;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task SendAsync()
        {
            while (await stdin.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (HasId(line)) Interlocked.Increment(ref pending);
                await writer.WriteLineAsync(line.AsMemory(), lifetime.Token).ConfigureAwait(false);
            }
            Volatile.Write(ref inputEnded, 1);
            if (Volatile.Read(ref pending) <= 0) drained.TrySetResult();
        }
        async Task ReceiveAsync()
        {
            while (await reader.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                await stdout.WriteLineAsync(line.AsMemory(), lifetime.Token).ConfigureAwait(false);
                if (HasId(line)) Interlocked.Decrement(ref pending);
                if (Volatile.Read(ref inputEnded) == 1 && Volatile.Read(ref pending) <= 0) drained.TrySetResult();
            }
        }

        var send = SendAsync();
        var receive = ReceiveAsync();
        try
        {
            var first = await Task.WhenAny(send, receive).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first == send)
            {
                var drainedOrDisconnected = await Task.WhenAny(drained.Task, receive)
                    .WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                await drainedOrDisconnected.ConfigureAwait(false);
                if (Volatile.Read(ref pending) > 0)
                    throw new IOException("The daemon disconnected before all messages were answered.");
            }
            else if (Volatile.Read(ref inputEnded) == 0 || Volatile.Read(ref pending) > 0)
            {
                throw new IOException("The daemon disconnected before all messages were answered.");
            }
            return 0;
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("SkillHub timed out while waiting for pending MCP responses.").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int streamId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    private static bool HasId(string text)
    {
        try
        {
            using var value = JsonDocument.Parse(text);
            return value.RootElement.ValueKind == JsonValueKind.Object && value.RootElement.TryGetProperty("id", out _);
        }
        catch (JsonException) { return true; }
    }
}
