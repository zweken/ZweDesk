using Microsoft.Web.WebView2.Core;
using ZweDesk.Core;
using ZweDesk.Providers;
using ZweDesk.Remote;

namespace ZweDesk.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var mock = args.Any(a => a.Equals("--mock", StringComparison.OrdinalIgnoreCase));
        var debug = args.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase));
        var dbArg = Array.IndexOf(args, "--db");
        if (dbArg >= 0 && dbArg + 1 < args.Length) AppPaths.OverrideDb(args[dbArg + 1]);
        else if (mock) AppPaths.OverrideDb(Path.Combine(AppPaths.DataDir, "zwedesk-mock.db"));

        AppPaths.EnsureDirectories();
        Log.Init(AppPaths.LogsDir);
        Log.Info($"start v{AppPaths.AppVersion} mode={(mock ? "mock" : "real")} debug={debug} exe={Environment.ProcessPath} db={AppPaths.DbPath}");

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest();

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        Db db;
        try
        {
            db = new Db(AppPaths.DbPath);
            db.Migrate();
            // first start: the demo database gets its fictional environment; a real database gets the domain this computer is joined to
            if (db.GetSetting("first_run_done") is null)
            {
                if (db.ListDomains().Count == 0)
                {
                    if (mock) MockProvider.SeedDemoEnvironment(db);
                    else AppService.SeedFromMachineDomain(db);
                }
                db.SetSetting("first_run_done", DateTime.UtcNow.ToString("o"));
            }
        }
        catch (Exception ex)
        {
            Fatal("The database could not be opened: " + AppPaths.DbPath, ex);
            return 2;
        }

        if (!WebView2Available(out var wvVersion))
        {
            Log.Warn("WebView2 Runtime not found");
            using var prereq = new PrereqForm();
            if (prereq.ShowDialog() != DialogResult.OK || !WebView2Available(out wvVersion))
                return 3;
        }
        Log.Info("WebView2 Runtime " + wvVersion);

        IServerProvider provider = mock ? new MockProvider() : new RealProvider(new WinRmRunner());
        var svc = new AppService(db, provider);
        Application.Run(new MainForm(svc, wvVersion, debug));
        Log.Info("exit");
        return 0;
    }

    /// <summary>
    /// --selftest: runs every remote script through the real WinRM wrapper against localhost with a dummy credential.
    /// The logon is expected to fail; what must NOT happen is a PowerShell parse error, which would mean a broken script.
    /// Result: %LocalAppData%\ZweDesk\selftest.txt and exit code 0 (all scripts parse) / 1.
    /// </summary>
    private static int SelfTest()
    {
        var runner = new WinRmRunner();
        var cred = new Credential { DomainName = "selftest.local", User = "selftest", Password = "not-a-real-password", Netbios = "SELFTEST" };
        var scripts = new (string Name, string Script, object?[] Args)[]
        {
            ("Ping", PsScripts.Ping, Array.Empty<object?>()),
            ("ScanFolders", PsScripts.ScanFolders, new object?[] { @"D:\Users" }),
            ("SetQuota", PsScripts.SetQuota, new object?[] { @"D:\Users\u0001", 5368709120L }),
            ("ComputeSize", PsScripts.ComputeSize, new object?[] { @"D:\Users\u0001" }),
            ("ListDfsLinks", PsScripts.ListDfsLinks, new object?[] { @"\\selftest.local\Users" }),
            ("CreateDfsLink", PsScripts.CreateDfsLink, new object?[] { @"\\selftest.local\Users\u0001", @"\\fs1.selftest.local\Users$\u0001" }),
            ("FsrmStatus", PsScripts.FsrmStatus, new object?[] { false }),
            ("ListShares", PsScripts.ListShares, Array.Empty<object?>()),
            ("CreateFolder", PsScripts.CreateFolder, new object?[] { @"D:\Users\u0001", "S-1-5-21-1-2-3-1105", "S-1-5-21-1-2-3-512" }),
            ("ProbeServer", PsScripts.ProbeServer, new object?[] { @"D:\Users" }),
            ("ProbeDfsHost", PsScripts.ProbeDfsHost, new object?[] { @"\\selftest.local\Users", false }),
            ("ListOus", PsScripts.ListOus, Array.Empty<object?>()),
            ("ListGroups", PsScripts.ListGroups, Array.Empty<object?>()),
            ("ListDcs", PsScripts.ListDcs, Array.Empty<object?>()),
            ("CreateAdUser", PsScripts.CreateAdUser, new object?[] { "u0001", "u0001@selftest.local", "Test", "User", "Test User", "OU=Users,DC=selftest,DC=local", true }),
            ("AddToGroups", PsScripts.AddToGroups, new object?[] { "u0001", "[\"CN=VDI-Users,OU=Groups,DC=selftest,DC=local\"]" }),
        };
        var lines = new List<string> { $"ZweDesk self-test {DateTime.Now:s} on {Environment.MachineName}" };
        var failed = 0;
        foreach (var (name, script, a) in scripts)
        {
            var r = runner.RunAsync("localhost", script, a, cred, CancellationToken.None, TimeSpan.FromSeconds(90), name == "CreateAdUser" ? "P@ssw0rd-selftest" : null).GetAwaiter().GetResult();
            var text = (r.Error ?? "") + " " + r.Stderr;
            var parseError = text.Contains("ParserError", StringComparison.OrdinalIgnoreCase) || text.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("Missing closing", StringComparison.OrdinalIgnoreCase) || text.Contains("is not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase) && text.Contains("Invoke-Command");
            var verdict = r.Ok ? "OK (ran)" : parseError ? "PARSE ERROR" : "syntax OK (remote logon refused as expected)";
            if (parseError) failed++;
            lines.Add($"{name,-14} {verdict}  [{(r.Error ?? "").Replace(Environment.NewLine, " ")}]");
        }
        foreach (var src in PrereqForm.Sources())
            lines.Add($"WebView2 installer source: {src.Description}");
        if (PrereqForm.Sources().Count == 0) lines.Add("WebView2 installer source: none (build without embedded runtime)");
        lines.Add(failed == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failed} script(s) with parse errors)");
        var outFile = Path.Combine(AppPaths.DataDir, "selftest.txt");
        File.WriteAllLines(outFile, lines);
        foreach (var l in lines) Log.Info("selftest: " + l);
        return failed == 0 ? 0 : 1;
    }

    private static bool WebView2Available(out string version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrEmpty(version);
        }
        catch (Exception)
        {
            version = "";
            return false;
        }
    }

    private static void Fatal(string what, Exception? ex)
    {
        Log.Error(what, ex);
        try
        {
            MessageBox.Show($"{what}\n\n{ex?.Message}\n\nDetails were written to:\n{Log.CurrentFile}", "ZweDesk",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
