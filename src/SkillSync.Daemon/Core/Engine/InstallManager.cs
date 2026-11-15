using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SkillSync.Daemon.Core.Engine;

public sealed record McpClientConfigTarget(
    string ClientId,
    string DisplayName,
    string ConfigFilePath,
    bool ParentDirectoryExists,
    bool ConfigFileExists,
    bool IsSkillHubRegistered,
    string? Error = null
);

public sealed record InstallStatusReport(
    bool IsAutoStartRegistered,
    string AutoStartMethod,
    string AutoStartTarget,
    string ExecutablePath,
    IReadOnlyList<McpClientConfigTarget> McpClientConfigs
);

public interface IDesktopStartup
{
    bool IsRegistered { get; }
    (bool Success, string Method, string Details) RegisterOsAutoStart(string? overrideExecutablePath = null, int port = 45880);
    (bool Success, string Details) UnregisterOsAutoStart();
}

public sealed partial class InstallManager : IDesktopStartup
{
    public const string McpServerKey = "skillhub";
    public const string LegacyMcpServerKey = "skillsync";

    private const string WindowsRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsRunValueName = "SkillHub";
    private const string LegacyWindowsRunValueName = "SkillSyncDaemon";
    private const string MacLaunchAgentFileName = "com.skillhub.daemon.plist";
    private const string LinuxSystemdServiceName = "skillhub.service";

    private readonly string _userHomeRoot;
    private readonly string _appDataRoot;
    private readonly bool _hasCustomHome;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _restartPending = new(StringComparer.Ordinal);

    /// <summary>True after SkillHub changed this client's MCP configuration and the user has not confirmed a client restart.</summary>
    public bool IsRestartPending(string clientId) => _restartPending.ContainsKey(clientId);

    public InstallManager(string? customUserHomeRoot = null, string? customAppDataRoot = null)
    {
        _hasCustomHome = !string.IsNullOrWhiteSpace(customUserHomeRoot);
        _userHomeRoot = !_hasCustomHome
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(customUserHomeRoot!);

        _appDataRoot = string.IsNullOrWhiteSpace(customAppDataRoot)
            ? _hasCustomHome ? Path.Combine(_userHomeRoot, "AppData", "Roaming") :
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.GetFullPath(customAppDataRoot);
    }

    public static string GetDefaultLocalInstallDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SkillHub");
    }

    public static void AttachParentConsoleIfAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = AttachConsole(-1); // ATTACH_PARENT_PROCESS
            }
            catch
            {
            }
        }
    }

    public static void DetachConsoleIfBackgroundOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = FreeConsole();
            }
            catch
            {
            }
        }
    }

    public string ResolveExecutablePath(string? overrideExecutablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideExecutablePath))
        {
            return Path.GetFullPath(overrideExecutablePath);
        }

        var currentProcess = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcess) &&
            !currentProcess.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !currentProcess.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(currentProcess);
        }

        var entryAssemblyLocation = Path.Combine(AppContext.BaseDirectory, "SkillHub.dll");
        if (File.Exists(entryAssemblyLocation))
        {
            var dir = AppContext.BaseDirectory;
            var exeName = OperatingSystem.IsWindows() ? "SkillHub.exe" : "SkillHub";
            var candidateExe = Path.Combine(dir, exeName);
            if (File.Exists(candidateExe))
            {
                return Path.GetFullPath(candidateExe);
            }

            return Path.GetFullPath(entryAssemblyLocation);
        }

        return "SkillHub";
    }

    public (string Command, string[] Args) BuildMcpStdioCommandSpec(string? overrideExecutablePath = null)
    {
        var resolved = ResolveExecutablePath(overrideExecutablePath);
        string[] args = _hasCustomHome ? ["mcp-stdio", "--home", _userHomeRoot] : ["mcp-stdio"];
        return resolved.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", [resolved, .. args]) : (resolved, args);
    }

    public string BuildDaemonLaunchCommandLine(string? overrideExecutablePath = null, int port = 45880)
    {
        var resolved = ResolveExecutablePath(overrideExecutablePath);
        var homeFlag = _hasCustomHome ? $" --home \"{_userHomeRoot}\"" : "";
        if (resolved.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"dotnet \"{resolved}\" --background --port {port}{homeFlag}";
        }

        return $"\"{resolved}\" --background --port {port}{homeFlag}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
