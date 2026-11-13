using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace SkillSync.Daemon.Mcp;

internal sealed class McpSession(string? transportVersion = null)
{
    private readonly object _state = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private string? _version;
    private bool _initialized;

    public string? TransportVersion { get; } = transportVersion;

    public string? Version { get { lock (_state) return _version; } }

    public bool TryInitialize(string version)
    {
        lock (_state)
        {
            if (_version is not null) return false;
            _version = version;
            return true;
        }
    }

    public void MarkInitialized()
    {
        lock (_state) _initialized = _version is not null;
    }

    public bool IsReady { get { lock (_state) return _initialized; } }

    public bool Track(JsonNode id, CancellationTokenSource request) => _requests.TryAdd(id.ToJsonString(), request);
    public void Complete(JsonNode id) => _requests.TryRemove(id.ToJsonString(), out _);

    public void Cancel(JsonNode? id)
    {
        if (id is not JsonValue || !_requests.TryGetValue(id.ToJsonString(), out var request)) return;
        try { request.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
