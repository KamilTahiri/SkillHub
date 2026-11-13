using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SkillSync.Daemon.Api;

public sealed class LocalHttpAccess
{
    public const string DesktopHeader = "X-SkillHub-Session";
    private readonly string? _mcpToken;
    public string DesktopToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public LocalHttpAccess(string? mcpToken = null)
    {
        if (!string.IsNullOrWhiteSpace(mcpToken) && mcpToken.Length < 32)
        {
            throw new ArgumentException("The local HTTP MCP token must be at least 32 characters.");
        }
        _mcpToken = string.IsNullOrWhiteSpace(mcpToken) ? null : mcpToken;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var connection = context.Connection;
        var expectedHost = $"127.0.0.1:{connection.LocalPort}";
        var origin = context.Request.Headers.Origin;
        if (connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer) ||
            !string.Equals(context.Request.Host.Value, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            origin.Count > 0 && (origin.Count != 1 || origin[0] != $"{context.Request.Scheme}://{expectedHost}") ||
            context.Request.Headers["Sec-Fetch-Site"].Any(value => value is "cross-site" or "same-site"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var path = context.Request.Path;
        var authorized = true;
        if (path.StartsWithSegments("/api/v1"))
        {
            authorized = HasToken(context.Request.Headers[DesktopHeader], DesktopToken);
        }
        else if (path.StartsWithSegments("/mcp") || path.StartsWithSegments("/sse") || path.StartsWithSegments("/message"))
        {
            authorized = _mcpToken is not null && HasToken(context.Request.Headers.Authorization, $"Bearer {_mcpToken}");
        }

        if (!authorized)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
            "connect-src 'self'; font-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";
        await next(context).ConfigureAwait(false);
    }

    private static bool HasToken(Microsoft.Extensions.Primitives.StringValues headers, string expected) =>
        headers.Count == 1 && headers[0] is { } value &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(expected));
}
