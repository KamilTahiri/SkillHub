using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SkillSync.Daemon.Mcp;

internal sealed class LocalDaemonIdentity
{
    public const string Protocol = "skillhub-mcp-v1";
    public const string Hello = "{\"skillhub_pipe\":1,\"action\":\"hello\"}";

    public LocalDaemonIdentity(string? home = null)
    {
        Home = string.IsNullOrWhiteSpace(home) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? identity.Name;
        var root = Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + "\n" + root.ToUpperInvariant())))[..24];
        PipeName = Protocol + "-" + key;
        MutexName = @"Global\SkillHub_Singleton_Daemon_Mutex_" + key;
    }

    public string? Home { get; }
    public string PipeName { get; }
    public string MutexName { get; }

    public NamedPipeClientStream ConnectStream() => new(".", PipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
