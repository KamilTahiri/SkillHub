using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SkillSync.Daemon.Core.Engine;

namespace SkillSync.Daemon.Desktop;

public sealed class SkillHubWindowController
{
    private Action? _showWindowCallback;
    private Action? _quitAppCallback;
    private Action<bool>? _setThemeCallback;

    public void RegisterCallbacks(Action showWindow, Action quitApp, Action<bool>? setTheme = null)
    {
        _showWindowCallback = showWindow;
        _quitAppCallback = quitApp;
        _setThemeCallback = setTheme;
    }

    public void RequestShowWindow() => _showWindowCallback?.Invoke();
    public void RequestQuitApplication() => _quitAppCallback?.Invoke();
    public void RequestSetTheme(bool isDark) => _setThemeCallback?.Invoke(isDark);
}

public sealed class SkillHubMainForm : Form
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(
        IntPtr hWnd,
        IntPtr lprcUpdate,
        IntPtr hrgnUpdate,
        uint flags);

    private readonly string _appUrl;
    private readonly bool _startHiddenInTray;
    private readonly SyncEngine _syncEngine;
    private readonly Func<Task> _stopHostCallback;
    private readonly Icon _appIcon;
    private readonly Icon _smallIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly WebView2 _webView;
    private Task? _webViewInitTask;
    private bool _allowExit;
    private bool _initialHideApplied;
    private bool _isDarkTheme = true;

    public SkillHubMainForm(
        string appUrl,
        bool startHiddenInTray,
        SyncEngine syncEngine,
        SkillHubWindowController windowController,
        Func<Task> stopHostCallback)
    {
        _appUrl = appUrl;
        _startHiddenInTray = startHiddenInTray;
        _syncEngine = syncEngine;
        _stopHostCallback = stopHostCallback;

        _appIcon = SkillHubIconRenderer.CreateAppIcon(64);
        _smallIcon = SkillHubIconRenderer.CreateHubIcon(16);

        Text = "SkillHub";
        Icon = _appIcon;
        ShowIcon = true;
        Width = 1280;
        Height = 840;
        MinimumSize = new Size(460, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 19, 29);

        if (_startHiddenInTray)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
        }

        // System Tray Context Menu
        var trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open SkillHub", null, (_, _) => ShowAndActivateWindow())
        {
            Font = new Font(Font, FontStyle.Bold)
        };
        var syncNowItem = new ToolStripMenuItem("Sync All Skills Now", null, async (_, _) =>
        {
            try
            {
                await _syncEngine.SynchronizeAllAsync();
            }
            catch
            {
            }
        });
        var quitItem = new ToolStripMenuItem("Quit SkillHub", null, async (_, _) => await ExitApplicationAsync());

        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(syncNowItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(quitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "SkillHub",
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowAndActivateWindow();
            }
        };
        _trayIcon.DoubleClick += (_, _) => ShowAndActivateWindow();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(255, 15, 19, 29)
        };
        Controls.Add(_webView);

        windowController.RegisterCallbacks(
            showWindow: () =>
            {
                if (IsHandleCreated)
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(ShowAndActivateWindow));
                    }
                    else
                    {
                        ShowAndActivateWindow();
                    }
                }
            },
            quitApp: () =>
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(async () => await ExitApplicationAsync()));
                }
            },
            setTheme: isDark =>
            {
                _isDarkTheme = isDark;
                if (IsHandleCreated)
                {
                    if (InvokeRequired)
                    {
                        BeginInvoke(new Action(() => ApplyWindowTheme(isDark)));
                    }
                    else
                    {
                        ApplyWindowTheme(isDark);
                    }
                }
            });

        Load += OnFormLoadAsync;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = SendMessage(Handle, WM_SETICON, (IntPtr)ICON_SMALL, _smallIcon.Handle);
                _ = SendMessage(Handle, WM_SETICON, (IntPtr)ICON_BIG, _appIcon.Handle);
            }
            catch
            {
            }
        }
        ApplyWindowTheme(_isDarkTheme);
    }

    public void ApplyWindowTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        var bg = isDark ? Color.FromArgb(255, 15, 19, 29) : Color.FromArgb(255, 228, 234, 242);
        BackColor = bg;
        if (_webView is not null)
        {
            _webView.DefaultBackgroundColor = bg;
        }

        if (!IsHandleCreated || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var hwnd = Handle;
            var darkFlag = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkFlag, sizeof(int));

            // COLORREF is 0x00BBGGRR:
            // Dark Mode:  Caption #0F131D (0x001D130F), Text #F3F4F6 (0x00F6F4F3), Border #1E2536 (0x0036251E)
            // Light Mode: Caption #E4EAF2 (0x00F2EAE4), Text #1E293B (0x003B291E), Border #D5DEE9 (0x00E9DED5)
            var captionColorRef = isDark ? 0x001D130F : 0x00F2EAE4;
            var textColorRef = isDark ? 0x00F6F4F3 : 0x003B291E;
            var borderColorRef = isDark ? 0x0036251E : 0x00E9DED5;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColorRef, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorRef, sizeof(int));

            // Force immediate non-client frame repaint so the title bar color updates instantaneously
            _ = SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            _ = RedrawWindow(
                hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                RDW_FRAME | RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
        }
        catch
        {
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHiddenInTray && !_initialHideApplied)
        {
            _initialHideApplied = true;
            base.SetVisibleCore(false);
            if (!IsHandleCreated)
            {
                CreateHandle();
            }
            return;
        }

        base.SetVisibleCore(value);
    }

    private async void OnFormLoadAsync(object? sender, EventArgs e)
    {
        ApplyWindowTheme(_isDarkTheme);
        await InitializeWebViewAsync();
    }

    private Task InitializeWebViewAsync()
    {
        return _webViewInitTask ??= InitializeWebViewCoreAsync();
    }

    private async Task InitializeWebViewCoreAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SkillHub",
                "WebView2Data");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            // Clear WebView2 HTTP Disk Cache so updated wwwroot HTML/CSS/JS files are never served stale
            try
            {
                await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
            }
            catch
            {
            }

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

            _webView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                try
                {
                    var raw = args.TryGetWebMessageAsString();
                    if (string.IsNullOrWhiteSpace(raw)) return;
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
                        string.Equals(typeProp.GetString(), "theme", StringComparison.OrdinalIgnoreCase) &&
                        doc.RootElement.TryGetProperty("theme", out var themeProp))
                    {
                        var isDark = !string.Equals(themeProp.GetString(), "light", StringComparison.OrdinalIgnoreCase);
                        ApplyWindowTheme(isDark);
                    }
                }
                catch
                {
                }
            };

            _webView.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    var themeResult = await _webView.CoreWebView2.ExecuteScriptAsync(
                        "document.documentElement.getAttribute('data-theme') || localStorage.getItem('skillhub_theme') || 'dark'");
                    var cleanTheme = themeResult?.Trim('"') ?? "dark";
                    var isDark = !string.Equals(cleanTheme, "light", StringComparison.OrdinalIgnoreCase);
                    ApplyWindowTheme(isDark);
                }
                catch
                {
                }
            };

            var cacheBustedUrl = $"{_appUrl.TrimEnd('/')}/?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            _webView.CoreWebView2.Navigate(cacheBustedUrl);
        }
        catch (Exception ex)
        {
            _webViewInitTask = null;
            // Never launch an external web browser; only log or display a native message if visible
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                MessageBox.Show($"Could not initialize SkillHub WebView2: {ex.Message}", "SkillHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    public void ShowAndActivateWindow()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        ApplyWindowTheme(_isDarkTheme);

        if (_webView.CoreWebView2 is null)
        {
            _ = InitializeWebViewAsync();
        }

        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            // Minimize to System Tray instead of quitting so MCP Server and File Watcher keep running
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            return;
        }

        base.OnFormClosing(e);
    }

    private async Task ExitApplicationAsync()
    {
        if (_allowExit) return;
        _allowExit = true;
        _trayIcon.Visible = false;

        try
        {
            await _stopHostCallback().ConfigureAwait(true);
        }
        catch
        {
        }

        _trayIcon.Dispose();
        _smallIcon.Dispose();
        _appIcon.Dispose();
        Application.Exit();
    }
}
