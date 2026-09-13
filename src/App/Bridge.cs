using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ZweDesk.Core;

namespace ZweDesk.App;

/// <summary>
/// JSON-RPC over WebView2 messages. JS sends {id, op, params}; C# answers {id, ok, result | error, details}.
/// Server-pushed events are {event, data}. Every handler runs off the UI thread; replies are marshalled back.
/// </summary>
public sealed class Bridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly Form _form;
    private readonly WebView2 _webView;
    private readonly AppService _svc;
    private readonly string _wvVersion;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _ops;

    public Bridge(Form form, WebView2 webView, AppService svc, string wvVersion)
    {
        _form = form;
        _webView = webView;
        _svc = svc;
        _wvVersion = wvVersion;
        _ops = BuildOps();
    }

    public void Attach()
    {
        _webView.CoreWebView2.WebMessageReceived += OnMessage;
        _svc.Push = (evt, data) => Post(new { @event = evt, data });
    }

    // ------------------------------------------------------------------ plumbing

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string id = "", op = "";
        JsonElement p = default;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
            op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";
            p = root.TryGetProperty("params", out var pEl) ? pEl.Clone() : default;
        }
        catch (Exception ex)
        {
            Log.Warn("bridge: bad message: " + ex.Message);
            return;
        }
        if (op.StartsWith("window.", StringComparison.Ordinal))
        {
            // window chrome: must run on the UI thread, and the drag must start while the mouse button is still down
            try { WindowOp(op, p); Post(new { id, ok = true, result = new { ok = true } }); }
            catch (Exception ex) { Post(new { id, ok = false, error = ex.Message }); }
            return;
        }
        _ = Task.Run(() => DispatchAsync(id, op, p));
    }

    private void WindowOp(string op, JsonElement p)
    {
        if (_form is not MainForm main) return;
        switch (op)
        {
            case "window.drag": main.WindowDrag(); break;
            case "window.minimize": main.WindowMinimize(); break;
            case "window.toggleMax": main.WindowToggleMaximize(); break;
            case "window.close": main.Close(); break;
            case "window.theme": main.ApplyTheme(Bool(p, "dark", true)); break;
            default: throw new AppException("Unknown operation: " + op);
        }
    }

    private async Task DispatchAsync(string id, string op, JsonElement p)
    {
        if (op == "rpc.cancel")
        {
            var target = Str(p, "targetId");
            if (target is not null && _inflight.TryGetValue(target, out var c)) c.Cancel();
            Post(new { id, ok = true, result = new { cancelled = target } });
            return;
        }
        if (!_ops.TryGetValue(op, out var handler))
        {
            Post(new { id, ok = false, error = "Unknown operation: " + op });
            return;
        }
        var cts = new CancellationTokenSource();
        _inflight[id] = cts;
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await handler(p, cts.Token);
            Post(new { id, ok = true, result });
            if (op is not ("dashboard.get" or "folders.list" or "settings.get" or "app.info" or "auth.session"))
                Log.Info($"op {op} ok in {sw.ElapsedMilliseconds} ms");
        }
        catch (AppException ex)
        {
            Log.Warn($"op {op} failed: {ex.Message}");
            Post(new { id, ok = false, error = ex.Message, details = ex.Details });
        }
        catch (OperationCanceledException)
        {
            Log.Info($"op {op} cancelled");
            Post(new { id, ok = false, error = "Cancelled", cancelled = true });
        }
        catch (Exception ex)
        {
            Log.Error($"op {op} crashed", ex);
            Post(new { id, ok = false, error = ex.Message, details = ex.ToString() });
        }
        finally
        {
            _inflight.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        try
        {
            if (_form.IsDisposed) return;
            if (_form.InvokeRequired) _form.BeginInvoke(() => { try { _webView.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Warn("bridge: post failed: " + ex.Message); } });
            else _webView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Log.Warn("bridge: post failed: " + ex.Message);
        }
    }

    private Task<T> OnUi<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>();
        _form.BeginInvoke(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    // ------------------------------------------------------------------ parameter helpers

    private static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    private static string Req(JsonElement p, string name) =>
        Str(p, name) ?? throw new AppException($"Missing parameter '{name}'");

    private static long Long(JsonElement p, string name)
    {
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        }
        throw new AppException($"Missing or invalid parameter '{name}'");
    }

    private static long? LongOpt(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static bool Bool(JsonElement p, string name, bool dflt = false) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True : dflt;

    private static List<long> Longs(JsonElement p, string name)
    {
        var list = new List<long>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray()) if (e.TryGetInt64(out var n)) list.Add(n);
        return list;
    }

    // ------------------------------------------------------------------ operations

    private Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> BuildOps()
    {
        var db = _svc.Db;
        return new()
        {
            ["app.info"] = (_, _) => Task.FromResult<object?>(new
            {
                version = AppPaths.AppVersion, mode = _svc.Mode, machine = Environment.MachineName, localFqdn = AppService.LocalFqdn(),
                machineDomain = MachineDomain.Query(), frameless = _form is MainForm,
                windowsUser = Environment.UserDomainName + "\\" + Environment.UserName, dataDir = AppPaths.DataDir, dbPath = AppPaths.DbPath,
                logFile = Log.CurrentFile, webview = _wvVersion, os = Environment.OSVersion.VersionString,
            }),

            // ---- auth
            ["auth.login"] = async (p, ct) => await _svc.LoginAsync(Long(p, "domainId"), Req(p, "user"), Str(p, "password") ?? "", Bool(p, "remember"), ct),
            ["auth.remembered"] = (p, _) => Task.FromResult<object?>(_svc.RememberedCredential(Long(p, "domainId"))),
            ["auth.logout"] = (_, _) => { _svc.Logout(); return Task.FromResult<object?>(new { ok = true }); },
            ["auth.session"] = (_, _) => Task.FromResult<object?>(_svc.Session is null ? new { loggedIn = false, user = (string?)null, domainId = 0L }
                : new { loggedIn = true, user = (string?)_svc.Session.Cred.LogonName, domainId = _svc.Session.DomainId }),

            // ---- domains & servers
            ["domains.list"] = (_, _) => Task.FromResult<object?>(db.ListDomains().Select(d => new
            {
                d.Id, d.Name, d.Netbios, d.DfsRoot, d.DfsHostFqdn, d.CreatedAt, d.DcFqdn,
                d.HorizonUrl, d.HorizonAuth, d.HorizonUser, d.HorizonDomain, d.HorizonIgnoreCert, d.HorizonEntitleMode, d.HorizonConfigured,
                hasHorizonPassword = _svc.HasHorizonPassword(d.Id), d.DefaultOuDn, d.DefaultPoolId,
                servers = db.ListServers(d.Id).Count, hasRemembered = db.GetSetting("cred_" + d.Id) is not null,
            }).ToList()),
            ["env.save"] = (p, _) =>
            {
                var id = _svc.SaveEnvironment(new AppService.EnvironmentInput(LongOpt(p, "id"), Req(p, "name"), Str(p, "netbios") ?? "", Str(p, "dfsRoot") ?? "",
                    Str(p, "dfsHostFqdn") ?? "", Str(p, "dcFqdn") ?? "", Str(p, "horizonUrl") ?? "", Str(p, "horizonAuth") ?? "none", Str(p, "horizonUser") ?? "",
                    Str(p, "horizonDomain") ?? "", Bool(p, "horizonIgnoreCert"), Str(p, "horizonEntitleMode") ?? "group", Str(p, "horizonPassword")));
                return Task.FromResult<object?>(new { id });
            },
            ["env.horizonTest"] = async (p, ct) => await _svc.HorizonTestAsync(Long(p, "domainId"), ct),
            ["env.pools"] = (p, _) => Task.FromResult<object?>(_svc.Pools(Long(p, "domainId"))),
            ["env.discoverDfs"] = async (p, ct) => await _svc.DiscoverNamespaceServersAsync(Long(p, "domainId"), ct),
            ["env.discoverRoots"] = async (p, ct) => await _svc.DiscoverNamespaceRootsAsync(Long(p, "domainId"), ct),
            ["dir.get"] = async (p, ct) => await _svc.DirectoryAsync(Long(p, "domainId"), Bool(p, "refresh"), ct),
            ["provision.plan"] = async (p, ct) => await _svc.ProvisionPlanAsync(Long(p, "domainId"), Req(p, "sam"), LongOpt(p, "serverId") ?? 0, ct),
            ["provision.run"] = async (p, ct) =>
            {
                var groups = new List<string>();
                if (p.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array)
                    foreach (var e in g.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) groups.Add(e.GetString()!);
                var spec = new AppService.ProvisionSpec(Str(p, "firstName") ?? "", Str(p, "lastName") ?? "", Req(p, "sam"), Str(p, "password"),
                    Bool(p, "mustChange", true), Str(p, "ouDn") ?? "", groups, Long(p, "serverId"), Long(p, "bytes"), Str(p, "poolId"), Str(p, "poolEntitle") ?? "none", Str(p, "dcFqdn"));
                return await _svc.ProvisionAsync(Long(p, "domainId"), spec, ct);
            },
            ["password.generate"] = (_, _) => Task.FromResult<object?>(new { password = AppService.GeneratePassword() }),
            ["domains.delete"] = (p, _) => { db.DeleteDomain(Long(p, "id")); return Task.FromResult<object?>(new { ok = true }); },

            ["servers.list"] = (p, _) => Task.FromResult<object?>(db.ListServers(Long(p, "domainId"))),
            ["servers.save"] = (p, _) =>
            {
                var name = Req(p, "name").Trim();
                var fqdn = (Str(p, "fqdn") ?? "").Trim();
                var root = (Str(p, "localRoot") ?? "").Trim();
                if (name.Length == 0) throw new AppException("Server name is required.");
                if (fqdn.Length == 0) fqdn = name;
                if (root.Length < 3 || root[1] != ':') throw new AppException("Local root must be a local path on the server, e.g. D:\\Users.");
                var share = Str(p, "shareName")?.Trim();
                var id = db.SaveServer(LongOpt(p, "id"), Long(p, "domainId"), name, fqdn, root.TrimEnd('\\'), string.IsNullOrEmpty(share) ? null : share, Bool(p, "enabled", true));
                return Task.FromResult<object?>(new { id });
            },
            ["servers.delete"] = (p, _) => { db.DeleteServer(Long(p, "id")); return Task.FromResult<object?>(new { ok = true }); },
            ["servers.test"] = async (p, ct) => await _svc.TestServerAsync(Long(p, "id"), ct),

            // ---- diagnostics / scan
            ["diag.run"] = async (p, ct) => await _svc.DiagnosticsAsync(Long(p, "domainId"), ct),
            ["diag.fix"] = async (p, ct) => await _svc.FixAsync(Long(p, "domainId"), Req(p, "kind"), LongOpt(p, "serverId"), ct),
            ["scan.run"] = async (p, ct) => await _svc.ScanAsync(Long(p, "domainId"), ct),

            // ---- data views
            ["dashboard.get"] = (p, _) => Task.FromResult<object?>(_svc.Dashboard(Long(p, "domainId"))),
            ["folders.list"] = (p, _) => Task.FromResult<object?>(_svc.Folders(Long(p, "domainId"))),
            ["folders.details"] = (p, _) => Task.FromResult<object?>(_svc.FolderDetails(Long(p, "folderId"))),
            ["links.list"] = (p, _) => Task.FromResult<object?>(_svc.Links(Long(p, "domainId"))),
            ["issues.list"] = (p, _) => Task.FromResult<object?>(_svc.Issues(Long(p, "domainId"))),
            ["audit.list"] = (p, _) =>
            {
                var limit = (int)Math.Clamp(LongOpt(p, "limit") ?? 100, 1, 1000);
                var offset = (int)Math.Max(0, LongOpt(p, "offset") ?? 0);
                var (rows, total) = db.ListAudit(limit, offset, Str(p, "filter"));
                return Task.FromResult<object?>(new { rows, total });
            },

            // ---- mutations
            ["folders.setQuota"] = async (p, ct) => await _svc.SetQuotaAsync(Long(p, "folderId"), Long(p, "bytes"), ct),
            ["folders.computeSize"] = async (p, ct) => await _svc.ComputeSizeAsync(Long(p, "folderId"), ct),
            ["folders.createLink"] = async (p, ct) => await _svc.CreateLinkForFolderAsync(Long(p, "folderId"), ct),
            ["users.lookup"] = async (p, ct) => await _svc.LookupUserAsync(Long(p, "domainId"), Req(p, "code"), ct),
            ["folders.create"] = async (p, ct) => await _svc.CreateUserFolderAsync(Long(p, "domainId"), Long(p, "serverId"), Req(p, "code"), Long(p, "bytes"), ct),
            ["sync.dryRun"] = (p, _) => Task.FromResult<object?>(_svc.SyncDryRun(Long(p, "domainId"))),
            ["sync.apply"] = async (p, ct) => await _svc.SyncApplyAsync(Long(p, "domainId"), Longs(p, "folderIds"), ct),

            // ---- settings & shell
            ["settings.get"] = (_, _) => Task.FromResult<object?>(db.AllSettings().Where(kv => !kv.Key.StartsWith("cred_")).ToDictionary(kv => kv.Key, kv => kv.Value)),
            ["settings.set"] = (p, _) =>
            {
                var key = Req(p, "key");
                if (key.StartsWith("cred_")) throw new AppException("Not allowed.");
                db.SetSetting(key, Str(p, "value"));
                return Task.FromResult<object?>(new { ok = true });
            },
            ["export.csv"] = async (p, _) =>
            {
                var domainId = Long(p, "domainId");
                var csv = _svc.ExportCsv(domainId);
                var path = await OnUi<string?>(() =>
                {
                    using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"zwedesk-folders-{DateTime.Now:yyyyMMdd-HHmm}.csv", Title = "Export user folders" };
                    return dlg.ShowDialog(_form) == DialogResult.OK ? dlg.FileName : null;
                });
                if (path is null) return new { cancelled = true, path = (string?)null };
                await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(true));
                return new { cancelled = false, path };
            },
            ["shell.openLogs"] = (_, _) => { Open(AppPaths.LogsDir); return Task.FromResult<object?>(new { ok = true }); },
            ["shell.openDataDir"] = (_, _) => { Open(AppPaths.DataDir); return Task.FromResult<object?>(new { ok = true }); },
            ["ui.error"] = (p, _) => { Log.Error("ui: " + (Str(p, "message") ?? "?") + "\n" + (Str(p, "stack") ?? "")); return Task.FromResult<object?>(new { ok = true }); },
        };
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("open folder failed: " + ex.Message); }
    }
}
