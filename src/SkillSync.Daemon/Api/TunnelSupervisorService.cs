using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SkillSync.Daemon.Api;

public sealed partial class TunnelSupervisorService : IHostedService, IDisposable
{
    private readonly ILogger<TunnelSupervisorService> _logger;
    private readonly string _accessToken;
    private readonly int _localPort;
    private readonly bool _startOnBoot;
    private readonly object _lock = new();
    private Process? _cloudflaredProcess;
    private string? _publicBaseUrl;
    private string _status = "stopped";
    private string? _lastError;

    public TunnelSupervisorService(
        ILogger<TunnelSupervisorService> logger,
        int localPort = 45880,
        bool startOnBoot = false)
    {
        _logger = logger;
        _localPort = localPort;
        _startOnBoot = startOnBoot;
        _accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public string AccessToken => _accessToken;
    public string Status => _status;
    public string? PublicBaseUrl => _publicBaseUrl;
    public string? PublicMcpUrl => _publicBaseUrl is not null ? $"{_publicBaseUrl}/mcp?token={_accessToken}" : null;
    public string? LastError => _lastError;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_startOnBoot)
        {
            _ = TryStartTunnel();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopTunnel();
        return Task.CompletedTask;
    }

    public bool ValidateRequestAuthorization(HttpContext context)
    {
        // Local loopback requests without Cloudflare proxy headers are trusted
        var hasCfHeader = context.Request.Headers.ContainsKey("CF-Connecting-IP")
                       || context.Request.Headers.ContainsKey("CF-Ray");

        if (!hasCfHeader)
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is null || System.Net.IPAddress.IsLoopback(remoteIp))
            {
                return true;
            }
        }

        // External / Cloudflare requests MUST present the cryptographic token
        var queryToken = context.Request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(queryToken) && FixedTimeEquals(queryToken, _accessToken))
        {
            return true;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader["Bearer ".Length..].Trim();
            if (FixedTimeEquals(bearer, _accessToken))
            {
                return true;
            }
        }

        return false;
    }

    public (bool Started, string Message) TryStartTunnel()
    {
        lock (_lock)
        {
            if (_cloudflaredProcess is { HasExited: false })
            {
                return (true, "Cloudflare Quick Tunnel is already running.");
            }

            _publicBaseUrl = null;
            _lastError = null;
            _status = "starting";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cloudflared",
                    Arguments = $"tunnel --url http://127.0.0.1:{_localPort} --no-autoupdate",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                var proc = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                proc.OutputDataReceived += (_, e) => InspectCloudflaredLine(e.Data);
                proc.ErrorDataReceived += (_, e) => InspectCloudflaredLine(e.Data);
                proc.Exited += (_, _) =>
                {
                    lock (_lock)
                    {
                        _status = "stopped";
                        _publicBaseUrl = null;
                    }
                };

                if (!proc.Start())
                {
                    _status = "error";
                    _lastError = "Failed to start cloudflared process.";
                    return (false, _lastError);
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                _cloudflaredProcess = proc;
                _logger.LogInformation("Started cloudflared quick tunnel targeting http://127.0.0.1:{Port}", _localPort);
                return (true, "Cloudflare Quick Tunnel process started.");
            }
            catch (Exception ex)
            {
                _status = "unavailable";
                _lastError = $"Could not launch 'cloudflared' ({ex.Message}). Install cloudflared and ensure it is in your PATH.";
                _logger.LogWarning("Could not start cloudflared tunnel: {Error}", _lastError);
                return (false, _lastError);
            }
        }
    }

    public void StopTunnel()
    {
        lock (_lock)
        {
            if (_cloudflaredProcess is not null)
            {
                try
                {
                    if (!_cloudflaredProcess.HasExited)
                    {
                        _cloudflaredProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
                finally
                {
                    _cloudflaredProcess.Dispose();
                    _cloudflaredProcess = null;
                }
            }

            _publicBaseUrl = null;
            _status = "stopped";
        }
    }

    private void InspectCloudflaredLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var match = TryCloudflareUrlRegex().Match(line);
        if (match.Success)
        {
            lock (_lock)
            {
                _publicBaseUrl = match.Value.TrimEnd('/');
                _status = "running";
                _logger.LogInformation("Cloudflare Quick Tunnel active: {PublicMcpUrl}", PublicMcpUrl);
            }
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    public void Dispose()
    {
        StopTunnel();
    }

    [GeneratedRegex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.CultureInvariant)]
    private static partial Regex TryCloudflareUrlRegex();
}

