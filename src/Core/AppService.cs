using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZweDesk.Providers;

namespace ZweDesk.Core;

/// <summary>Business logic behind every UI operation. Provider-agnostic; all remote work goes through <see cref="IServerProvider"/>.</summary>
public sealed class AppService
{
    private static readonly Regex CodeRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private readonly Db _db;
    private readonly IServerProvider _provider;
    private LoginSession? _session;

    public AppService(Db db, IServerProvider provider)
    {
        _db = db;
        _provider = provider;
    }

    /// <summary>Fire-and-forget push of an event to the UI (set by the bridge).</summary>
    public Action<string, object> Push { get; set; } = (_, _) => { };

    public Db Db => _db;
    public string Mode => _provider.Mode;
    public LoginSession? Session => _session;

    public sealed class LoginSession
    {
        public required Credential Cred { get; init; }
        public required AdInfo Ad { get; init; }
        public required long DomainId { get; init; }
        public DateTime LoginAt { get; } = DateTime.UtcNow;
    }

    private (DomainRec Domain, Credential Cred) Require(long domainId)
    {
        var s = _session ?? throw new AppException("Not logged in.");
        if (s.DomainId != domainId) throw new AppException("The session belongs to a different domain. Log in again.");
        var d = _db.GetDomain(domainId) ?? throw new AppException("Domain not found.");
        return (d, s.Cred);
    }

    private void Audit(string op, string? domain, string? server, string? target, object? parameters, bool ok, string? error)
    {
        var actor = _session?.Cred.LogonName ?? Environment.UserName;
        _db.AddAudit(actor, domain, server, op, target, parameters is null ? null : JsonSerializer.Serialize(parameters, IssueEngine.Json), ok, error);
    }

    // ------------------------------------------------------------------ auth

    public static (string User, string? Domain) ParseUserInput(string input)
    {
        input = input.Trim();
        var bs = input.IndexOf('\\');
        if (bs >= 0) return (input[(bs + 1)..], input[..bs]);
        var at = input.IndexOf('@');
        if (at >= 0) return (input[..at], input[(at + 1)..]);
        return (input, null);
    }

    /// <summary>First start on a domain-joined computer: the environment is that domain; everything else is discovered at sign-in or entered by the user.</summary>
    public static void SeedFromMachineDomain(Db db)
    {
        var m = MachineDomain.Query();
        if (m is null)
        {
            Log.Info("first run: this computer is not joined to a domain; no environment created");
            return;
        }
        db.SaveDomain(null, m.DnsName, m.Netbios, "", LocalFqdn());
        Log.Info($"first run: environment {m.DnsName} ({m.Netbios}) created from this computer's domain membership");
    }

    public async Task<object> LoginAsync(long domainId, string userInput, string password, bool remember, CancellationToken ct)
    {
        var domain = _db.GetDomain(domainId) ?? throw new AppException("Domain not found.");
        var (user, _) = ParseUserInput(userInput);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) throw new AppException("User name and password are required.");

