using System.Diagnostics;
using System.Reflection;
using ZweDesk.Core;

namespace ZweDesk.App;

/// <summary>
/// Shown when the WebView2 Runtime is missing (typical on Windows Server). Installs it from the installer embedded in
/// the executable (offline standalone preferred, online bootstrapper as fallback), or from a prereq\ folder next to the
/// exe, then lets the application continue.
/// </summary>
public sealed class PrereqForm : Form
{
    private const string Standalone = "MicrosoftEdgeWebView2RuntimeInstallerX64.exe";
    private const string Bootstrapper = "MicrosoftEdgeWebview2Setup.exe";
    private const string DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private readonly Label _status;
    private readonly Button _install;

    public PrereqForm()
    {
        Text = "ZweDesk — first start";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 300);
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }

        var title = new Label
        {
            Text = "Microsoft Edge WebView2 Runtime is not installed",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = false,
            Bounds = new Rectangle(20, 18, 520, 32),
        };
        var body = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(20, 56, 520, 100),
            Text = "ZweDesk renders its interface with the WebView2 Runtime, which Windows Server does not ship by default. " +
                   "It is installed once, system-wide, from the installer carried inside this executable. " +
                   "A User Account Control prompt will appear.",
        };
        _status = new Label { AutoSize = false, Bounds = new Rectangle(20, 160, 520, 70), ForeColor = Color.DarkSlateGray };
        _install = new Button { Text = "Install WebView2 Runtime", Bounds = new Rectangle(20, 245, 220, 36) };
        var open = new Button { Text = "Open download page", Bounds = new Rectangle(250, 245, 170, 36) };
        var exit = new Button { Text = "Exit", Bounds = new Rectangle(440, 245, 100, 36), DialogResult = DialogResult.Cancel };

        _install.Click += async (_, _) => await InstallAsync();
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true }); } catch { } };
        Controls.AddRange(new Control[] { title, body, _status, _install, open, exit });
        CancelButton = exit;
        RefreshStatus();
    }

    // ------------------------------------------------------------------ installer sources

    public sealed record Source(string Name, string Description, bool Offline, Func<string?> Materialize);

    /// <summary>Installer sources in order of preference: embedded offline, embedded online, files next to the exe.</summary>
    public static List<Source> Sources()
    {
        var list = new List<Source>();
        foreach (var (name, offline) in new[] { (Standalone, true), (Bootstrapper, false) })
        {
            var size = EmbeddedSize(name);
            if (size > 0)
                list.Add(new Source(name, $"embedded {(offline ? "offline" : "online")} installer ({size / 1048576.0:0.#} MB)", offline, () => ExtractEmbedded(name)));
        }
        foreach (var (name, offline) in new[] { (Standalone, true), (Bootstrapper, false) })
        {
            foreach (var dir in new[] { AppPaths.PrereqDir, AppPaths.ExeDir })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) { list.Add(new Source(name, $"{(offline ? "offline" : "online")} installer next to the exe", offline, () => p)); break; }
            }
        }
        return list;
    }

    private static Assembly? PrereqAssembly()
    {
        try { return Assembly.Load(new AssemblyName("ZweDesk.Prereq")); }
        catch (Exception ex) { Log.Warn("prereq: carrier assembly not available: " + ex.Message); return null; }
    }

    public static long EmbeddedSize(string name)
    {
        try
        {
            using var s = PrereqAssembly()?.GetManifestResourceStream("prereq/" + name);
            return s?.Length ?? 0;
        }
        catch { return 0; }
    }

    private static string? ExtractEmbedded(string name)
    {
        try
        {
            using var s = PrereqAssembly()?.GetManifestResourceStream("prereq/" + name);
            if (s is null) return null;
            var dir = Path.Combine(AppPaths.DataDir, "prereq");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, name);
            if (!File.Exists(target) || new FileInfo(target).Length != s.Length)
            {
                using var f = File.Create(target);
                s.CopyTo(f);
            }
            return target;
        }
        catch (Exception ex)
        {
            Log.Error("prereq: extracting " + name + " failed", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ UI

    private void RefreshStatus()
    {
        var src = Sources().FirstOrDefault();
        _install.Enabled = src is not null;
        _status.Text = src is null
            ? "No installer is available in this build. Download the Evergreen Standalone Installer (x64) from the page below and place it next to ZweDesk.exe, then start again."
            : $"Ready: {src.Description}{(src.Offline ? "" : " — needs internet access on this machine")}.";
    }

    private async Task InstallAsync()
    {
        var src = Sources().FirstOrDefault();
        if (src is null) { RefreshStatus(); return; }
        _install.Enabled = false;
        _status.Text = "Preparing installer…";
        Log.Info("prereq: using " + src.Description);
        try
        {
            var path = await Task.Run(src.Materialize);
            if (path is null) throw new InvalidOperationException("installer could not be prepared");
            _status.Text = "Installing… (a User Account Control prompt may appear)";
            var psi = new ProcessStartInfo(path, "/silent /install") { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("installer did not start");
            await p.WaitForExitAsync();
            Log.Info("prereq: installer exit code " + p.ExitCode);
            string v;
            try { v = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { v = ""; }
            if (!string.IsNullOrEmpty(v))
            {
                _status.Text = "WebView2 Runtime " + v + " installed. Starting…";
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            _status.Text = $"The installer finished (exit code {p.ExitCode}) but the runtime is still not detected. A reboot may be required.";
        }
        catch (Exception ex)
        {
            Log.Error("prereq: install failed", ex);
            _status.Text = "Installation failed: " + ex.Message;
        }
        _install.Enabled = true;
    }
}
