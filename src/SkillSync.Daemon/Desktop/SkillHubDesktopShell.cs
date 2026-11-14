using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SkillSync.Daemon.Core.Engine;
using SkillSync.Daemon.Api;

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
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

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
    private readonly string _desktopSessionToken;
    private readonly bool _startHiddenInTray;
    private readonly SyncEngine _syncEngine;
    private readonly DesktopPreferencesStore _preferences;
    private readonly string _userDataFolder;
    private readonly ContextMenuStrip _trayMenu;
    private readonly Panel _recoveryPanel;
    private readonly Label _recoveryMessage;
    private readonly Icon _appIcon;
    private readonly Icon _smallIcon;
    private readonly NotifyIcon _trayIcon;
    private WebView2 _webView;
    private Task? _webViewInitTask;
    private bool _allowExit;
    private bool _trayTipShown;
    private bool _initialHideApplied;
    private bool _isDarkTheme = true;

    public SkillHubMainForm(
        string appUrl,
        string desktopSessionToken,
        bool startHiddenInTray,
        SyncEngine syncEngine,
        SkillHubWindowController windowController,
        DesktopPreferencesStore preferences,
        string userDataFolder)
    {
        _appUrl = appUrl.TrimEnd('/');
        _desktopSessionToken = desktopSessionToken;
        _startHiddenInTray = startHiddenInTray;
        _syncEngine = syncEngine;
        _preferences = preferences;
        _userDataFolder = userDataFolder;

        _appIcon = SkillHubIconRenderer.CreateAppIcon(64);
        _smallIcon = SkillHubIconRenderer.CreateHubIcon(16);

        Text = "SkillHub";
        Icon = _appIcon;
        ShowIcon = true;
        Width = 1280;
        Height = 840;
        MinimumSize = new Size(460, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(21, 23, 30);
        _isDarkTheme = preferences.Read().Theme switch { "light" => false, "dark" => true, _ => !SystemInformation.HighContrast };
        RestorePlacement(preferences.Read().Bounds);

        if (_startHiddenInTray)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Opacity = 0;
        }

        // System Tray Context Menu
        _trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open SkillHub", null, (_, _) => ShowAndActivateWindow())
        {
            Font = SystemFonts.MenuFont
        };
        var syncNowItem = new ToolStripMenuItem("Reconcile skills");
        syncNowItem.Click += async (_, _) =>
        {
            syncNowItem.Enabled = false;
            try
            {
                var report = await _syncEngine.SynchronizeAllAsync();
                if (report.FailedDestinations + report.ConflictedDestinations > 0)
                {
                    _trayIcon?.ShowBalloonTip(5000, "SkillHub needs attention", "Open Diagnostics to review sync results.", ToolTipIcon.Warning);
                }
            }
            catch (Exception exception)
            {
                _trayIcon?.ShowBalloonTip(5000, "SkillHub could not reconcile", exception.Message, ToolTipIcon.Error);
            }
            finally
            {
                if (!IsDisposed) syncNowItem.Enabled = true;
            }
        };
        var quitItem = new ToolStripMenuItem("Quit SkillHub", null, (_, _) => Quit());
        _trayMenu.Items.AddRange([openItem, syncNowItem, new ToolStripSeparator(), quitItem]);

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "SkillHub",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowAndActivateWindow();
            }
        };
        _trayIcon.DoubleClick += (_, _) => ShowAndActivateWindow();

        _webView = NewWebView();
        _recoveryMessage = new Label { AutoSize = true, MaximumSize = new Size(410, 0) };
        var retry = new Button { Text = "Retry", AutoSize = true };
        retry.Click += async (_, _) => await RetryWebViewAsync();
        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(24),
            Dock = DockStyle.Top
        };
        layout.Controls.Add(_recoveryMessage);
        layout.Controls.Add(retry);
        _recoveryPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _recoveryPanel.Controls.Add(layout);
        Controls.Add(_recoveryPanel);

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
                    BeginInvoke(new Action(Quit));
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
        var bg = SystemInformation.HighContrast ? SystemColors.Window : isDark ? Color.FromArgb(255, 15, 19, 29) : Color.FromArgb(255, 228, 234, 242);
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
            if (SystemInformation.HighContrast) darkFlag = 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkFlag, sizeof(int));

            // COLORREF is 0x00BBGGRR:
            // Dark Mode:  Caption #0F131D (0x001D130F), Text #F3F4F6 (0x00F6F4F3), Border #1E2536 (0x0036251E)
            // Light Mode: Caption #E4EAF2 (0x00F2EAE4), Text #1E293B (0x003B291E), Border #D5DEE9 (0x00E9DED5)
            var captionColorRef = isDark ? 0x001D130F : 0x00F2EAE4;
            var textColorRef = isDark ? 0x00F6F4F3 : 0x003B291E;
            var borderColorRef = isDark ? 0x0036251E : 0x00E9DED5;
            if (SystemInformation.HighContrast) captionColorRef = textColorRef = borderColorRef = -1;
            else if (SupportsMica)
            {
                // Windows 11 22H2+: let the Mica material show through the title bar instead of a flat caption color.
                var backdrop = DWMSBT_MAINWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                {
                    captionColorRef = DWMWA_COLOR_DEFAULT;
                    textColorRef = DWMWA_COLOR_DEFAULT;
                }
            }
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

    private static bool SupportsMica => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    /// <summary>
    /// Opens an https documentation link from the page in the user's default browser. Loopback and SkillHub's own
    /// origin are refused, so the desktop UI itself never opens in an external browser (see AGENTS.md invariant 2).
    /// </summary>
    private static void OpenExternalLink(string? value, string appOrigin)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.IsLoopback || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.GetLeftPart(UriPartial.Authority).Equals(appOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser is registered. The page already told the user which site it tried to open.
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

    private WebView2 NewWebView()
    {
        var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(view);
        return view;
    }

    private async Task InitializeWebViewCoreAsync()
    {
        try
        {
            SkillPaths.AssertNoReparsePoints(_userDataFolder);
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;

            var web = _webView.CoreWebView2;
            var appOrigin = new Uri(_appUrl).GetLeftPart(UriPartial.Authority);
            bool IsAppUri(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                uri.GetLeftPart(UriPartial.Authority) == appOrigin;

            web.Settings.IsStatusBarEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsZoomControlEnabled = true;
            web.Settings.IsWebMessageEnabled = true;
            web.NavigationStarting += (_, args) => args.Cancel = !IsAppUri(args.Uri);
            web.FrameNavigationStarting += (_, args) => args.Cancel = !IsAppUri(args.Uri);
            web.NewWindowRequested += (_, args) => args.Handled = true;
            web.PermissionRequested += (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
            };
            web.DownloadStarting += (_, args) =>
            {
                var uri = args.DownloadOperation.Uri;
                args.Cancel = !IsAppUri(web.Source) || !uri.StartsWith($"blob:{appOrigin}/", StringComparison.Ordinal);
            };
            web.ProcessFailed += (_, args) => BeginInvoke(new Action(() =>
                ShowWebViewError($"The embedded browser stopped ({args.ProcessFailedKind}). Retry to reopen SkillHub.")));
            web.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    ShowWebViewError($"SkillHub could not load its local page ({args.WebErrorStatus}).");
                }
            };
            web.AddWebResourceRequestedFilter($"{_appUrl}/api/v1/*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.Document);
            web.WebResourceRequested += (_, args) =>
            {
                if (IsAppUri(args.Request.Uri) && IsAppUri(web.Source))
                {
                    args.Request.Headers.SetHeader(LocalHttpAccess.DesktopHeader, _desktopSessionToken);
                }
            };
            web.WebMessageReceived += (_, args) =>
            {
                if (!IsAppUri(args.Source)) return;
                try
                {
                    var raw = args.TryGetWebMessageAsString();
                    if (raw.Length > 4096) return;
                    using var doc = JsonDocument.Parse(raw);
                    var kind = doc.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
                    if (kind == "theme" && doc.RootElement.TryGetProperty("theme", out var value) && value.GetString() is "light" or "dark")
                    {
                        ApplyWindowTheme(value.GetString() == "dark");
                    }
                    else if (kind == "open-external" && doc.RootElement.TryGetProperty("url", out var link))
                    {
                        OpenExternalLink(link.GetString(), appOrigin);
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
                {
                    // Ignore malformed messages from the embedded document.
                }
            };

            _recoveryPanel.Visible = false;
            _webView.Visible = true;
            web.Navigate(_appUrl + "/");
        }
        catch (Exception exception) when (!IsDisposed)
        {
            ShowWebViewError(exception is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is required. Install it, then retry."
                : $"SkillHub could not start its embedded browser. {exception.Message}");
        }
    }

    private void ShowWebViewError(string message)
    {
        if (IsDisposed) return;
        _webView.Visible = false;
        _recoveryMessage.Text = message;
        _recoveryPanel.Visible = true;
        _recoveryPanel.BringToFront();
        if (!Visible)
        {
            _trayIcon?.ShowBalloonTip(4500, "SkillHub window unavailable", message, ToolTipIcon.Warning);
        }
    }

    private async Task RetryWebViewAsync()
    {
        if (_webViewInitTask is { IsCompleted: false }) return;
        Controls.Remove(_webView);
        _webView.Dispose();
        _webView = NewWebView();
        _webViewInitTask = null;
        await InitializeWebViewAsync();
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

    private void RestorePlacement(DesktopBounds? placement)
    {
        if (placement is null || placement.Width < MinimumSize.Width || placement.Height < MinimumSize.Height) return;
        var bounds = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
        var screen = Screen.AllScreens.OrderByDescending(display =>
        {
            var overlap = Rectangle.Intersect(display.WorkingArea, bounds);
            return (long)overlap.Width * overlap.Height;
        }).FirstOrDefault(display => Rectangle.Intersect(display.WorkingArea, bounds).Width >= 80);
        if (screen is null) return;
        var area = screen.WorkingArea;
        var width = Math.Min(bounds.Width, area.Width);
        var height = Math.Min(bounds.Height, area.Height);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(Math.Clamp(bounds.X, area.X, area.Right - width),
            Math.Clamp(bounds.Y, area.Y, area.Bottom - height), width, height);
        if (placement.Maximized && !_startHiddenInTray) WindowState = FormWindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (!Visible && _startHiddenInTray) return;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width < MinimumSize.Width || bounds.Height < MinimumSize.Height) return;
        try
        {
            _preferences.SetBounds(new DesktopBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _trayIcon?.ShowBalloonTip(4500, "Window placement could not be saved", exception.Message, ToolTipIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing && _preferences.Read().CloseToTray)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _trayIcon.ShowBalloonTip(4500, "SkillHub is still running", "Use the tray icon to reopen SkillHub or quit.", ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    internal void RequestHostShutdown()
    {
        if (!IsDisposed && IsHandleCreated)
        {
            try { BeginInvoke(new Action(Quit)); }
            catch (InvalidOperationException) { }
        }
    }

    private void Quit()
    {
        _allowExit = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _smallIcon?.Dispose();
            _appIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