        var cred = new Credential { DomainName = domain.Name, User = user, Password = password, Netbios = domain.Netbios };
        AdInfo ad;
        try
        {
            ad = await _provider.ValidateCredentialsAsync(domain, cred, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Audit("login", domain.Name, null, user, null, false, ex.Message);
            throw;
        }

        if (!string.IsNullOrEmpty(ad.Netbios) && !ad.Netbios.Equals(domain.Netbios, StringComparison.OrdinalIgnoreCase))
        {
            _db.SaveDomain(domain.Id, domain.Name, ad.Netbios, domain.DfsRoot, domain.DfsHostFqdn);
            domain = _db.GetDomain(domain.Id)!;
        }
        cred.Netbios = domain.Netbios;

        // AD cmdlets must run on a domain controller and DFSN cmdlets on a namespace server of the root. When the environment
        // still points at this machine (the first-run default) and this machine is not a DC, pick sensible hosts automatically:
        // the DC that answered the LDAP bind, and the first namespace server found in AD (or the DC when none is found).
        var notices = new List<string>();
        var localIsDc = LocalIsDomainController();
        var local = LocalFqdn();
        bool IsLocal(string h) => string.IsNullOrWhiteSpace(h) || h.Equals(local, StringComparison.OrdinalIgnoreCase) || h.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        // No DFS root yet (environment created from the machine's domain membership): take it from AD when the domain has exactly one namespace.
        if (string.IsNullOrEmpty(domain.DfsRoot))
        {
            try
            {
                var roots = await _provider.DiscoverNamespaceRootsAsync(domain, cred, ct);
                if (roots.Count == 1)
                {
                    _db.SaveEnvironment(domain with { DfsRoot = roots[0] });
                    domain = _db.GetDomain(domain.Id)!;
                    notices.Add($"DFS namespace {roots[0]} found in Active Directory and set as the DFS root. Change it under Environment if the user folders live elsewhere.");
                    Log.Info("login: dfs root set from AD: " + roots[0]);
                }
                else if (roots.Count > 1)
                    notices.Add($"{domain.Name} has {roots.Count} DFS namespaces ({string.Join(", ", roots)}). Choose the one that holds the user folders under Environment → DFS namespace → Find namespaces.");
                else
                    notices.Add("No domain-based DFS namespace was found in Active Directory. If the user folders are published through DFS, enter the root under Environment.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("namespace root discovery failed: " + ex.Message); }
        }
        List<string> nsServers = new();
        if (!string.IsNullOrEmpty(domain.DfsRoot))
        {
            try { nsServers = await _provider.DiscoverNamespaceServersAsync(domain, cred, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("namespace discovery failed: " + ex.Message); }
        }
        var dc = domain.DcFqdn;
        var dfs = domain.DfsHostFqdn;
        if (!localIsDc && !string.IsNullOrEmpty(ad.DcHost) && IsLocal(dc)) dc = ad.DcHost;
        if (!localIsDc && IsLocal(dfs)) dfs = nsServers.FirstOrDefault() ?? ad.DcHost ?? dfs;
        if (dc != domain.DcFqdn || dfs != domain.DfsHostFqdn)
        {
            _db.SaveEnvironment(domain with { DcFqdn = dc, DfsHostFqdn = dfs });
            domain = _db.GetDomain(domain.Id)!;
            notices.Add($"This computer is not a domain controller. Directory server set to {dc}; DFS host set to {dfs}" +
                        (nsServers.Count > 0 ? $" (namespace servers of {domain.DfsRoot}: {string.Join(", ", nsServers)})" : "") + ". Change them under Environment if needed.");
            Log.Info($"login: environment hosts set to dc={dc} dfs={dfs}");
        }
        else if (nsServers.Count > 0 && !nsServers.Any(n => SameHostName(n, domain.DfsHostFqdn)))
        {
            notices.Add($"The DFS host {domain.DfsHostFqdn} is not a namespace server of {domain.DfsRoot} (namespace servers: {string.Join(", ", nsServers)}). DFS commands work best on a namespace server — change it under Environment.");
        }
        _session = new LoginSession { Cred = cred, Ad = ad, DomainId = domain.Id };

        if (remember) RememberCredential(domain.Id, user, password);
        else _db.SetSetting("cred_" + domain.Id, null);

        Audit("login", domain.Name, null, cred.LogonName, null, true, null);
        Log.Info($"login: {cred.LogonName} ({_provider.Mode})");

        // non-fatal connectivity check of every enabled server; the outcome is kept on the server row (dashboard, Environment page)
        var servers = _db.ListServers(domain.Id).Where(s => s.Enabled).ToList();
        var checks = await Task.WhenAll(servers.Select(async s =>
        {
            try
            {
                var who = await _provider.PingAsync(s.Fqdn, cred, ct);
                if (s.LastError is not null && s.LastError.StartsWith(WinRmErrorPrefix, StringComparison.Ordinal)) _db.SetServerScanState(s.Id, null, null);
                return new { s.Id, s.Name, ok = true, detail = who };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.SetServerScanState(s.Id, null, WinRmErrorPrefix + ex.Message);
                return new { s.Id, s.Name, ok = false, detail = ex.Message };
            }
        }));

        return new { user = cred.LogonName, domain = domain.Name, netbios = domain.Netbios, domainId = domain.Id, mode = _provider.Mode, servers = checks, notices };
    }

    public static bool SameHostName(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.Split('.')[0].Equals(b.Split('.')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>Prefix of a server's last_error when it was written by the sign-in connectivity check rather than a scan.</summary>
    public const string WinRmErrorPrefix = "Not reachable: ";

    public async Task<object> DiscoverNamespaceRootsAsync(long domainId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var roots = await _provider.DiscoverNamespaceRootsAsync(domain, cred, ct);
        return new { roots, current = domain.DfsRoot };
    }

    public async Task<object> DiscoverNamespaceServersAsync(long domainId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var servers = await _provider.DiscoverNamespaceServersAsync(domain, cred, ct);
        return new { domain.DfsRoot, servers, current = domain.DfsHostFqdn, currentIsNamespaceServer = servers.Any(n => SameHostName(n, domain.DfsHostFqdn)) };
    }

    /// <summary>True when this machine is a domain controller (ProductType LanmanNT).</summary>
    public static bool LocalIsDomainController()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            return string.Equals(key?.GetValue("ProductType") as string, "LanmanNT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public void Logout()
    {
        if (_session is not null) Log.Info("logout: " + _session.Cred.LogonName);
        _session = null;
    }

    private void RememberCredential(long domainId, string user, string password)
    {
        try
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { user, password })), null, DataProtectionScope.CurrentUser);
            _db.SetSetting("cred_" + domainId, Convert.ToBase64String(blob));
        }
        catch (Exception ex)
        {
            Log.Warn("remember credential failed: " + ex.Message);
        }
    }

    public object? RememberedCredential(long domainId)
    {
        var b64 = _db.GetSetting("cred_" + domainId);
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser));
            using var doc = JsonDocument.Parse(json);
            return new { user = doc.RootElement.GetProperty("user").GetString(), password = doc.RootElement.GetProperty("password").GetString() };
        }
        catch
        {
            _db.SetSetting("cred_" + domainId, null);
            return null;
        }
    }

    public async Task<object> TestServerAsync(long serverId, CancellationToken ct)
    {
        var s = _db.GetServer(serverId) ?? throw new AppException("Server not found.");
        var (_, cred) = Require(s.DomainId);
        var who = await _provider.PingAsync(s.Fqdn, cred, ct);
        return new { ok = true, detail = who };
    }

    // ------------------------------------------------------------------ diagnostics

    public sealed record DiagRow(string Key, string Label, string Status, string Detail, string? Fix = null);

    public async Task<object> DiagnosticsAsync(long domainId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var servers = _db.ListServers(domainId).Where(s => s.Enabled).ToList();
        Push("progress", new { scope = "diag", status = "running", message = $"Probing {servers.Count} server(s) and the DFS host" });
        var reports = await Task.WhenAll(servers.Select(s => DiagServerAsync(s, cred, ct)));
        var dfs = await DiagDfsHostAsync(domain, cred, false, ct);
        Push("progress", new { scope = "diag", status = "done" });
        return new { servers = reports, dfsHost = dfs };
    }

    private async Task<object> DiagServerAsync(ServerRec s, Credential cred, CancellationToken ct)
    {
        var rows = new List<DiagRow>();
        if (_provider.Mode == "mock")
        {
            rows.Add(new DiagRow("dns", "DNS resolution", "info", "skipped in demo mode"));
            rows.Add(new DiagRow("icmp", "ICMP ping", "info", "skipped in demo mode"));
        }
        else
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(s.Fqdn, ct);
                rows.Add(new DiagRow("dns", "DNS resolution", "ok", string.Join(", ", addrs.Select(a => a.ToString()))));
            }
            catch (Exception ex) { rows.Add(new DiagRow("dns", "DNS resolution", "fail", ex.Message)); }

            try
            {
                using var ping = new Ping();
                var r = await ping.SendPingAsync(s.Fqdn, 2000);
                rows.Add(r.Status == IPStatus.Success
                    ? new DiagRow("icmp", "ICMP ping", "ok", $"{r.RoundtripTime} ms")
                    : new DiagRow("icmp", "ICMP ping", "warn", r.Status + " (ICMP may be filtered; not required)"));
            }
            catch (Exception ex) { rows.Add(new DiagRow("icmp", "ICMP ping", "warn", ex.InnerException?.Message ?? ex.Message)); }
        }

        bool winrm;
        try
        {
            var who = await _provider.PingAsync(s.Fqdn, cred, ct);
            rows.Add(new DiagRow("winrm", "WinRM (Invoke-Command) with your credentials", "ok", who));
            winrm = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(new DiagRow("winrm", "WinRM (Invoke-Command) with your credentials", "fail", ex.Message));
            winrm = false;
        }

        if (!winrm)
        {
            foreach (var (k, l) in new[] { ("admin", "Local administrator on server"), ("fsrm", "FSRM role (File Server Resource Manager)"), ("root", "User data root exists"), ("share", "SMB share for the user data root") })
                rows.Add(new DiagRow(k, l, "skip", "skipped — WinRM failed"));
            return new { s.Id, s.Name, s.Fqdn, s.LocalRoot, s.ShareName, rows };
        }

        try
        {
            var p = await _provider.ProbeServerAsync(s, cred, ct);
            rows.Add(new DiagRow("os", "Operating system", "info", $"{p.Computer}: {p.Os ?? "unknown"}"));
            rows.Add(new DiagRow("admin", "Local administrator on server", p.IsAdmin ? "ok" : "fail", p.IsAdmin ? "yes" : "the account is not a member of Administrators on this server"));
            rows.Add(p.FsrmInstalled switch
            {
                true => new DiagRow("fsrm", "FSRM role (File Server Resource Manager)", "ok", "installed"),
                false => new DiagRow("fsrm", "FSRM role (File Server Resource Manager)", "fail", "not installed — quotas cannot be managed", "installFsrm"),
                null => new DiagRow("fsrm", "FSRM role (File Server Resource Manager)", "warn", p.FsrmModule ? "role state unknown, PowerShell module present" : "role state unknown (Get-WindowsFeature unavailable — not a Windows Server?)"),
            });
            _db.SetServerFsrm(s.Id, p.FsrmInstalled ?? p.FsrmModule);
            rows.Add(new DiagRow("root", $"User data root {s.LocalRoot}", p.RootExists ? "ok" : "fail", p.RootExists ? "exists" : "path not found on the server"));

            var root = s.LocalRoot.TrimEnd('\\');
            var share = p.Shares.FirstOrDefault(x => x.Path.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase));
            if (share is not null)
            {
                _db.SetServerShare(s.Id, share.Name);
                rows.Add(new DiagRow("share", "SMB share for the user data root", "ok", $"\\\\{s.Fqdn}\\{share.Name}"));
            }
            else
            {
                var listed = p.Shares.Count == 0 ? "no shares found" : "shares: " + string.Join(", ", p.Shares.Select(x => $"{x.Name} → {x.Path}"));
                rows.Add(new DiagRow("share", "SMB share for the user data root", string.IsNullOrEmpty(s.ShareName) ? "fail" : "warn",
                    (string.IsNullOrEmpty(s.ShareName) ? $"no share exports {s.LocalRoot}; DFS links cannot be built. " : $"no share exports {s.LocalRoot}; keeping configured share '{s.ShareName}'. ") + listed));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(new DiagRow("probe", "Server probe", "fail", ex.Message));
        }

        var fresh = _db.GetServer(s.Id) ?? s;
        return new { s.Id, s.Name, s.Fqdn, s.LocalRoot, fresh.ShareName, rows };
    }

    private async Task<object> DiagDfsHostAsync(DomainRec domain, Credential cred, bool installTools, CancellationToken ct)
    {
        var rows = new List<DiagRow>();
        try
        {
            var ns = await _provider.DiscoverNamespaceServersAsync(domain, cred, ct);
            if (ns.Count == 0) rows.Add(new DiagRow("ns", $"Namespace servers of {domain.DfsRoot}", "warn", "none found in AD (stand-alone namespace, or the root name is wrong)"));
            else rows.Add(new DiagRow("ns", $"Namespace servers of {domain.DfsRoot}", ns.Any(n => SameHostName(n, domain.DfsHostFqdn)) ? "ok" : "warn",
                string.Join(", ", ns) + (ns.Any(n => SameHostName(n, domain.DfsHostFqdn)) ? "" : $" — the configured DFS host {domain.DfsHostFqdn} is not one of them")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { rows.Add(new DiagRow("ns", "Namespace servers", "warn", ex.Message)); }
        try
        {
            var who = await _provider.PingAsync(domain.DfsHostFqdn, cred, ct);
            rows.Add(new DiagRow("winrm", "WinRM to DFS host", "ok", who));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(new DiagRow("winrm", "WinRM to DFS host", "fail", ex.Message + (domain.DfsHostFqdn.Equals(LocalFqdn(), StringComparison.OrdinalIgnoreCase) ? " — on this machine run: Enable-PSRemoting -Force" : "")));
            rows.Add(new DiagRow("dfsn", "PowerShell module DFSN", "skip", "skipped — WinRM failed"));
            rows.Add(new DiagRow("root", $"Namespace {domain.DfsRoot}", "skip", "skipped — WinRM failed"));
            return new { host = domain.DfsHostFqdn, domain.DfsRoot, rows };
        }
        try
        {
            var p = await _provider.ProbeDfsHostAsync(domain, installTools, cred, ct);
            rows.Add(p.DfsnModule
                ? new DiagRow("dfsn", "PowerShell module DFSN", "ok", "available" + (p.RsatInstalled == true ? " (RSAT-DFS-Mgmt-Con installed)" : ""))
                : new DiagRow("dfsn", "PowerShell module DFSN", "fail", "missing — install the DFS management tools", "installDfsTools"));
            rows.Add(p.RootReachable
                ? new DiagRow("root", $"Namespace {domain.DfsRoot}", "ok", "reachable")
                : new DiagRow("root", $"Namespace {domain.DfsRoot}", "fail", p.Error ?? "not reachable"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(new DiagRow("probe", "DFS host probe", "fail", ex.Message));
        }
        return new { host = domain.DfsHostFqdn, domain.DfsRoot, rows };
    }

    public static string LocalFqdn()
    {
        try
        {
            var dn = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrEmpty(dn) ? Environment.MachineName : Environment.MachineName + "." + dn;
        }
        catch { return Environment.MachineName; }
    }

    public async Task<object> FixAsync(long domainId, string kind, long? serverId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        switch (kind)
        {
            case "installFsrm":
            {
                var s = _db.GetServer(serverId ?? 0) ?? throw new AppException("Server not found.");
                Push("progress", new { scope = "fix", status = "running", message = $"Installing FSRM on {s.Name} (this can take a few minutes)" });
                try
                {
                    var ok = await _provider.InstallFsrmAsync(s, cred, ct);
                    _db.SetServerFsrm(s.Id, ok);
                    Audit("install_fsrm", domain.Name, s.Name, s.Fqdn, null, ok, ok ? null : "feature not installed after Install-WindowsFeature");
                    Push("progress", new { scope = "fix", status = "done" });
                    return new { ok, message = ok ? $"FSRM installed on {s.Name}" : "Install-WindowsFeature finished but FSRM is still not installed" };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Audit("install_fsrm", domain.Name, s.Name, s.Fqdn, null, false, ex.Message);
                    Push("progress", new { scope = "fix", status = "done" });
                    throw;
                }
            }
            case "installDfsTools":
            {
                Push("progress", new { scope = "fix", status = "running", message = $"Installing DFS management tools on {domain.DfsHostFqdn}" });
                try
                {
                    var report = await DiagDfsHostAsync(domain, cred, true, ct);
                    Audit("install_dfs_tools", domain.Name, domain.DfsHostFqdn, domain.DfsHostFqdn, null, true, null);
                    Push("progress", new { scope = "fix", status = "done" });
                    return new { ok = true, dfsHost = report };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Audit("install_dfs_tools", domain.Name, domain.DfsHostFqdn, domain.DfsHostFqdn, null, false, ex.Message);
                    Push("progress", new { scope = "fix", status = "done" });
                    throw;
                }
            }
            default:
                throw new AppException("Unknown fix: " + kind);
        }
    }

    // ------------------------------------------------------------------ scan

    public async Task<object> ScanAsync(long domainId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var servers = _db.ListServers(domainId).Where(s => s.Enabled).ToList();
        if (servers.Count == 0) throw new AppException("No enabled servers in this domain. Add one under Domains & Servers.");

        Push("progress", new { scope = "scan", status = "running", servers = servers.Select(s => new { s.Id, s.Name, status = "running" }), dfs = "pending" });
        var results = new ConcurrentDictionary<long, object>();
        var okFlags = new ConcurrentDictionary<long, bool>();

        await Task.WhenAll(servers.Select(async s =>
        {
            try
            {
                var r = await _provider.ScanFoldersAsync(s, cred, ct);
                var rows = r.Folders.Select(f => IssueEngine.BuildFolder(s.Id, f, cred.Netbios, domain.Name)).ToList();
                _db.ReplaceFolders(s.Id, rows);
                _db.SetServerScanState(s.Id, DateTime.UtcNow, null);
                _db.SetServerFsrm(s.Id, r.FsrmModule);
                okFlags[s.Id] = true;
                results[s.Id] = new { s.Id, s.Name, ok = true, count = rows.Count };
                Push("progress", new { scope = "scan", serverId = s.Id, server = s.Name, status = "done", count = rows.Count });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.SetServerScanState(s.Id, null, ex.Message);
                okFlags[s.Id] = false;
                results[s.Id] = new { s.Id, s.Name, ok = false, error = ex.Message };
                Push("progress", new { scope = "scan", serverId = s.Id, server = s.Name, status = "error", message = ex.Message });
                Log.Warn($"scan {s.Name} failed: {ex.Message}");
            }
        }));
        ct.ThrowIfCancellationRequested();

        string? dfsError = null;
        var linkCount = 0;
        try
        {
            Push("progress", new { scope = "scan", dfs = "running", host = domain.DfsHostFqdn });
            var links = await _provider.ScanDfsLinksAsync(domain, cred, ct);
            _db.ReplaceLinks(domain.Id, links.Select(l => new DfsLinkRec(0, domain.Id, l.Name, Db.Key(l.Name), l.Path,
                JsonSerializer.Serialize(l.Targets, IssueEngine.Json), l.State, DateTime.UtcNow)));
            _db.SetDfsScanState(domain.Id, DateTime.UtcNow, null);
            linkCount = links.Count;
            Push("progress", new { scope = "scan", dfs = "done", count = linkCount });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            dfsError = ex.Message;
            _db.SetDfsScanState(domain.Id, null, ex.Message);
            Push("progress", new { scope = "scan", dfs = "error", message = ex.Message });
            Log.Warn("dfs scan failed: " + ex.Message);
        }

        IssueEngine.Recompute(_db, domain);
        var issues = _db.ListIssues(domain.Id);
        Audit("scan", domain.Name, null, null, new { servers = results.Values, links = linkCount, dfsError }, dfsError is null && okFlags.Values.All(v => v), dfsError);
        Push("progress", new { scope = "scan", status = "done" });
        return new { servers = results.Values, links = linkCount, dfsError, issues = issues.Count };
    }

    // ------------------------------------------------------------------ views

    public sealed record FolderView(
        long Id, long ServerId, string ServerName, string Name, string Path, string? Owner, string? UserRights, List<Ace> ExtraAccess,
        long? QuotaBytes, long? UsedBytes, double? Pct, DateTime? LastWriteUtc, DateTime ScannedAt,
        string LinkStatus, List<string> LinkTargets, List<string> Issues, bool IsUserFolder);

    public List<FolderView> Folders(long domainId)
    {
        var servers = _db.ListServers(domainId).ToDictionary(s => s.Id);
        var links = _db.ListLinks(domainId).ToDictionary(l => l.NameKey);
        var issues = _db.ListIssues(domainId)
            .GroupBy(i => i.NameKey.Split('@')[0])
            .ToDictionary(g => g.Key, g => g.ToList());
        var list = new List<FolderView>();
        foreach (var f in _db.ListFolders(domainId))
        {
            if (!servers.TryGetValue(f.ServerId, out var s)) continue;
            links.TryGetValue(f.NameKey, out var link);
            var targets = link is null ? new List<LinkTarget>() : IssueEngine.ParseTargets(link.TargetsJson);
            var linkStatus = link is null ? "missing" : targets.Any(t => IssueEngine.SameHost(IssueEngine.HostOfUnc(t.Target), s)) ? "ok" : "mismatch";
            var kinds = new List<string>();
            if (issues.TryGetValue(f.NameKey, out var li))
            {
                foreach (var i in li)
                {
                    var perServer = i.NameKey.Contains('@');
                    if (perServer && !i.NameKey.EndsWith("@" + s.Name.ToLowerInvariant(), StringComparison.Ordinal)) continue;
                    if (i.Kind == IssueKinds.FolderWithoutLink)
                    {
                        // per-folder issue stored under the plain key; only count it for the server it names
                        try
                        {
                            using var doc = JsonDocument.Parse(i.DetailsJson);
                            if (doc.RootElement.TryGetProperty("serverId", out var sid) && sid.GetInt64() != s.Id) continue;
                        }
                        catch { }
                    }
                    if (!kinds.Contains(i.Kind)) kinds.Add(i.Kind);
                }
            }
            double? pct = f.QuotaBytes is > 0 && f.UsedBytes.HasValue ? Math.Round(100.0 * f.UsedBytes.Value / f.QuotaBytes.Value, 1) : null;
            list.Add(new FolderView(f.Id, s.Id, s.Name, f.Name, f.Path, f.Owner, f.UserRights, IssueEngine.ParseAces(f.ExtraAccessJson),
                f.QuotaBytes, f.UsedBytes, pct, f.LastWriteUtc, f.ScannedAt, linkStatus, targets.Select(t => t.Target).ToList(), kinds, IssueEngine.IsUserFolder(f.Name)));
        }
        return list;
    }

    public object FolderDetails(long folderId)
    {
        var f = _db.GetFolder(folderId) ?? throw new AppException("Folder not found.");
        var s = _db.GetServer(f.ServerId) ?? throw new AppException("Server not found.");
        var view = Folders(s.DomainId).FirstOrDefault(v => v.Id == folderId) ?? throw new AppException("Folder not found.");
        var link = _db.ListLinks(s.DomainId).FirstOrDefault(l => l.NameKey == f.NameKey);
        return new
        {
            folder = view,
            aces = IssueEngine.ParseAces(f.AcesJson),
            link = link is null ? null : new { link.LinkPath, link.State, targets = IssueEngine.ParseTargets(link.TargetsJson) },
            audit = _db.AuditForTarget(f.Path),
            server = new { s.Id, s.Name, s.Fqdn, s.ShareName, s.LocalRoot, s.FsrmInstalled },
        };
    }

    public object Dashboard(long domainId)
    {
        var domain = _db.GetDomain(domainId) ?? throw new AppException("Domain not found.");
        var servers = _db.ListServers(domainId);
        var folders = Folders(domainId).Where(f => f.IsUserFolder).ToList();
        var issues = _db.ListIssues(domainId);
        var links = _db.ListLinks(domainId);
        return new
        {
            domain = new { domain.Id, domain.Name, domain.Netbios, domain.DfsRoot, domain.DfsHostFqdn },
            dfsScanError = _db.GetSetting("dfs_scan_error_" + domainId),
            servers = servers.Select(s => new { s.Id, s.Name, s.Fqdn, s.Enabled, s.LastScanAt, s.LastError, s.ShareName, s.FsrmInstalled, folders = folders.Count(f => f.ServerId == s.Id), usedBytes = folders.Where(f => f.ServerId == s.Id).Sum(f => f.UsedBytes ?? 0), quotaBytes = folders.Where(f => f.ServerId == s.Id).Sum(f => f.QuotaBytes ?? 0) }),
            counts = new
            {
                servers = servers.Count(s => s.Enabled),
                folders = folders.Count,
                links = links.Count,
                issues = issues.Count,
                withQuota = folders.Count(f => f.QuotaBytes.HasValue),
                over90 = folders.Count(f => f.Pct >= 90),
                over100 = folders.Count(f => f.Pct >= 100),
            },
            issuesByKind = issues.GroupBy(i => i.Kind).Select(g => new { kind = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
            topUsage = folders.Where(f => f.UsedBytes.HasValue).OrderByDescending(f => f.UsedBytes).Take(10),
            critical = folders.Where(f => f.Pct >= 90).OrderByDescending(f => f.Pct).Take(20),
            lastScan = servers.Where(s => s.LastScanAt.HasValue).Select(s => s.LastScanAt).DefaultIfEmpty(null).Max(),
            totalUsed = folders.Sum(f => f.UsedBytes ?? 0),
            totalQuota = folders.Sum(f => f.QuotaBytes ?? 0),
        };
    }

    /// <summary>
    /// Every link with the verdict of the folder check: ok (a target holds the folder), mismatch (folder exists, link points elsewhere),
    /// nofolder (every file server was inventoried and none has it), external (targets are not configured file servers) or
    /// unknown — at least one file server has no successful scan, so absence proves nothing.
    /// </summary>
    public object Links(long domainId)
    {
        var servers = _db.ListServers(domainId);
        var unverified = servers.Where(s => s.Enabled && !IssueEngine.IsInventoried(s)).Select(s => s.Name).ToList();
        var foldersComplete = unverified.Count == 0;
        var folderKeys = _db.ListFolders(domainId).GroupBy(f => f.NameKey).ToDictionary(g => g.Key, g => g.Select(f => servers.FirstOrDefault(s => s.Id == f.ServerId)?.Name ?? "?").ToList());
        var rows = _db.ListLinks(domainId).Select(l =>
        {
            var targets = IssueEngine.ParseTargets(l.TargetsJson);
            folderKeys.TryGetValue(l.NameKey, out var on);
            var hosts = targets.Select(t => IssueEngine.HostOfUnc(t.Target)).Where(h => h is not null).ToList();
            var targetServers = servers.Where(s => hosts.Any(h => IssueEngine.SameHost(h, s))).ToList();
            string status;
            if (on is { Count: > 0 }) status = targetServers.Count > 0 && targetServers.All(ts => on.Contains(ts.Name)) ? "ok" : "mismatch";
            else if (hosts.Count == 0) status = foldersComplete ? "notarget" : "unknown";
            else if (targetServers.Count == 0) status = "external";
            else if (targetServers.All(IssueEngine.IsInventoried)) status = "nofolder";   // its own target(s) were inventoried: the folder is not there
            else status = "unknown";
            return new { l.Id, l.Name, l.LinkPath, l.State, targets, foldersOn = on ?? new List<string>(), status, l.ScannedAt };
        }).ToList();
        return new { links = rows, unverified };
    }

    public static JsonElement ParseDetails(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public object Issues(long domainId) =>
        _db.ListIssues(domainId).Select(i => new { i.Id, i.Kind, i.NameKey, details = ParseDetails(i.DetailsJson), i.DetectedAt }).ToList();

    // ------------------------------------------------------------------ mutations

    public async Task<object> SetQuotaAsync(long folderId, long bytes, CancellationToken ct)
    {
        if (bytes < 100L * 1024 * 1024) throw new AppException("Quota must be at least 100 MB.");
        var f = _db.GetFolder(folderId) ?? throw new AppException("Folder not found.");
        var s = _db.GetServer(f.ServerId) ?? throw new AppException("Server not found.");
        var (domain, cred) = Require(s.DomainId);
        try
        {
            var q = await _provider.SetQuotaAsync(s, f.Path, bytes, cred, ct);
            _db.SetFolderQuota(f.Id, q.Size, q.Usage);
            _db.SetServerFsrm(s.Id, true);
            Audit("set_quota", domain.Name, s.Name, f.Path, new { bytes, previous = f.QuotaBytes }, true, null);
            IssueEngine.Recompute(_db, domain);
            return new { quotaBytes = q.Size, usedBytes = q.Usage };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Audit("set_quota", domain.Name, s.Name, f.Path, new { bytes, previous = f.QuotaBytes }, false, ex.Message);
            throw;
        }
    }

    public async Task<object> ComputeSizeAsync(long folderId, CancellationToken ct)
    {
        var f = _db.GetFolder(folderId) ?? throw new AppException("Folder not found.");
        var s = _db.GetServer(f.ServerId) ?? throw new AppException("Server not found.");
        var (_, cred) = Require(s.DomainId);
        var used = await _provider.ComputeSizeAsync(s, f.Path, cred, ct);
        _db.SetFolderUsed(f.Id, used);
        return new { usedBytes = used };
    }

    public async Task<object> LookupUserAsync(long domainId, string code, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        code = code.Trim();
        if (!CodeRx.IsMatch(code) || code.StartsWith('_')) throw new AppException("Invalid user code. Use letters, digits, '.', '_' or '-' (max 64), not starting with '_'.");
        var user = await _provider.FindUserAsync(domain, code, cred, ct);
        var existing = _db.ListFolders(domainId).Where(f => f.NameKey == Db.Key(code))
            .Select(f => new { server = _db.GetServer(f.ServerId)?.Name, f.Path }).ToList();
        var link = _db.ListLinks(domainId).FirstOrDefault(l => l.NameKey == Db.Key(code));
        return new { found = user is not null, user, existingFolders = existing, existingLink = link?.LinkPath };
    }

    public async Task<object> CreateUserFolderAsync(long domainId, long serverId, string code, long quotaBytes, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var ad = _session!.Ad;
        code = code.Trim();
        if (!CodeRx.IsMatch(code) || code.StartsWith('_')) throw new AppException("Invalid user code.");
        if (quotaBytes < 100L * 1024 * 1024) throw new AppException("Quota must be at least 100 MB.");
        var server = _db.GetServer(serverId) ?? throw new AppException("Server not found.");
        if (server.DomainId != domainId) throw new AppException("Server belongs to another domain.");
        if (!server.Enabled) throw new AppException($"{server.Name} is disabled.");
        if (string.IsNullOrEmpty(server.ShareName))
            throw new AppException($"The SMB share of {server.Name} is unknown, so the DFS target cannot be built. Run Diagnostics first (it discovers the share that exports {server.LocalRoot}).");
        if (server.FsrmInstalled == false)
            throw new AppException($"FSRM is not installed on {server.Name}. Install it from Diagnostics before creating folders with quotas.");

        var user = await _provider.FindUserAsync(domain, code, cred, ct) ?? throw new AppException($"AD account '{code}' was not found in {domain.Name}. The account must exist before its folder is created.");
        var key = Db.Key(user.SamAccountName);
        var existing = _db.ListFolders(domainId).Where(f => f.NameKey == key).ToList();
        if (existing.Count > 0)
            throw new AppException($"A folder for {user.SamAccountName} already exists on {string.Join(", ", existing.Select(f => _db.GetServer(f.ServerId)?.Name))}. Nothing was changed.");
        var existingLink = _db.ListLinks(domainId).FirstOrDefault(l => l.NameKey == key);
        if (existingLink is not null)
            throw new AppException($"A DFS link {existingLink.LinkPath} already exists. Nothing was changed.");

        var name = user.SamAccountName;
        var path = server.LocalRoot.TrimEnd('\\') + "\\" + name;
        var linkPath = domain.DfsRoot.TrimEnd('\\') + "\\" + name;
        var targetPath = $"\\\\{server.Fqdn}\\{server.ShareName}\\{name}";
        var steps = new List<object>();
        var failed = false;

        async Task Step(string id, string label, string op, Func<Task> action, object? parameters)
        {
            if (failed) { steps.Add(new { id, label, status = "skipped" }); return; }
            Push("progress", new { scope = "create", status = "running", message = label });
            try
            {
                await action();
                Audit(op, domain.Name, server.Name, path, parameters, true, null);
                steps.Add(new { id, label, status = "ok" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Audit(op, domain.Name, server.Name, path, parameters, false, ex.Message);
                steps.Add(new { id, label, status = "fail", error = ex.Message, details = (ex as AppException)?.Details });
                failed = true;
            }
        }

        await Step("folder", $"Create {path} with standard ACL", "create_folder",
            () => _provider.CreateFolderAsync(server, path, user.Sid, ad.DomainAdminsSid, cred, ct), new { user = name, sid = user.Sid });
        await Step("quota", $"Set FSRM quota {FormatBytes(quotaBytes)}", "set_quota",
            () => _provider.SetQuotaAsync(server, path, quotaBytes, cred, ct), new { bytes = quotaBytes });
        await Step("link", $"Create DFS link {linkPath} → {targetPath}", "create_link",
            () => _provider.CreateDfsLinkAsync(domain, linkPath, targetPath, cred, ct), new { linkPath, targetPath });

        Push("progress", new { scope = "create", status = "running", message = "Refreshing " + server.Name });
        try { await RescanServerAsync(domain, server, cred, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("rescan after create failed: " + ex.Message); }
        Push("progress", new { scope = "create", status = "done" });

        return new { ok = !failed, user, path, linkPath, targetPath, steps, disabledAccount = user.Disabled };
    }

    private async Task RescanServerAsync(DomainRec domain, ServerRec server, Credential cred, CancellationToken ct)
    {
        var r = await _provider.ScanFoldersAsync(server, cred, ct);
        _db.ReplaceFolders(server.Id, r.Folders.Select(f => IssueEngine.BuildFolder(server.Id, f, cred.Netbios, domain.Name)));
        _db.SetServerScanState(server.Id, DateTime.UtcNow, null);
        try
        {
            var links = await _provider.ScanDfsLinksAsync(domain, cred, ct);
            _db.ReplaceLinks(domain.Id, links.Select(l => new DfsLinkRec(0, domain.Id, l.Name, Db.Key(l.Name), l.Path, JsonSerializer.Serialize(l.Targets, IssueEngine.Json), l.State, DateTime.UtcNow)));
            _db.SetDfsScanState(domain.Id, DateTime.UtcNow, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _db.SetDfsScanState(domain.Id, null, ex.Message); Log.Warn("dfs rescan failed: " + ex.Message); }
        IssueEngine.Recompute(_db, domain);
    }

    // ------------------------------------------------------------------ DFS link sync

    public sealed record SyncRow(long FolderId, string Name, string Server, long ServerId, string Path, string LinkPath, string? TargetPath, bool Ready, string? Reason);

    private sealed record SyncPlan(DomainRec Domain, List<SyncRow> ToCreate, List<IssueRec> Manual, List<IssueRec> Dead, List<IssueRec> Mismatch);

    private SyncPlan BuildSyncPlan(long domainId)
    {
        var domain = _db.GetDomain(domainId) ?? throw new AppException("Domain not found.");
        var servers = _db.ListServers(domainId).ToDictionary(s => s.Id);
        var issues = _db.ListIssues(domainId);
        var toCreate = new List<SyncRow>();
        foreach (var i in issues.Where(i => i.Kind == IssueKinds.FolderWithoutLink))
        {
            using var doc = JsonDocument.Parse(i.DetailsJson);
            var d = doc.RootElement;
            if (d.TryGetProperty("duplicate", out var dup) && dup.GetBoolean()) continue;
            var sid = d.GetProperty("serverId").GetInt64();
            if (!servers.TryGetValue(sid, out var s)) continue;
            var name = d.GetProperty("name").GetString() ?? "";
            var linkPath = domain.DfsRoot.TrimEnd('\\') + "\\" + name;
            var ready = !string.IsNullOrEmpty(s.ShareName);
            toCreate.Add(new SyncRow(d.GetProperty("folderId").GetInt64(), name, s.Name, s.Id, d.GetProperty("path").GetString() ?? "", linkPath,
                ready ? $"\\\\{s.Fqdn}\\{s.ShareName}\\{name}" : null, ready, ready ? null : $"share name of {s.Name} unknown — run Diagnostics"));
        }
        return new SyncPlan(domain,
            toCreate.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            issues.Where(i => i.Kind == IssueKinds.DuplicateFolder).ToList(),
            issues.Where(i => i.Kind == IssueKinds.LinkWithoutFolder).ToList(),
            issues.Where(i => i.Kind == IssueKinds.LinkTargetMismatch).ToList());
    }

    public object SyncDryRun(long domainId)
    {
        var p = BuildSyncPlan(domainId);
        return new
        {
            toCreate = p.ToCreate,
            manual = p.Manual.Select(i => ParseDetails(i.DetailsJson)),
            dead = p.Dead.Select(i => ParseDetails(i.DetailsJson)),
            mismatch = p.Mismatch.Select(i => ParseDetails(i.DetailsJson)),
            unverified = _db.ListServers(domainId).Where(s => s.Enabled && !IssueEngine.IsInventoried(s)).Select(s => s.Name).ToList(),
            linksRead = _db.DfsScanOk(domainId),
        };
    }

    public async Task<object> SyncApplyAsync(long domainId, IReadOnlyList<long> folderIds, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var rows = BuildSyncPlan(domainId).ToCreate.ToDictionary(r => r.FolderId);
        var results = new List<object>();
        var n = 0;
        var created = 0;
        foreach (var id in folderIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!rows.TryGetValue(id, out var row)) { results.Add(new { folderId = id, ok = false, error = "not part of the plan any more" }); continue; }
            if (!row.Ready) { results.Add(new { folderId = id, row.Name, ok = false, error = row.Reason }); continue; }
            Push("progress", new { scope = "sync", status = "running", current = ++n, total = folderIds.Count, message = $"Creating {row.LinkPath}" });
            try
            {
                await _provider.CreateDfsLinkAsync(domain, row.LinkPath, row.TargetPath!, cred, ct);
                Audit("create_link", domain.Name, row.Server, row.Path, new { row.LinkPath, row.TargetPath }, true, null);
                results.Add(new { folderId = id, row.Name, row.LinkPath, row.TargetPath, ok = true });
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Audit("create_link", domain.Name, row.Server, row.Path, new { row.LinkPath, row.TargetPath }, false, ex.Message);
                results.Add(new { folderId = id, row.Name, row.LinkPath, row.TargetPath, ok = false, error = ex.Message });
            }
        }
        try
        {
            Push("progress", new { scope = "sync", status = "running", message = "Refreshing DFS links" });
            var links = await _provider.ScanDfsLinksAsync(domain, cred, ct);
            _db.ReplaceLinks(domain.Id, links.Select(l => new DfsLinkRec(0, domain.Id, l.Name, Db.Key(l.Name), l.Path, JsonSerializer.Serialize(l.Targets, IssueEngine.Json), l.State, DateTime.UtcNow)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("dfs rescan after sync failed: " + ex.Message); }
        IssueEngine.Recompute(_db, domain);
        Push("progress", new { scope = "sync", status = "done" });
        return new { created, results };
    }

    public async Task<object> CreateLinkForFolderAsync(long folderId, CancellationToken ct)
    {
        var f = _db.GetFolder(folderId) ?? throw new AppException("Folder not found.");
        var s = _db.GetServer(f.ServerId) ?? throw new AppException("Server not found.");
        var (domain, _) = Require(s.DomainId);
        var copies = _db.ListFolders(domain.Id).Count(x => x.NameKey == f.NameKey);
        if (copies > 1) throw new AppException($"{f.Name} exists on more than one server. Decide manually which copy is the live one; no link is created automatically.");
        if (_db.ListLinks(domain.Id).Any(l => l.NameKey == f.NameKey)) throw new AppException("A DFS link with this name already exists.");
        if (string.IsNullOrEmpty(s.ShareName)) throw new AppException($"The SMB share of {s.Name} is unknown. Run Diagnostics first.");
        var result = await SyncApplyAsync(domain.Id, new[] { folderId }, ct);
        return result;
    }

    // ------------------------------------------------------------------ environment (AD / DFS / Horizon settings)

    public sealed record EnvironmentInput(long? Id, string Name, string Netbios, string DfsRoot, string DfsHostFqdn, string DcFqdn,
        string HorizonUrl, string HorizonAuth, string HorizonUser, string HorizonDomain, bool HorizonIgnoreCert, string HorizonEntitleMode, string? HorizonPassword);

    public long SaveEnvironment(EnvironmentInput e)
    {
        var name = e.Name.Trim().ToLowerInvariant();
        if (name.Length == 0) throw new AppException("Domain name is required.");
        var root = e.DfsRoot.Trim().TrimEnd('\\');
        if (root.Length > 0 && !root.StartsWith("\\\\")) throw new AppException("DFS root must be a UNC path like \\\\corp.example.com\\Users.");
        var dfsHost = string.IsNullOrWhiteSpace(e.DfsHostFqdn) ? LocalFqdn() : e.DfsHostFqdn.Trim();
        var dc = string.IsNullOrWhiteSpace(e.DcFqdn) ? dfsHost : e.DcFqdn.Trim();
        var auth = e.HorizonAuth is "session" or "stored" ? e.HorizonAuth : "none";
        var url = e.HorizonUrl.Trim();
        if (auth != "none" && url.Length == 0) throw new AppException("Horizon Connection Server URL is required when Horizon is enabled.");
        var mode = e.HorizonEntitleMode == "user" ? "user" : "group";
        var existing = e.Id is > 0 ? _db.GetDomain(e.Id.Value) : null;
        var rec = new DomainRec(e.Id ?? 0, name, e.Netbios.Trim().ToUpperInvariant(), root, dfsHost, existing?.CreatedAt ?? DateTime.UtcNow,
            dc, url, auth, e.HorizonUser.Trim(), e.HorizonDomain.Trim(), e.HorizonIgnoreCert, mode,
            existing?.DefaultOuDn ?? "", existing?.DefaultGroupsJson ?? "[]", existing?.DefaultPoolId ?? "");
        var id = _db.SaveEnvironment(rec);
        if (!string.IsNullOrEmpty(e.HorizonPassword)) StoreSecret("horizon_cred_" + id, e.HorizonPassword);
        if (auth != "stored") _db.SetSetting("horizon_cred_" + id, null);
        Log.Info($"environment saved: {name} (horizon={auth})");
        return id;
    }

    private void StoreSecret(string key, string value)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        _db.SetSetting(key, Convert.ToBase64String(blob));
    }

    private string? ReadSecret(string key)
    {
        var b64 = _db.GetSetting(key);
        if (string.IsNullOrEmpty(b64)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public bool HasHorizonPassword(long domainId) => _db.GetSetting("horizon_cred_" + domainId) is not null;

    public async Task<object> HorizonTestAsync(long domainId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        var r = await _provider.HorizonPoolsAsync(domain, cred, ReadSecret("horizon_cred_" + domainId), ct);
        _db.ReplacePools(domainId, r.Pools);
        Audit("horizon_test", domain.Name, domain.HorizonUrl, null, new { pools = r.Pools.Count }, r.Ok, r.Ok ? null : r.Message);
        return new { r.Ok, r.Message, pools = Pools(domainId) };
    }

    public object Pools(long domainId) =>
        _db.ListPools(domainId).Select(p => new { p.PoolId, p.Name, p.DisplayName, p.Type, p.Enabled, entitledGroups = JsonSerializer.Deserialize<List<string>>(p.EntitledGroupsJson) ?? new(), p.EntitledUsers, p.FetchedAt }).ToList();

    // ------------------------------------------------------------------ directory listings (cached per session)

    private readonly Dictionary<long, (List<OuInfo> Ous, List<GroupInfo> Groups, List<DcInfo> Dcs, DateTime At)> _dirCache = new();

    public async Task<object> DirectoryAsync(long domainId, bool refresh, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        if (refresh || !_dirCache.TryGetValue(domainId, out var d))
        {
            Push("progress", new { scope = "dir", status = "running", message = "Reading OUs, groups and domain controllers from " + domain.DirectoryHost });
            try
            {
                var ous = _provider.ListOusAsync(domain, cred, ct);
                var groups = _provider.ListGroupsAsync(domain, cred, ct);
                var dcs = _provider.ListDcsAsync(domain, cred, ct);
                await Task.WhenAll(ous, groups, dcs);
                d = (ous.Result, groups.Result, dcs.Result, DateTime.UtcNow);
                _dirCache[domainId] = d;
            }
            finally { Push("progress", new { scope = "dir", status = "done" }); }
        }
        return new
        {
            ous = d.Ous, groups = d.Groups, dcs = d.Dcs, fetchedAt = d.At,
            defaults = new { ouDn = domain.DefaultOuDn, groups = JsonSerializer.Deserialize<List<string>>(domain.DefaultGroupsJson) ?? new(), poolId = domain.DefaultPoolId },
            pools = Pools(domainId),
            horizon = new { configured = domain.HorizonConfigured, domain.HorizonUrl, entitleMode = domain.HorizonEntitleMode },
            servers = _db.ListServers(domainId).Where(s => s.Enabled).Select(s => new { s.Id, s.Name, s.Fqdn, s.LocalRoot, s.ShareName, s.FsrmInstalled }),
            defaultQuotaBytes = _db.DefaultQuotaBytes,
            mustChangeDefault = _db.GetSetting("must_change_default") != "0",
        };
    }

    // ------------------------------------------------------------------ provisioning wizard

    private static readonly Regex SamRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$", RegexOptions.Compiled);

    public sealed record ProvisionSpec(string FirstName, string LastName, string Sam, string? Password, bool MustChange, string OuDn,
        List<string> Groups, long ServerId, long QuotaBytes, string? PoolId, string PoolEntitle, string? DcFqdn = null);

    public static string GeneratePassword(int length = 14)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789", symbols = "!@#$%&*?-+=";
        var all = upper + lower + digits + symbols;
        var chars = new List<char> { Pick(upper), Pick(lower), Pick(digits), Pick(symbols) };
        while (chars.Count < length) chars.Add(Pick(all));
        // Fisher–Yates with a cryptographic source
        for (var i = chars.Count - 1; i > 0; i--) { var j = RandomNumberGenerator.GetInt32(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars.ToArray());
        static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }

    public async Task<object> ProvisionPlanAsync(long domainId, string sam, long serverId, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        sam = sam.Trim();
        if (!SamRx.IsMatch(sam)) throw new AppException("User name must be 1–20 characters: letters, digits, '.', '_' or '-'.");
        var server = _db.GetServer(serverId);
        var account = await _provider.FindUserAsync(domain, sam, cred, ct);
        var key = Db.Key(sam);
        var folders = _db.ListFolders(domainId).Where(f => f.NameKey == key).Select(f => new { serverId = f.ServerId, server = _db.GetServer(f.ServerId)?.Name, f.Path, f.QuotaBytes }).ToList();
        var link = _db.ListLinks(domainId).FirstOrDefault(l => l.NameKey == key);
        var blockers = new List<string>();
        if (server is null) blockers.Add("Choose a file server.");
        else
        {
            if (!server.Enabled) blockers.Add($"{server.Name} is disabled.");
            if (string.IsNullOrEmpty(server.ShareName)) blockers.Add($"The SMB share of {server.Name} is unknown — run Diagnostics first.");
            if (server.FsrmInstalled == false) blockers.Add($"FSRM is not installed on {server.Name} — install it from Diagnostics.");
            if (folders.Any(f => f.serverId != server.Id)) blockers.Add($"A folder named {sam} already exists on {string.Join(", ", folders.Where(f => f.serverId != server.Id).Select(f => f.server))}. Pick that server or resolve the duplicate first.");
        }
        return new
        {
            account,
            folders,
            link = link is null ? null : new { link.LinkPath, targets = IssueEngine.ParseTargets(link.TargetsJson).Select(t => t.Target) },
            linkOk = link is not null && server is not null && IssueEngine.ParseTargets(link.TargetsJson).Any(t => IssueEngine.SameHost(IssueEngine.HostOfUnc(t.Target), server)),
            blockers,
        };
    }

    public async Task<object> ProvisionAsync(long domainId, ProvisionSpec spec, CancellationToken ct)
    {
        var (domain, cred) = Require(domainId);
        if (!string.IsNullOrWhiteSpace(spec.DcFqdn)) domain = domain with { DcFqdn = spec.DcFqdn.Trim() };
        var ad = _session!.Ad;
        var sam = spec.Sam.Trim();
        var first = spec.FirstName.Trim();
        var last = spec.LastName.Trim();
        if (!SamRx.IsMatch(sam)) throw new AppException("User name must be 1–20 characters: letters, digits, '.', '_' or '-'.");
        if (spec.QuotaBytes < 100L * 1024 * 1024) throw new AppException("Quota must be at least 100 MB.");
        var server = _db.GetServer(spec.ServerId) ?? throw new AppException("Server not found.");
        if (server.DomainId != domainId || !server.Enabled) throw new AppException("Choose an enabled file server of this environment.");
        if (string.IsNullOrEmpty(server.ShareName)) throw new AppException($"The SMB share of {server.Name} is unknown — run Diagnostics first.");
        if (server.FsrmInstalled == false) throw new AppException($"FSRM is not installed on {server.Name} — install it from Diagnostics first.");

        var existing = await _provider.FindUserAsync(domain, sam, cred, ct);
        if (existing is null)
        {
            if (first.Length == 0 || last.Length == 0) throw new AppException("First name and last name are required for a new account.");
            if (string.IsNullOrWhiteSpace(spec.OuDn)) throw new AppException("Choose the OU for the new account.");
            if (spec.Password is not null && spec.Password.Length < 8) throw new AppException("Password must be at least 8 characters (the domain policy may require more).");
        }
        var password = existing is null ? (spec.Password ?? GeneratePassword()) : null;
        var generated = existing is null && spec.Password is null;
        var displayName = existing?.DisplayName ?? (first + " " + last).Trim();
        var upn = sam + "@" + domain.Name;

        var steps = new List<Dictionary<string, object?>>();
        var fatal = false;
        AdUser? user = existing;

        Dictionary<string, object?> Step(string id, string label) { var d = new Dictionary<string, object?> { ["id"] = id, ["label"] = label, ["status"] = "running" }; steps.Add(d); Push("progress", new { scope = "provision", status = "running", message = label }); return d; }
        void Done(Dictionary<string, object?> st, string status, string? detail = null, string? error = null) { st["status"] = status; st["detail"] = detail; st["error"] = error; }

        // 1. account
        {
            var st = Step("account", existing is null ? $"Create AD account {sam} in {spec.OuDn}" : $"AD account {sam}");
            if (existing is not null) Done(st, "exists", "already exists: " + existing.DistinguishedName);
            else
            {
                var adSpec = new AdUserSpec(sam, upn, first, last, displayName, spec.OuDn.Trim(), spec.MustChange);
                try
                {
                    user = await _provider.CreateUserAsync(domain, adSpec, password!, cred, ct);
                    Audit("create_ad_user", domain.Name, domain.DirectoryHost, user.DistinguishedName, new { sam, upn, ou = spec.OuDn, spec.MustChange, passwordGenerated = generated }, true, null);
                    Done(st, "ok", user.DistinguishedName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Audit("create_ad_user", domain.Name, domain.DirectoryHost, sam, new { sam, upn, ou = spec.OuDn, spec.MustChange, passwordGenerated = generated }, false, ex.Message);
                    Done(st, "fail", null, ex.Message);
                    fatal = true;
                }
            }
        }

        // 2. groups
        var groups = spec.Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!fatal && groups.Count > 0)
        {
            var st = Step("groups", $"Add to {groups.Count} group(s)");
            try
            {
                var r = await _provider.AddToGroupsAsync(domain, sam, groups, cred, ct);
                var detail = string.Join("; ", new[] { r.Added.Count > 0 ? "added: " + string.Join(", ", r.Added) : null, r.AlreadyMember.Count > 0 ? "already member: " + string.Join(", ", r.AlreadyMember) : null }.Where(x => x != null));
                Audit("add_groups", domain.Name, domain.DirectoryHost, sam, new { groups, r.Added, r.AlreadyMember, r.Failed }, r.Failed.Count == 0, r.Failed.Count == 0 ? null : string.Join(" | ", r.Failed));
                Done(st, r.Failed.Count == 0 ? "ok" : "fail", detail, r.Failed.Count == 0 ? null : string.Join(" | ", r.Failed));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Audit("add_groups", domain.Name, domain.DirectoryHost, sam, new { groups }, false, ex.Message);
                Done(st, "fail", null, ex.Message);
            }
        }
        else if (groups.Count > 0) steps.Add(new() { ["id"] = "groups", ["label"] = "Add to groups", ["status"] = "skipped" });

        // 3. folder
        var name = user?.SamAccountName ?? sam;
        var key = Db.Key(name);
        var path = server.LocalRoot.TrimEnd('\\') + "\\" + name;
        var linkPath = domain.DfsRoot.TrimEnd('\\') + "\\" + name;
        var targetPath = $"\\\\{server.Fqdn}\\{server.ShareName}\\{name}";
        var folderRows = _db.ListFolders(domainId).Where(f => f.NameKey == key).ToList();
        if (!fatal)
        {
            var st = Step("folder", $"Create {path} with standard ACL");
            var onServer = folderRows.FirstOrDefault(f => f.ServerId == server.Id);
            var elsewhere = folderRows.Where(f => f.ServerId != server.Id).Select(f => _db.GetServer(f.ServerId)?.Name).ToList();
            if (elsewhere.Count > 0) { Done(st, "fail", null, $"A folder named {name} already exists on {string.Join(", ", elsewhere)}."); fatal = true; }
            else if (onServer is not null) { path = onServer.Path; Done(st, "exists", "already exists"); }
            else
            {
                try
                {
                    await _provider.CreateFolderAsync(server, path, user!.Sid, ad.DomainAdminsSid, cred, ct);
                    Audit("create_folder", domain.Name, server.Name, path, new { user = name, sid = user.Sid }, true, null);
                    Done(st, "ok", path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Audit("create_folder", domain.Name, server.Name, path, new { user = name }, false, ex.Message);
                    Done(st, "fail", null, ex.Message);
                    fatal = true;
                }
            }
        }
        else steps.Add(new() { ["id"] = "folder", ["label"] = "Create folder", ["status"] = "skipped" });

        // 4. quota
        if (!fatal)
        {
            var st = Step("quota", $"Set FSRM quota {FormatBytes(spec.QuotaBytes)}");
            try
            {
                var q = await _provider.SetQuotaAsync(server, path, spec.QuotaBytes, cred, ct);
                Audit("set_quota", domain.Name, server.Name, path, new { bytes = spec.QuotaBytes }, true, null);
                Done(st, "ok", $"{FormatBytes(q.Size)} (used {FormatBytes(q.Usage)})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Audit("set_quota", domain.Name, server.Name, path, new { bytes = spec.QuotaBytes }, false, ex.Message);
                Done(st, "fail", null, ex.Message);
            }
        }
        else steps.Add(new() { ["id"] = "quota", ["label"] = "Set quota", ["status"] = "skipped" });

        // 5. DFS link
        if (!fatal)
        {
            var st = Step("link", $"Create DFS link {linkPath} → {targetPath}");
            var link = _db.ListLinks(domainId).FirstOrDefault(l => l.NameKey == key);
            if (link is not null)
            {
                var targets = IssueEngine.ParseTargets(link.TargetsJson);
                if (targets.Any(t => IssueEngine.SameHost(IssueEngine.HostOfUnc(t.Target), server))) Done(st, "exists", "already exists: " + link.LinkPath);
                else Done(st, "fail", null, $"A link {link.LinkPath} already exists but points to {string.Join(", ", targets.Select(t => t.Target))}. Fix it in DFS Management.");
            }
            else
            {
                try
                {
                    await _provider.CreateDfsLinkAsync(domain, linkPath, targetPath, cred, ct);
                    Audit("create_link", domain.Name, server.Name, path, new { linkPath, targetPath }, true, null);
                    Done(st, "ok", linkPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Audit("create_link", domain.Name, server.Name, path, new { linkPath, targetPath }, false, ex.Message);
                    Done(st, "fail", null, ex.Message);
                }
            }
        }
        else steps.Add(new() { ["id"] = "link", ["label"] = "Create DFS link", ["status"] = "skipped" });

        // 6. Horizon (direct user entitlement; the group mode is covered by step 2)
        if (!string.IsNullOrEmpty(spec.PoolId) && spec.PoolEntitle == "user")
        {
            var pool = _db.ListPools(domainId).FirstOrDefault(p => p.PoolId == spec.PoolId);
            var st = Step("horizon", $"Entitle to Horizon pool {pool?.Name ?? spec.PoolId}");
            if (fatal || user is null) Done(st, "skipped");
            else
            {
                try
                {
                    await _provider.HorizonEntitleUserAsync(domain, spec.PoolId, user.Sid, name, cred, ReadSecret("horizon_cred_" + domainId), ct);
                    Audit("horizon_entitle", domain.Name, domain.HorizonUrl, name, new { poolId = spec.PoolId, pool = pool?.Name }, true, null);
                    Done(st, "ok", pool?.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Audit("horizon_entitle", domain.Name, domain.HorizonUrl, name, new { poolId = spec.PoolId, pool = pool?.Name }, false, ex.Message);
                    Done(st, "fail", null, ex.Message);
                }
            }
        }

        _db.SaveProvisionDefaults(domainId, spec.OuDn ?? "", JsonSerializer.Serialize(groups), spec.PoolId ?? "");
        if (!fatal)
        {
            Push("progress", new { scope = "provision", status = "running", message = "Refreshing " + server.Name });
            try { await RescanServerAsync(domain, server, cred, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("rescan after provisioning failed: " + ex.Message); }
        }
        Push("progress", new { scope = "provision", status = "done" });
        var ok = steps.All(st => st["status"] is "ok" or "exists" or "skipped");
        return new { ok, steps, user, password, passwordGenerated = generated, path, linkPath, targetPath, displayName, upn };
    }

    // ------------------------------------------------------------------ misc

    public static string FormatBytes(long? b)
    {
        if (b is null) return "—";
        double v = b.Value;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public string ExportCsv(long domainId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Server,Folder,Path,Owner,UserRights,ExtraAccess,QuotaBytes,UsedBytes,UsedPercent,LastWriteUtc,LinkStatus,LinkTargets,Issues");
        foreach (var f in Folders(domainId))
        {
            static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            sb.AppendLine(string.Join(",", Q(f.ServerName), Q(f.Name), Q(f.Path), Q(f.Owner), Q(f.UserRights),
                Q(string.Join("; ", f.ExtraAccess.Select(a => $"{a.Id}:{a.Rights}"))), f.QuotaBytes?.ToString() ?? "", f.UsedBytes?.ToString() ?? "",
                f.Pct?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", f.LastWriteUtc?.ToString("o") ?? "", f.LinkStatus,
                Q(string.Join("; ", f.LinkTargets)), Q(string.Join("; ", f.Issues))));
        }
        return sb.ToString();
    }
}
