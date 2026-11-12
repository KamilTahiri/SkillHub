using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using SkillSync.Daemon.Api;

namespace SkillSync.Daemon.Mcp;

public static class HttpMcpEndpoints
{
    private static readonly ConcurrentDictionary<string, Channel<string>> ActiveSseSessions = new(StringComparer.Ordinal);

    public static void MapMcpEndpoints(this WebApplication app)
    {
        // 1. Streamable HTTP MCP Endpoint (POST /mcp) - MCP 2025-03-26 standard
        app.MapPost("/mcp", async (
            HttpContext context,
            McpJsonRpcHandler rpcHandler,
            TunnelSupervisorService tunnelSupervisor,
            CancellationToken ct) =>
        {
            if (!tunnelSupervisor.ValidateRequestAuthorization(context))
            {
                return Results.Unauthorized();
            }

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return Results.BadRequest(new { error = "Empty JSON-RPC body" });
            }

            var responseJson = await rpcHandler.ProcessMessageAsync(body, ct).ConfigureAwait(false);
            if (responseJson is null)
            {
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            return Results.Text(responseJson, "application/json; charset=utf-8");
        });

        // 2. Streamable HTTP GET /mcp & Legacy SSE /sse Endpoint (MCP 2024-11-05 standard)
        app.MapGet("/mcp", HandleSseConnectionAsync);
        app.MapGet("/sse", HandleSseConnectionAsync);

        // 3. Legacy SSE POST /message Endpoint
        app.MapPost("/message", async (
            HttpContext context,
            McpJsonRpcHandler rpcHandler,
            TunnelSupervisorService tunnelSupervisor,
            CancellationToken ct) =>
        {
            if (!tunnelSupervisor.ValidateRequestAuthorization(context))
            {
                return Results.Unauthorized();
            }

            var sessionId = context.Request.Query["sessionId"].ToString();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var responseJson = await rpcHandler.ProcessMessageAsync(body, ct).ConfigureAwait(false);
            if (responseJson is not null)
            {
                if (!string.IsNullOrEmpty(sessionId) && ActiveSseSessions.TryGetValue(sessionId, out var channel))
                {
                    await channel.Writer.WriteAsync(responseJson, ct).ConfigureAwait(false);
                    return Results.Accepted();
                }

                return Results.Text(responseJson, "application/json; charset=utf-8");
            }

            return Results.Accepted();
        });
    }

    private static async Task HandleSseConnectionAsync(
        HttpContext context,
        TunnelSupervisorService tunnelSupervisor,
        CancellationToken ct)
    {
        if (!tunnelSupervisor.ValidateRequestAuthorization(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        var sessionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<string>();
        ActiveSseSessions[sessionId] = channel;

        try
        {
            var tokenParam = context.Request.Query["token"].ToString();
            var endpointUrl = string.IsNullOrEmpty(tokenParam)
                ? $"/message?sessionId={sessionId}"
                : $"/message?sessionId={sessionId}&token={Uri.EscapeDataString(tokenParam)}";

            await context.Response.WriteAsync($"event: endpoint\ndata: {endpointUrl}\n\n", ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            await foreach (var msg in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await context.Response.WriteAsync($"event: message\ndata: {msg}\n\n", ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ActiveSseSessions.TryRemove(sessionId, out _);
            channel.Writer.TryComplete();
        }
    }
}

