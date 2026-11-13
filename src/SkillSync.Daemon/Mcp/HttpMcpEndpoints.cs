using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SkillSync.Daemon.Mcp;

public static class HttpMcpEndpoints
{
    private const int MaxMessageBytes = 1024 * 1024;

    private const string SessionHeader = "Mcp-Session-Id";
    private const string ProtocolHeader = "MCP-Protocol-Version";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);
    private sealed record LegacySession(McpSession Protocol, Channel<string> Messages);
    private sealed record HttpSession(McpSession Protocol, DateTimeOffset Expires);

    public static void MapMcpEndpoints(this WebApplication app)
    {
        var sessions = new ConcurrentDictionary<string, LegacySession>(StringComparer.Ordinal);
        var httpSessions = new ConcurrentDictionary<string, HttpSession>(StringComparer.Ordinal);
        var sessionLock = new object();
        var registrations = new SemaphoreSlim(16);

        app.MapPost("/mcp", async (HttpContext context, McpJsonRpcHandler handler, CancellationToken ct) =>
        {
            var accept = context.Request.GetTypedHeaders().Accept;
            if (!accept.Any(value => value.MediaType.Value == "application/json") ||
                !accept.Any(value => value.MediaType.Value == "text/event-stream"))
            {
                return Results.StatusCode(StatusCodes.Status406NotAcceptable);
            }
            if (context.Request.Headers.TryGetValue(ProtocolHeader, out var version) &&
                (version.Count != 1 || version.ToString() != "2025-06-18"))
            {
                return Results.BadRequest(new { error = "Streamable HTTP uses MCP 2025-06-18." });
            }

            var body = await ReadMessageAsync(context, ct).ConfigureAwait(false);
            if (body.Error is not null) return body.Error;
            var message = body.Message!;
            var id = context.Request.Headers[SessionHeader].ToString();
            if (id.Length > 0)
            {
                if (!httpSessions.TryGetValue(id, out var existing) || existing.Expires <= DateTimeOffset.UtcNow)
                    return Results.NotFound(new { error = "The MCP session expired. Initialize a new session." });
                if (version.Count != 1)
                    return Results.BadRequest(new { error = "MCP-Protocol-Version is required for this session." });
                return await DispatchAsync(message, handler, existing.Protocol, ct).ConfigureAwait(false);
            }

            var method = ReadMethod(message);
            if (method is not (null or "initialize" or "ping"))
                return Results.BadRequest(new { error = "Initialize and include Mcp-Session-Id." });

            var protocol = new McpSession("2025-06-18");
            if (method != "initialize")
                return await DispatchAsync(message, handler, protocol, ct).ConfigureAwait(false);

            lock (sessionLock)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var expired in httpSessions.Where(pair => pair.Value.Expires <= now))
                    httpSessions.TryRemove(expired.Key, out _);
                if (httpSessions.Count >= 32)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                id = Guid.NewGuid().ToString("N");
                httpSessions[id] = new HttpSession(protocol, now + SessionLifetime);
            }

            var response = await DispatchAsync(message, handler, protocol, ct).ConfigureAwait(false);
            if (protocol.Version is null)
                httpSessions.TryRemove(id, out _);
            else
                context.Response.Headers[SessionHeader] = id;
            return response;
        });

        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapDelete("/mcp", (HttpContext context) =>
        {
            if (context.Request.Headers[ProtocolHeader].ToString() != "2025-06-18")
                return Results.BadRequest(new { error = "MCP-Protocol-Version is required." });
            return httpSessions.TryRemove(context.Request.Headers[SessionHeader].ToString(), out _)
                ? Results.NoContent() : Results.NotFound();
        });

        app.MapGet("/sse", async (HttpContext context, CancellationToken ct) =>
        {
            if (!await registrations.WaitAsync(0, ct).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            var id = Guid.NewGuid().ToString("N");
            var messages = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            sessions[id] = new LegacySession(new McpSession("2024-11-05"), messages);
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sessionLifetime.CancelAfter(SessionLifetime);
            var token = sessionLifetime.Token;
            try
            {
                await context.Response.WriteAsync($"event: endpoint\ndata: /message?sessionId={id}\n\n", token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                await foreach (var message in messages.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    await context.Response.WriteAsync($"event: message\ndata: {message}\n\n", token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                sessions.TryRemove(id, out _);
                messages.Writer.TryComplete();
                registrations.Release();
            }
        });

        app.MapPost("/message", async (HttpContext context, McpJsonRpcHandler handler, CancellationToken ct) =>
        {
            if (!sessions.TryGetValue(context.Request.Query["sessionId"].ToString(), out var session))
            {
                return Results.NotFound(new { error = "The SSE session is unavailable." });
            }
            var body = await ReadMessageAsync(context, ct).ConfigureAwait(false);
            if (body.Error is not null) return body.Error;
            if (session.Messages.Reader.Completion.IsCompleted)
                return Results.NotFound(new { error = "The SSE session is unavailable." });
            return await DispatchAsync(body.Message!, handler, session.Protocol, ct, session.Messages).ConfigureAwait(false);
        });
    }

    private static string? ReadMethod(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String ? method.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<IResult> DispatchAsync(string message, McpJsonRpcHandler handler, McpSession protocol,
        CancellationToken ct, Channel<string>? messages = null)
    {
        var response = await handler.ProcessMessageAsync(message, protocol, ct).ConfigureAwait(false);
        if (response is null) return Results.Accepted();
        if (messages is null) return Results.Text(response, "application/json; charset=utf-8");
        if (messages.Writer.TryWrite(response)) return Results.Accepted();
        messages.Writer.TryComplete();
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<(string? Message, IResult? Error)> ReadMessageAsync(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType())
        {
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        try
        {
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > MaxMessageBytes)
                {
                    return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
                }
                content.Write(buffer, 0, read);
            }

            var message = new UTF8Encoding(false, true).GetString(content.GetBuffer(), 0, (int)content.Length);
            return string.IsNullOrWhiteSpace(message)
                ? (null, Results.BadRequest(new { error = "Empty JSON-RPC body." }))
                : (message, null);
        }
        catch (DecoderFallbackException)
        {
            return (null, Results.BadRequest(new { error = "MCP messages must contain valid UTF-8." }));
        }
    }
}
