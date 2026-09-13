using System.DirectoryServices;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZweDesk.Core;
using ZweDesk.Remote;

namespace ZweDesk.Providers;

/// <summary>Talks to Active Directory (LDAP) and to the file / DFS servers (WinRM via <see cref="IRemoteRunner"/>).</summary>
public sealed class RealProvider : IServerProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IRemoteRunner _runner;

    public RealProvider(IRemoteRunner runner) => _runner = runner;

    public string Mode => "real";

    // ------------------------------------------------------------------ Active Directory

    public Task<AdInfo> ValidateCredentialsAsync(DomainRec domain, Credential cred, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var root = new DirectoryEntry($"LDAP://{domain.Name}", cred.LogonName, cred.Password, AuthenticationTypes.Secure);
            var sidBytes = root.Properties["objectSid"].Value as byte[]
                           ?? throw new AppException($"Bound to {domain.Name} but it returned no objectSid (is this a domain?)");
            var domainSid = new SecurityIdentifier(sidBytes, 0).Value;

            var netbios = domain.Netbios;
            string? dcHost = null;
            try
            {
                using var rootDse = new DirectoryEntry($"LDAP://{domain.Name}/RootDSE", cred.LogonName, cred.Password, AuthenticationTypes.Secure);
                dcHost = rootDse.Properties["dnsHostName"].Value as string;
                var config = (string)rootDse.Properties["configurationNamingContext"].Value!;
                var dnc = (string)rootDse.Properties["defaultNamingContext"].Value!;
                using var partitions = new DirectoryEntry($"LDAP://{domain.Name}/CN=Partitions,{config}", cred.LogonName, cred.Password, AuthenticationTypes.Secure);
                using var ds = new DirectorySearcher(partitions, $"(&(objectClass=crossRef)(nCName={LdapEscape(dnc)}))", new[] { "nETBIOSName" });
                var r = ds.FindOne();
                if (r?.Properties["nETBIOSName"]?.Count > 0) netbios = (string)r.Properties["nETBIOSName"][0];
            }
            catch (Exception ex)
            {
                Log.Warn("ad: NetBIOS lookup failed, keeping '" + netbios + "': " + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(netbios)) netbios = domain.Name.Split('.')[0].ToUpperInvariant();

            return new AdInfo(domainSid, netbios, domainSid + "-512", dcHost);
        }
        catch (AppException) { throw; }
        catch (Exception ex)
        {
            throw new AppException(FriendlyLdapError(ex, domain.Name), ex.ToString(), ex);
        }
    }, ct);

    public Task<AdUser?> FindUserAsync(DomainRec domain, string samAccountName, Credential cred, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var root = new DirectoryEntry($"LDAP://{domain.Name}", cred.LogonName, cred.Password, AuthenticationTypes.Secure);
            using var ds = new DirectorySearcher(root,
                $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={LdapEscape(samAccountName)}))",
                new[] { "objectSid", "distinguishedName", "displayName", "userAccountControl", "sAMAccountName" });
            var r = ds.FindOne();
            if (r is null) return null;
            var sid = new SecurityIdentifier((byte[])r.Properties["objectSid"][0], 0).Value;
            var uac = r.Properties["userAccountControl"].Count > 0 ? Convert.ToInt64(r.Properties["userAccountControl"][0], CultureInfo.InvariantCulture) : 0;
            return new AdUser(
                (string)r.Properties["sAMAccountName"][0],
                sid,
                (string)r.Properties["distinguishedName"][0],
                r.Properties["displayName"].Count > 0 ? (string)r.Properties["displayName"][0] : null,
                (uac & 2) != 0);
        }
        catch (Exception ex)
        {
            throw new AppException(FriendlyLdapError(ex, domain.Name), ex.ToString(), ex);
        }
    }, ct);

    /// <summary>
    /// Root targets of a domain-based namespace from CN=Dfs-Configuration,CN=System: msDFS-TargetListv2 (Windows Server 2008 mode)
    /// or remoteServerName on the fTDfs object (Windows 2000 mode). Returns FQDNs.
    /// </summary>
    public Task<List<string>> DiscoverNamespaceServersAsync(DomainRec domain, Credential cred, CancellationToken ct) => Task.Run(() =>
    {
        var hosts = new List<string>();
        var rootName = domain.DfsRoot.TrimEnd('\\').Split('\\').LastOrDefault();
        if (string.IsNullOrWhiteSpace(rootName)) return hosts;
        string dnc;
        using (var rootDse = new DirectoryEntry($"LDAP://{domain.Name}/RootDSE", cred.LogonName, cred.Password, AuthenticationTypes.Secure))
            dnc = (string)rootDse.Properties["defaultNamingContext"].Value!;
        var dn = $"CN={LdapDnEscape(rootName)},CN=Dfs-Configuration,CN=System,{dnc}";
        void AddTarget(string unc)
        {
            var h = IssueEngine.HostOfUnc(unc.Trim());
            if (string.IsNullOrEmpty(h)) return;
            if (!h.Contains('.')) h = h + "." + domain.Name;
            if (!hosts.Contains(h, StringComparer.OrdinalIgnoreCase)) hosts.Add(h);
        }
        try
        {
            using var root = new DirectoryEntry($"LDAP://{domain.Name}/{dn}", cred.LogonName, cred.Password, AuthenticationTypes.Secure);
            var classes = root.Properties["objectClass"].Cast<object>().Select(o => o.ToString() ?? "").ToList();
            if (classes.Any(c => c.Equals("fTDfs", StringComparison.OrdinalIgnoreCase)))
                foreach (var v in root.Properties["remoteServerName"]) AddTarget(v?.ToString() ?? "");
            using var ds = new DirectorySearcher(root, "(objectClass=msDFS-Namespacev2)", new[] { "msDFS-TargetListv2" }) { SearchScope = SearchScope.Subtree };
            foreach (SearchResult r in ds.FindAll())
            {
                foreach (var v in r.Properties["msDFS-TargetListv2"])
                {
                    var xml = v switch { byte[] b => DecodeXml(b), string str => str, _ => v?.ToString() ?? "" };
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml, @"\\\\[^<""\s]+"))
                        AddTarget(m.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"dfs discovery: {dn}: {ex.Message}");
        }
        return hosts;
    }, ct);

    /// <summary>Domain-based namespaces: fTDfs objects (Windows 2000 mode) and msDFS-NamespaceAnchor containers (2008 mode) directly under CN=Dfs-Configuration.</summary>
    public Task<List<string>> DiscoverNamespaceRootsAsync(DomainRec domain, Credential cred, CancellationToken ct) => Task.Run(() =>
    {
        var roots = new List<string>();
        try
        {
            string dnc;
            using (var rootDse = new DirectoryEntry($"LDAP://{domain.Name}/RootDSE", cred.LogonName, cred.Password, AuthenticationTypes.Secure))
                dnc = (string)rootDse.Properties["defaultNamingContext"].Value!;
            using var cfg = new DirectoryEntry($"LDAP://{domain.Name}/CN=Dfs-Configuration,CN=System,{dnc}", cred.LogonName, cred.Password, AuthenticationTypes.Secure);
            using var ds = new DirectorySearcher(cfg, "(|(objectClass=fTDfs)(objectClass=msDFS-NamespaceAnchor))", new[] { "cn" }) { SearchScope = SearchScope.OneLevel };
            foreach (SearchResult r in ds.FindAll())
            {
                var cn = r.Properties["cn"].Count > 0 ? r.Properties["cn"][0]?.ToString() : null;
                if (!string.IsNullOrWhiteSpace(cn) && !roots.Contains($"\\\\{domain.Name}\\{cn}", StringComparer.OrdinalIgnoreCase))
                    roots.Add($"\\\\{domain.Name}\\{cn}");
            }
        }
        catch (Exception ex)
        {
            // no CN=Dfs-Configuration (DFS never set up) or no read access: nothing to offer
            Log.Warn($"dfs discovery: namespaces of {domain.Name}: {ex.Message}");
        }
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots;
    }, ct);

    private static string DecodeXml(byte[] b)
    {
        if (b.Length >= 2 && b[1] == 0) return System.Text.Encoding.Unicode.GetString(b);
        return System.Text.Encoding.UTF8.GetString(b);
    }

    private static string LdapDnEscape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (",+\"\\<>;=".Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string FriendlyLdapError(Exception ex, string domain)
    {
        var msg = ex.Message;
        if (ex is DirectoryServicesCOMException dse)
        {
            if ((uint)dse.ErrorCode == 0x8007052E) return "The user name or password is incorrect.";
            if ((uint)dse.ErrorCode == 0x8007203A) return $"Cannot reach a domain controller for {domain} (LDAP). Check DNS and network.";
        }
        if (ex is System.Runtime.InteropServices.COMException com)
        {
            if ((uint)com.ErrorCode == 0x8007052E) return "The user name or password is incorrect.";
            if ((uint)com.ErrorCode == 0x8007203A) return $"Cannot reach a domain controller for {domain} (LDAP).";
        }
        return $"Active Directory error for {domain}: {msg}";
    }

    private static string LdapEscape(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ remote scripts

    private async Task<T> RunJsonAsync<T>(string fqdn, string script, IReadOnlyList<object?> args, Credential cred, CancellationToken ct, string what, TimeSpan? timeout = null, string? secret = null)
    {
        var result = await _runner.RunAsync(fqdn, script, args, cred, ct, timeout, secret);
        var json = result.Require($"{what} on {fqdn}");
        if (string.IsNullOrWhiteSpace(json))
            throw new AppException($"{what} on {fqdn} returned no output", result.Stderr);
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts) ?? throw new AppException($"{what} on {fqdn} returned empty JSON", json);
        }
        catch (JsonException ex)
        {
            throw new AppException($"{what} on {fqdn} returned invalid JSON: {ex.Message}", json, ex);
        }
    }

    public async Task<string> PingAsync(string computerFqdn, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<PingJson>(computerFqdn, PsScripts.Ping, Array.Empty<object?>(), cred, ct, "WinRM test", TimeSpan.FromSeconds(90));
        return $"{r.Computer} as {r.User}";
    }

    public async Task<ServerProbe> ProbeServerAsync(ServerRec server, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<ProbeJson>(server.Fqdn, PsScripts.ProbeServer, new object?[] { server.LocalRoot }, cred, ct, "Diagnostics probe", TimeSpan.FromMinutes(2));
        return new ServerProbe(r.Computer ?? server.Name, r.IsAdmin, r.FsrmInstalled, r.FsrmModule, r.RootExists,
            (r.Shares ?? new()).Where(s => s is not null).Select(s => new ShareMap(s!.Name ?? "", s.Path ?? "")).ToList(), r.Os);
    }

    public async Task<DfsHostProbe> ProbeDfsHostAsync(DomainRec domain, bool installTools, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<DfsProbeJson>(domain.DfsHostFqdn, PsScripts.ProbeDfsHost, new object?[] { domain.DfsRoot, installTools }, cred, ct, "DFS host probe", TimeSpan.FromMinutes(installTools ? 15 : 2));
        return new DfsHostProbe(r.Computer ?? domain.DfsHostFqdn, r.DfsnModule, r.RootReachable, r.Error, r.RsatInstalled);
    }

    public async Task<bool> InstallFsrmAsync(ServerRec server, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<FsrmJson>(server.Fqdn, PsScripts.FsrmStatus, new object?[] { true }, cred, ct, "FSRM installation", TimeSpan.FromMinutes(20));
        return r.FsrmInstalled;
    }

    public async Task<ScanResult> ScanFoldersAsync(ServerRec server, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<ScanJson>(server.Fqdn, PsScripts.ScanFolders, new object?[] { server.LocalRoot }, cred, ct, "Folder scan", TimeSpan.FromMinutes(15));
        var folders = (r.Folders ?? new()).Where(f => f is not null && !string.IsNullOrEmpty(f.Name)).Select(f => new ScannedFolder(
            f!.Name!, f.Path ?? "", ParseDate(f.LastWrite), f.Owner,
            (f.Aces ?? new()).Where(a => a is not null).Select(a => new Ace(a!.Id ?? "", a.Rights ?? "", a.Type ?? "", a.Inherited)).ToList(),
            f.QuotaBytes, f.UsedBytes)).ToList();
        return new ScanResult(r.Computer ?? server.Name, r.FsrmModule, folders);
    }

    public async Task<List<ScannedLink>> ScanDfsLinksAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        var rows = await RunJsonAsync<List<LinkJson?>>(domain.DfsHostFqdn, PsScripts.ListDfsLinks, new object?[] { domain.DfsRoot }, cred, ct, "DFS link scan", TimeSpan.FromMinutes(10));
        return rows.Where(l => l is not null && !string.IsNullOrEmpty(l.Path)).Select(l => new ScannedLink(
            l!.Path!, l.Name ?? l.Path!.Split('\\').Last(), l.State ?? "",
            (l.Targets ?? new()).Where(t => t is not null).Select(t => new LinkTarget(t!.Target ?? "", t.State ?? "")).ToList())).ToList();
    }

    public async Task<QuotaInfo> SetQuotaAsync(ServerRec server, string path, long bytes, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<QuotaJson>(server.Fqdn, PsScripts.SetQuota, new object?[] { path, bytes }, cred, ct, "Set quota");
        return new QuotaInfo(r.Path ?? path, r.Size, r.Usage);
    }

    public async Task<long> ComputeSizeAsync(ServerRec server, string path, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<SizeJson>(server.Fqdn, PsScripts.ComputeSize, new object?[] { path }, cred, ct, "Compute size", TimeSpan.FromMinutes(30));
        return r.UsedBytes;
    }

    public async Task CreateFolderAsync(ServerRec server, string path, string userSid, string domainAdminsSid, Credential cred, CancellationToken ct)
    {
        await RunJsonAsync<OkJson>(server.Fqdn, PsScripts.CreateFolder, new object?[] { path, userSid, domainAdminsSid }, cred, ct, "Create folder");
    }

    public async Task CreateDfsLinkAsync(DomainRec domain, string linkPath, string targetPath, Credential cred, CancellationToken ct)
    {
        await RunJsonAsync<OkJson>(domain.DfsHostFqdn, PsScripts.CreateDfsLink, new object?[] { linkPath, targetPath }, cred, ct, "Create DFS link");
    }

    // ------------------------------------------------------------------ directory (AD cmdlets on the domain controller)

    public async Task<List<OuInfo>> ListOusAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        var rows = await RunJsonAsync<List<OuJson?>>(domain.DirectoryHost, PsScripts.ListOus, Array.Empty<object?>(), cred, ct, "OU listing", TimeSpan.FromMinutes(3));
        return rows.Where(r => r is not null && !string.IsNullOrEmpty(r.Dn)).Select(r => new OuInfo(r!.Dn!, r.Name ?? "", r.Canonical ?? "", r.Kind ?? "ou")).ToList();
    }

    public async Task<List<GroupInfo>> ListGroupsAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        var rows = await RunJsonAsync<List<GroupJson?>>(domain.DirectoryHost, PsScripts.ListGroups, Array.Empty<object?>(), cred, ct, "Group listing", TimeSpan.FromMinutes(5));
        return rows.Where(r => r is not null && !string.IsNullOrEmpty(r.Dn)).Select(r => new GroupInfo(r!.Name ?? "", r.Sam ?? "", r.Dn!, r.Scope ?? "", r.Description)).ToList();
    }

    public async Task<List<DcInfo>> ListDcsAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        var rows = await RunJsonAsync<List<DcJson?>>(domain.DirectoryHost, PsScripts.ListDcs, Array.Empty<object?>(), cred, ct, "Domain controller listing", TimeSpan.FromMinutes(2));
        return rows.Where(r => r is not null && !string.IsNullOrEmpty(r.HostName)).Select(r => new DcInfo(r!.HostName!, r.Ip, r.Site, r.Gc)).ToList();
    }

    public async Task<AdUser> CreateUserAsync(DomainRec domain, AdUserSpec spec, string password, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<CreatedUserJson>(domain.DirectoryHost, PsScripts.CreateAdUser,
            new object?[] { spec.Sam, spec.Upn, spec.GivenName, spec.Surname, spec.DisplayName, spec.OuDn, spec.MustChangePassword },
            cred, ct, "Create AD account", TimeSpan.FromMinutes(3), secret: password);
        return new AdUser(r.Sam ?? spec.Sam, r.Sid ?? "", r.Dn ?? "", spec.DisplayName, false);
    }

    public async Task<GroupAddResult> AddToGroupsAsync(DomainRec domain, string samAccountName, IReadOnlyList<string> groups, Credential cred, CancellationToken ct)
    {
        var r = await RunJsonAsync<GroupAddJson>(domain.DirectoryHost, PsScripts.AddToGroups,
            new object?[] { samAccountName, JsonSerializer.Serialize(groups) }, cred, ct, "Group membership", TimeSpan.FromMinutes(3));
        return new GroupAddResult(r.Added ?? new(), r.AlreadyMember ?? new(), r.Failed ?? new());
    }

    // ------------------------------------------------------------------ Horizon

    private static (string Domain, string User, string Password) HorizonCredentials(DomainRec d, Credential cred, string? storedPassword)
    {
        if (!d.HorizonConfigured) throw new AppException("Horizon is not configured for this environment (Environment → Horizon).");
        if (d.HorizonAuth == "stored")
        {
            if (string.IsNullOrEmpty(d.HorizonUser) || string.IsNullOrEmpty(storedPassword))
                throw new AppException("Horizon uses a dedicated account but no user name / password is stored (Environment → Horizon).");
            var (u, dom) = AppService.ParseUserInput(d.HorizonUser);
            return (dom ?? (string.IsNullOrEmpty(d.HorizonDomain) ? d.Netbios : d.HorizonDomain), u, storedPassword);
        }
        return (string.IsNullOrEmpty(d.HorizonDomain) ? cred.Netbios : d.HorizonDomain, cred.User, cred.Password);
    }

    public async Task<HorizonTestResult> HorizonPoolsAsync(DomainRec domain, Credential cred, string? horizonPassword, CancellationToken ct)
    {
        var (dom, user, pass) = HorizonCredentials(domain, cred, horizonPassword);
        using var hz = new HorizonClient(domain.HorizonUrl, domain.HorizonIgnoreCert);
        await hz.LoginAsync(dom, user, pass, ct);
        try
        {
            var pools = await hz.PoolsAsync(ct);
            List<HorizonClient.EntitlementDto> ents;
            try { ents = await hz.PoolEntitlementsAsync(ct); }
            catch (AppException ex) { Log.Warn("horizon: entitlements unavailable: " + ex.Message); ents = new(); }
            var names = new Dictionary<string, HorizonClient.AdObjectDto?>();
            var list = new List<HorizonPool>();
            foreach (var p in pools)
            {
                var ent = ents.FirstOrDefault(e => e.Id == p.Id);
                var groups = new List<string>();
                var users = 0;
                foreach (var id in ent?.AdUserOrGroupIds ?? new())
                {
                    if (!names.TryGetValue(id, out var obj)) names[id] = obj = await hz.AdObjectAsync(id, ct);
                    if (obj is null) continue;
                    if (obj.Group) groups.Add(string.IsNullOrEmpty(obj.Domain) ? obj.Name ?? id : obj.Domain + "\\" + obj.Name);
                    else users++;
                }
                list.Add(new HorizonPool(p.Id, p.Name, p.DisplayName, p.Type, p.Enabled, groups, users));
            }
            return new HorizonTestResult(true, $"Connected to {hz.BaseUrl}: {list.Count} desktop pool(s)", list);
        }
        finally { await hz.LogoutAsync(); }
    }

    public async Task HorizonEntitleUserAsync(DomainRec domain, string poolId, string userSid, string samAccountName, Credential cred, string? horizonPassword, CancellationToken ct)
    {
        var (dom, user, pass) = HorizonCredentials(domain, cred, horizonPassword);
        using var hz = new HorizonClient(domain.HorizonUrl, domain.HorizonIgnoreCert);
        await hz.LoginAsync(dom, user, pass, ct);
        try
        {
            var obj = await hz.FindAdUserAsync(userSid, samAccountName, domain.Netbios, ct)
                      ?? throw new AppException($"Horizon does not know the account {samAccountName} yet (directory sync). Retry in a minute, or entitle the account through an AD group.");
            await hz.EntitleAsync(poolId, obj.Id, ct);
        }
        finally { await hz.LogoutAsync(); }
    }

    private static DateTime? ParseDate(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null
        : DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // ------------------------------------------------------------------ JSON shapes emitted by PsScripts

    private sealed class PingJson { public string? Computer { get; set; } public string? User { get; set; } }
    private sealed class ShareJson { public string? Name { get; set; } public string? Path { get; set; } }
    private sealed class ProbeJson
    {
        public string? Computer { get; set; } public bool IsAdmin { get; set; } public bool? FsrmInstalled { get; set; }
        public bool FsrmModule { get; set; } public bool RootExists { get; set; } public List<ShareJson?>? Shares { get; set; } public string? Os { get; set; }
    }
    private sealed class DfsProbeJson
    {
        public string? Computer { get; set; } public bool DfsnModule { get; set; } public bool RootReachable { get; set; }
        public string? Error { get; set; } public bool? RsatInstalled { get; set; }
    }
    private sealed class FsrmJson { public bool FsrmInstalled { get; set; } public bool DfsnModule { get; set; } public string? Computer { get; set; } }
    private sealed class AceJson { public string? Id { get; set; } public string? Rights { get; set; } public string? Type { get; set; } public bool Inherited { get; set; } }
    private sealed class FolderJson
    {
        public string? Name { get; set; } public string? Path { get; set; } public string? LastWrite { get; set; } public string? Owner { get; set; }
        public List<AceJson?>? Aces { get; set; } public long? QuotaBytes { get; set; } public long? UsedBytes { get; set; }
    }
    private sealed class ScanJson { public string? Computer { get; set; } public bool FsrmModule { get; set; } public List<FolderJson?>? Folders { get; set; } }
    private sealed class TargetJson { public string? Target { get; set; } public string? State { get; set; } }
    private sealed class LinkJson { public string? Path { get; set; } public string? Name { get; set; } public string? State { get; set; } public List<TargetJson?>? Targets { get; set; } }
    private sealed class QuotaJson { public string? Path { get; set; } public long Size { get; set; } public long Usage { get; set; } }
    private sealed class SizeJson { public string? Path { get; set; } public long UsedBytes { get; set; } }
    private sealed class OkJson { public bool Ok { get; set; } }
    private sealed class OuJson { public string? Dn { get; set; } public string? Name { get; set; } public string? Canonical { get; set; } public string? Kind { get; set; } }
    private sealed class GroupJson { public string? Name { get; set; } public string? Sam { get; set; } public string? Dn { get; set; } public string? Scope { get; set; } public string? Description { get; set; } }
    private sealed class DcJson { public string? HostName { get; set; } public string? Ip { get; set; } public string? Site { get; set; } public bool Gc { get; set; } }
    private sealed class CreatedUserJson { public string? Sid { get; set; } public string? Dn { get; set; } public string? Sam { get; set; } public string? Upn { get; set; } }
    private sealed class GroupAddJson { public List<string>? Added { get; set; } public List<string>? AlreadyMember { get; set; } public List<string>? Failed { get; set; } }
}
