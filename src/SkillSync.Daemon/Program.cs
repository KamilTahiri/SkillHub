using System.IO.Pipes;
using System.Windows.Forms;
using SkillSync.Daemon.Api;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Desktop;
using SkillSync.Daemon.Mcp;
using SkillSync.Daemon.Watchers;

namespace SkillSync.Daemon;

public static class Program
{
    private const string GlobalMutexName = @"Global\SkillHub_Singleton_Daemon_Mutex";

    [STAThread]
    public static int Main(string[] args)
    {
        var subcommand = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "app";

        if (subcommand == "generate-ico" && args.Length > 1)
        {
            SkillHubIconRenderer.SaveIconFile(Path.GetFullPath(args[1]));
            return 0;
        }

        if (subcommand == "mcp-stdio")
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            return StdioProxyRunner.RunAsync(args, cts.Token).GetAwaiter().GetResult();
        }

        var port = 45880;
        var enableTunnel = false;
        var isBackground = false;
        var launchDesktopAfterInstall = false;
        var noSpawnOnInstall = false;
        string? customHome = null;
        string? binPathOverride = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
                i++;
            }
            else if (string.Equals(arg, "--tunnel", StringComparison.OrdinalIgnoreCase))
            {
                enableTunnel = true;
            }
            else if (string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase))
            {
                isBackground = true;
            }
            else if (string.Equals(arg, "--launch-ui", StringComparison.OrdinalIgnoreCase))
            {
                launchDesktopAfterInstall = true;
            }
            else if (string.Equals(arg, "--no-spawn", StringComparison.OrdinalIgnoreCase))
            {
                noSpawnOnInstall = true;
            }
            else if (string.Equals(arg, "--home", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                customHome = args[i + 1];
                i++;
            }
            else if (string.Equals(arg, "--bin-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                binPathOverride = args[i + 1];
                i++;
            }
            else if (string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase))
            {
                subcommand = "install";
            }
            else if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                subcommand = "uninstall";
            }
        }

        if (subcommand == "install")
        {
            InstallManager.AttachParentConsoleIfAvailable();
            return RunInstallCommandAsync(customHome, binPathOverride, port, launchDesktopAfterInstall, noSpawnOnInstall).GetAwaiter().GetResult();
        }

        if (subcommand == "uninstall")
        {
            InstallManager.AttachParentConsoleIfAvailable();
            return RunUninstallCommand(customHome);
        }

        if (subcommand == "status")
        {
            InstallManager.AttachParentConsoleIfAvailable();
            return RunStatusCommandAsync(customHome, binPathOverride, port).GetAwaiter().GetResult();
        }

        return RunDesktopAndDaemonApp(args, port, enableTunnel, isBackground, customHome);
    }

    private static async Task<int> RunInstallCommandAsync(
        string? customHome,
        string? binPathOverride,
        int port,
        bool launchUi,
        bool noSpawn = false)
    {
        Console.WriteLine("=== SkillHub Desktop & MCP Installer ===");

        // Stop any existing running daemon processes so files can be updated cleanly
        InstallManager.StopRunningDaemonProcesses();

        var discovery = new ProviderDiscoveryService(customHome);
        var installManager = new InstallManager(customHome);
        var resolvedExe = installManager.ResolveExecutablePath(binPathOverride);
        var exeDir = Path.GetDirectoryName(resolvedExe);

        // Save custom SkillHub .ico next to the executable for Desktop & Start Menu shortcuts
        if (!string.IsNullOrEmpty(exeDir) && Directory.Exists(exeDir))
        {
            var oldIco = Path.Combine(exeDir, "skillhub.ico");
            if (File.Exists(oldIco))
            {
                try { File.Delete(oldIco); } catch { }
            }
            SkillHubIconRenderer.SaveIconFile(Path.Combine(exeDir, "skillhub-v2.ico"));
        }

        // 1. Seed bootstrap skill & synchronize canonical store (including ~/.codex/skills)
        await using (var engine = new SyncEngine(discovery))
        {
            await BootstrapManager.EnsureBootstrapSkillSeededAsync(engine).ConfigureAwait(false);
            await engine.SynchronizeAllAsync().ConfigureAwait(false);
            var total = engine.ListSkills().Count;
            Console.WriteLine($"[1/5] Canonical store initialized at: {discovery.CanonicalSkillsRoot}");
            Console.WriteLine($"      Synced {total} skill(s) across all detected AI providers.");
        }

        // 2. Create Windows Start Menu & Desktop Shortcuts
        if (OperatingSystem.IsWindows())
        {
            InstallManager.CreateWindowsShortcuts(resolvedExe);
            Console.WriteLine("[2/5] Created 'SkillHub' shortcuts on Desktop and Windows Start Menu.");
        }

        // 3. Register OS Auto-Start on Login
        var (autoOk, method, details) = installManager.RegisterOsAutoStart(resolvedExe, port);
        if (autoOk)
        {
            Console.WriteLine($"[3/5] OS Auto-Start registered via {method}:");
            Console.WriteLine($"      -> {details}");
        }
        else
        {
            Console.WriteLine($"[3/5] Warning: Could not register OS Auto-Start ({method}): {details}");
        }

        // 4. Inject MCP Server configuration into all detected AI clients (including Codex config.toml)
        var clientResults = installManager.InjectMcpConfigIntoAllDetectedClients(resolvedExe);
        var registeredClients = clientResults.Where(c => c.IsSkillHubRegistered).ToList();
        Console.WriteLine($"[4/5] Injected 'skillhub' MCP server into {registeredClients.Count} AI client config(s):");
        foreach (var c in clientResults)
        {
            var badge = c.IsSkillHubRegistered ? "REGISTERED" : "SKIPPED (not installed)";
            Console.WriteLine($"      - {c.DisplayName,-26} [{badge}] ({c.ConfigFilePath})");
        }

        // 5. Start SkillHub (either showing the Desktop Window or running in the System Tray)
        if (!noSpawn)
        {
            var spawned = InstallManager.SpawnDetachedBackgroundDaemon(resolvedExe, port, showWindow: launchUi);
            Console.WriteLine(spawned
                ? $"[5/5] SkillHub started {(launchUi ? "with Desktop Window & System Tray" : "in System Tray") }."
                : "[5/5] SkillHub ready.");
        }
        else
        {
            Console.WriteLine("[5/5] Configuration complete.");
        }

        Console.WriteLine("\nInstallation complete!");
        return 0;
    }

    private static int RunUninstallCommand(string? customHome)
    {
        Console.WriteLine("=== SkillHub Uninstaller ===");
        var installManager = new InstallManager(customHome);

        var (autoOk, autoDetails) = installManager.UnregisterOsAutoStart();
        Console.WriteLine($"[1/4] OS Auto-Start removal: {(autoOk ? "OK" : "Warning")} ({autoDetails})");

        if (OperatingSystem.IsWindows())
        {
            InstallManager.RemoveWindowsShortcuts();
            Console.WriteLine("[2/4] Removed Desktop and Start Menu shortcuts.");
        }

        var clients = installManager.RemoveMcpConfigFromAllClients();
        Console.WriteLine("[3/4] Removed 'skillhub' from AI client MCP configuration files:");
        foreach (var c in clients.Where(x => x.ConfigFileExists))
        {
            Console.WriteLine($"      - Cleaned: {c.DisplayName} ({c.ConfigFilePath})");
        }

        var stopped = InstallManager.StopRunningDaemonProcesses();
        Console.WriteLine($"[4/4] Stopped {stopped} running SkillHub process(es).");
        Console.WriteLine("\nUninstallation complete. (Your canonical skills in ~/.skillhub/skills were preserved.)");
        return 0;
    }

    private static async Task<int> RunStatusCommandAsync(string? customHome, string? binPathOverride, int port)
    {
        var discovery = new ProviderDiscoveryService(customHome);
        var installManager = new InstallManager(customHome);
        var report = installManager.GetStatusReport(binPathOverride);
        var daemonAlive = await IsDaemonPipeAliveAsync().ConfigureAwait(false);

        Console.WriteLine("=== SkillHub System Status ===");
        Console.WriteLine($"Background Service    : {(daemonAlive ? "ONLINE (skillhub-mcp-v1)" : "OFFLINE")}");
        Console.WriteLine($"Canonical Store       : {discovery.CanonicalSkillsRoot}");
        Console.WriteLine($"OS Auto-Start         : {(report.IsAutoStartRegistered ? "REGISTERED" : "NOT REGISTERED")} ({report.AutoStartMethod})");
        Console.WriteLine($"Auto-Start Target     : {report.AutoStartTarget}");
        Console.WriteLine("\n--- AI Provider Skill Directories ---");
        foreach (var p in discovery.DiscoverProviders())
        {
            Console.WriteLine($"  - {p.DisplayName,-32} [{(p.IsInstalled ? "DETECTED" : "MISSING")}] -> {p.SkillsRootDirectory}");
        }

        Console.WriteLine("\n--- AI Client MCP Configurations ---");
        foreach (var c in report.McpClientConfigs)
        {
            var state = c.IsSkillHubRegistered ? "MCP REGISTERED" : (c.ConfigFileExists ? "NOT REGISTERED" : "FILE NOT PRESENT");
            Console.WriteLine($"  - {c.DisplayName,-32} [{state}] -> {c.ConfigFilePath}");
        }

        return 0;
    }

    private static int RunDesktopAndDaemonApp(
        string[] args,
        int port,
        bool enableTunnel,
        bool isBackground,
        string? customHome)
    {
        using var mutex = new Mutex(initiallyOwned: true, name: GlobalMutexName, out var createdNew);
        if (!createdNew)
        {
            // An instance is already running! If the user launched SkillHub interactively (not --background),
            // signal the running instance to pop up its Desktop window and focus it.
            if (!isBackground)
            {
                TrySignalRunningInstanceToShowWindow(port);
            }

            return 0;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Logging.ClearProviders();

        var wwwrootCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "SkillSync.Daemon", "wwwroot")
        };
        var resolvedWwwroot = wwwrootCandidates.FirstOrDefault(Directory.Exists) ?? wwwrootCandidates[0];
        Directory.CreateDirectory(resolvedWwwroot);
        builder.Environment.WebRootPath = resolvedWwwroot;

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var windowController = new SkillHubWindowController();
        builder.Services.AddSingleton(windowController);
        builder.Services.AddSingleton(_ => new ProviderDiscoveryService(customHome));
        builder.Services.AddSingleton(_ => new InstallManager(customHome));
        builder.Services.AddSingleton<SyncEngine>();
        builder.Services.AddSingleton<SkillImporterService>();
        builder.Services.AddSingleton<McpJsonRpcHandler>();
        builder.Services.AddSingleton(sp =>
            new MultiRootFileWatcher(
                sp.GetRequiredService<ProviderDiscoveryService>(),
                sp.GetRequiredService<SyncEngine>(),
                sp.GetRequiredService<ILogger<MultiRootFileWatcher>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MultiRootFileWatcher>());
        builder.Services.AddHostedService<NamedPipeMcpServer>();

        builder.Services.AddSingleton(sp =>
            new TunnelSupervisorService(
                sp.GetRequiredService<ILogger<TunnelSupervisorService>>(),
                localPort: port,
                startOnBoot: enableTunnel));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TunnelSupervisorService>());

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path == "/" ||
                path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
                ctx.Context.Response.Headers.Pragma = "no-cache";
                ctx.Context.Response.Headers.Expires = "0";
            }
        });

        app.MapSkillRestApi(port);
        app.MapMcpEndpoints();

        // Start Kestrel + File Watcher + MCP Server asynchronously in the background
        app.StartAsync().GetAwaiter().GetResult();

        // Run native Windows Forms + WebView2 + System Tray loop on the main STA thread
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var syncEngine = app.Services.GetRequiredService<SyncEngine>();
        using var mainForm = new SkillHubMainForm(
            appUrl: $"http://127.0.0.1:{port}",
            startHiddenInTray: isBackground,
            syncEngine: syncEngine,
            windowController: windowController,
            stopHostCallback: async () =>
            {
                await app.StopAsync().ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            });

        Application.Run(mainForm);
        return 0;
    }

    private static void TrySignalRunningInstanceToShowWindow(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            _ = http.PostAsync($"http://127.0.0.1:{port}/api/v1/window/show", content).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private static async Task<bool> IsDaemonPipeAliveAsync()
    {
        await using var client = new NamedPipeClientStream(".", NamedPipeMcpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(timeout: 250).ConfigureAwait(false);
            return client.IsConnected;
        }
        catch
        {
            return false;
        }
    }
}
