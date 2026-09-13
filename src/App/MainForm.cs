using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ZweDesk.Core;

namespace ZweDesk.App;

/// <summary>
/// Hosts the WebView2 control as a plain application surface: no browser chrome, error pages, script dialogs, zoom,
/// navigation gestures or browser shortcuts; only cut/copy/paste in editable fields. Extracts the embedded UI and wires the JSON bridge.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Ground = Color.FromArgb(15, 18, 26);
    private static readonly HashSet<string> EditableMenu = new(StringComparer.OrdinalIgnoreCase) { "cut", "copy", "paste", "selectAll", "undo", "redo" };
    private static readonly HashSet<string> SelectionMenu = new(StringComparer.OrdinalIgnoreCase) { "copy" };

    private readonly AppService _svc;
    private readonly string _wvVersion;
    private readonly bool _debug;
    private readonly WebView2 _webView;
    private readonly Label _splash;
    private Bridge? _bridge;
    private bool _uiReady;

    public MainForm(AppService svc, string wvVersion, bool debug)
    {
        _svc = svc;
        _wvVersion = wvVersion;
        _debug = debug;

        Text = "ZweDesk" + (svc.Mode == "mock" ? "  —  DEMO MODE (mock data)" : "");
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1480, 920);
        MinimumSize = new Size(1000, 640);
        BackColor = Ground;
        Padding = new Padding(0, TopGrip, 0, 0);   // thin strip above the web view: the only part of the client area that is ours, used as the top resize handle
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }

        _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Ground, AllowExternalDrop = false };
        Controls.Add(_webView);

        _splash = new Label
        {
            Text = "ZweDesk\r\n\r\nloading…",
            ForeColor = Color.FromArgb(139, 147, 167),
            BackColor = Ground,
            Font = new Font("Segoe UI", 12f),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };
        Controls.Add(_splash);
        _splash.BringToFront();

        Load += async (_, _) => { RestoreWindowPlacement(); await InitAsync(); };
        FormClosing += (_, _) => SaveWindowPlacement();
        FormClosed += (_, _) => _svc.Logout();
    }

    // ------------------------------------------------------------------ window chrome
    // The native title bar is removed (no WS_CAPTION); the interface draws its own bar (#chrome in index.html) and asks the
    // bridge for drag / minimize / maximize / close. The resizable frame, Aero snap, the shadow and Alt+F4 stay native.

    private const int WS_CAPTION = 0x00C00000;
    private const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34;
    private const int TopGrip = 5;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style &= ~WS_CAPTION;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme(dark: true);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
            {
                // DefWindowProc reserves the frame on every side; the top is given back so the client area starts at the
                // window's edge (otherwise Windows paints a caption-coloured strip there). Maximized windows hang over the
                // screen edge by the frame width, so there the reservation is kept.
                var top = Marshal.ReadInt32(m.LParam, 4);    // NCCALCSIZE_PARAMS.rgrc[0].top
                base.WndProc(ref m);
                if (!IsZoomed(Handle)) Marshal.WriteInt32(m.LParam, 4, top);
                return;
            }
            case WM_NCHITTEST:
            {
                base.WndProc(ref m);
                if ((long)m.Result == HTCLIENT && !IsZoomed(Handle))
                {
                    var lp = (long)m.LParam;
                    var pt = PointToClient(new Point(unchecked((short)(lp & 0xFFFF)), unchecked((short)((lp >> 16) & 0xFFFF))));
                    if (pt.Y < TopGrip) m.Result = (IntPtr)(pt.X < 16 ? HTTOPLEFT : pt.X > ClientSize.Width - 16 ? HTTOPRIGHT : HTTOP);
                }
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>Starts the native move loop; called by the bridge while the left button is down on the interface's title bar.</summary>
    public void WindowDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    public void WindowMinimize() => WindowState = FormWindowState.Minimized;

    public void WindowToggleMaximize() => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    /// <summary>Frame colours follow the interface theme: dark title-bar mode and a 1 px border in the interface's border colour.</summary>
    public void ApplyTheme(bool dark)
    {
        var ground = dark ? Ground : Color.FromArgb(243, 245, 249);
        BackColor = ground;
        try { _webView.DefaultBackgroundColor = ground; } catch { }
        try
        {
            var mode = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref mode, sizeof(int));
            var border = dark ? 0x0042302A : 0x00ECE4DF;      // COLORREF (0x00BBGGRR) of #2a3042 / #dfe4ec
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));   // Windows 11; older versions ignore it
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ------------------------------------------------------------------ window placement (remembered between runs)

    private sealed record Placement(int X, int Y, int W, int H, bool Maximized);

    private void RestoreWindowPlacement()
    {
        try
        {
            var json = _svc.Db.GetSetting("window_placement");
            if (string.IsNullOrEmpty(json)) return;
            var p = JsonSerializer.Deserialize<Placement>(json);
            if (p is null || p.W < MinimumSize.Width || p.H < MinimumSize.Height) return;
            var rect = new Rectangle(p.X, p.Y, p.W, p.H);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = rect;
            if (p.Maximized) WindowState = FormWindowState.Maximized;
        }
        catch (Exception ex) { Log.Warn("window placement not restored: " + ex.Message); }
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var p = new Placement(r.X, r.Y, r.Width, r.Height, WindowState == FormWindowState.Maximized);
            _svc.Db.SetSetting("window_placement", JsonSerializer.Serialize(p));
        }
        catch (Exception ex) { Log.Warn("window placement not saved: " + ex.Message); }
    }

    // ------------------------------------------------------------------ WebView2

    private async Task InitAsync()
    {
        try
        {
            ExtractUi();
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection,msEdgeTranslate,msWebOOUI --disable-pinch",
                AllowSingleSignOnUsingOSPrimaryAccount = false,
            };
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir, options);
            await _webView.EnsureCoreWebView2Async(env);
            var core = _webView.CoreWebView2;

            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = true;      // filtered in ContextMenuRequested to cut/copy/paste only
            s.AreDefaultScriptDialogsEnabled = false;   // the UI has its own dialogs; alert() would look like a browser
            s.AreDevToolsEnabled = _debug;
            s.AreBrowserAcceleratorKeysEnabled = _debug; // no F5 / Ctrl+F / Ctrl+P / F12 / zoom shortcuts
            s.AreHostObjectsAllowed = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;         // never show "This page can't be displayed"
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            try { s.IsReputationCheckingRequired = false; } catch { }

            core.ContextMenuRequested += OnContextMenu;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            core.NavigationStarting += (_, e) =>
            {
                // the UI lives only on the virtual host; back/forward, file drops and any other navigation are refused
                if (!e.Uri.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                }
            };
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
            core.WindowCloseRequested += (_, _) => Close();
            core.DocumentTitleChanged += (_, _) => { };  // title stays the application's

            await HookConsoleAsync(core);

            core.SetVirtualHostNameToFolderMapping("app.local", AppPaths.UiDir, CoreWebView2HostResourceAccessKind.Allow);
            _bridge = new Bridge(this, _webView, _svc, _wvVersion);
            _bridge.Attach();
            core.Navigate("https://app.local/index.html");
            Log.Info("ui: navigated to https://app.local/index.html from " + AppPaths.UiDir);
            if (_debug) StartDebugEval();
        }
        catch (Exception ex)
        {
            Log.Error("ui: initialisation failed", ex);
            MessageBox.Show(this, "The user interface could not be started:\n\n" + ex.Message + "\n\nSee " + Log.CurrentFile, "ZweDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnContextMenu(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_debug) return;
        var allowed = e.ContextMenuTarget.IsEditable ? EditableMenu : e.ContextMenuTarget.HasSelection ? SelectionMenu : null;
        if (allowed is null) { e.Handled = true; return; }
        var items = e.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Kind != CoreWebView2ContextMenuItemKind.Command || !allowed.Contains(items[i].Name)) items.RemoveAt(i);
        if (items.Count == 0) e.Handled = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            if (!_uiReady)
            {
                _uiReady = true;
                _splash.Visible = false;
                _webView.Focus();
                Log.Info("ui: ready");
            }
            return;
        }
        Log.Error($"ui: navigation failed: {e.WebErrorStatus} (http {e.HttpStatusCode})");
        _splash.Text = $"ZweDesk\r\n\r\nThe interface failed to load ({e.WebErrorStatus}).\r\nSee {Log.CurrentFile}";
        _splash.Visible = true;
        _splash.BringToFront();
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error($"ui: WebView2 process failure {e.ProcessFailedKind} ({e.Reason}) exit={e.ExitCode} {e.ProcessDescription}");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                try { _webView.CoreWebView2?.Reload(); } catch { }
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                MessageBox.Show(this, "The interface process stopped unexpectedly. ZweDesk will restart.", "ZweDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', Environment.GetCommandLineArgs().Skip(1))) { UseShellExecute = true }); } catch { }
                Close();
                break;
        }
    }

    /// <summary>Copies the embedded ui/* resources to %LocalAppData%\ZweDesk\ui\&lt;version&gt; (overwritten on every start).</summary>
    private static void ExtractUi()
    {
        var asm = Assembly.GetExecutingAssembly();
        Directory.CreateDirectory(AppPaths.UiDir);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ui/", StringComparison.Ordinal)))
        {
            var rel = name["ui/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(AppPaths.UiDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
    }

    /// <summary>Mirrors JavaScript exceptions and console errors into the application log so UI faults are diagnosable from the log alone.</summary>
    private static async Task HookConsoleAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
                Log.Error("js exception: " + Truncate(e.ParameterObjectAsJson, 4000));
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
            {
                var json = e.ParameterObjectAsJson;
                if (json.Contains("\"type\":\"error\"") || json.Contains("\"type\":\"warning\""))
                    Log.Warn("js console: " + Truncate(json, 4000));
            };
        }
        catch (Exception ex)
        {
            Log.Warn("ui: console hook unavailable: " + ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// --debug only: every 400 ms looks for %LocalAppData%\ZweDesk\debug-eval.js, runs it in the page and writes the
    /// JSON result to debug-eval.out. Lets automated tests drive the real UI without any network exposure.
    /// </summary>
    private void StartDebugEval()
    {
        var js = Path.Combine(AppPaths.DataDir, "debug-eval.js");
        var outPath = Path.Combine(AppPaths.DataDir, "debug-eval.out");
        var timer = new System.Windows.Forms.Timer { Interval = 400 };
        var running = false;
        timer.Tick += async (_, _) =>
        {
            if (running || !File.Exists(js) || _webView.CoreWebView2 is null) return;
            running = true;
            try
            {
                string code;
                try { code = File.ReadAllText(js); File.Delete(js); }
                catch { running = false; return; }
                string result;
                try { result = await _webView.CoreWebView2.ExecuteScriptAsync(code); }
                catch (Exception ex) { result = "\"ERROR: " + ex.Message.Replace("\"", "'") + "\""; }
                File.WriteAllText(outPath, result);
            }
            finally { running = false; }
        };
        timer.Start();
        Log.Info("debug: eval hook active on " + js);
    }
}
