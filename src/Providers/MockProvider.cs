using System.Globalization;
using System.Text.RegularExpressions;
using ZweDesk.Core;

namespace ZweDesk.Providers;

/// <summary>
/// Offline provider fed by fixtures/userdata_listing.txt (a synthetic listing of two demo file servers, FS1 + FS2).
/// Quota / usage / ACL / DFS data is synthesized deterministically so every screen and every issue kind can be exercised.
/// Mutations are kept in memory for the life of the process.
/// </summary>
public sealed class MockProvider : IServerProvider
{
    private const long GB = 1024L * 1024 * 1024;
    private const string DomainSid = "S-1-5-21-1000000001-2000000002-3000000003";

    private readonly object _gate = new();
    // server name (upper) -> folder name key -> folder
    private readonly Dictionary<string, Dictionary<string, ScannedFolder>> _folders = new(StringComparer.OrdinalIgnoreCase);
    // link name key -> link
    private readonly Dictionary<string, ScannedLink> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _fsrmInstalled = new(StringComparer.OrdinalIgnoreCase);
    private bool _dfsTools = true;
    private bool _seeded;
    // accounts created through the wizard (sam -> user) and their group memberships / pool entitlements
    private readonly Dictionary<string, AdUser> _createdUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _memberships = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _poolUsers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<OuInfo> Ous = new()
    {
        new("DC=demo,DC=local", "demo.local", "demo.local/", "domain"),
        new("CN=Users,DC=demo,DC=local", "Users", "demo.local/Users", "container"),
        new("OU=VDI Users,DC=demo,DC=local", "VDI Users", "demo.local/VDI Users", "ou"),
        new("OU=Sales,OU=VDI Users,DC=demo,DC=local", "Sales", "demo.local/VDI Users/Sales", "ou"),
        new("OU=Engineering,OU=VDI Users,DC=demo,DC=local", "Engineering", "demo.local/VDI Users/Engineering", "ou"),
        new("OU=Kiosk,OU=VDI Users,DC=demo,DC=local", "Kiosk", "demo.local/VDI Users/Kiosk", "ou"),
        new("OU=Staff,DC=demo,DC=local", "Staff", "demo.local/Staff", "ou"),
        new("OU=Service Accounts,DC=demo,DC=local", "Service Accounts", "demo.local/Service Accounts", "ou"),
    };

    private static readonly List<GroupInfo> Groups = new()
    {
        new("VDI-Users", "VDI-Users", "CN=VDI-Users,OU=Groups,DC=demo,DC=local", "Global", "All VDI users (Horizon entitlement)"),
        new("VDI-Sales", "VDI-Sales", "CN=VDI-Sales,OU=Groups,DC=demo,DC=local", "Global", "Sales desktop pool"),
        new("VDI-Engineering", "VDI-Engineering", "CN=VDI-Engineering,OU=Groups,DC=demo,DC=local", "Global", "Engineering desktop pool"),
        new("VDI-Kiosk", "VDI-Kiosk", "CN=VDI-Kiosk,OU=Groups,DC=demo,DC=local", "Global", "Kiosk desktops"),
        new("Internet-Users", "Internet-Users", "CN=Internet-Users,OU=Groups,DC=demo,DC=local", "Global", "Proxy access"),
        new("Printer-Users", "Printer-Users", "CN=Printer-Users,OU=Groups,DC=demo,DC=local", "DomainLocal", null),
        new("Domain Users", "Domain Users", "CN=Domain Users,CN=Users,DC=demo,DC=local", "Global", "All domain users"),
        new("Domain Admins", "Domain Admins", "CN=Domain Admins,CN=Users,DC=demo,DC=local", "Global", "Designated administrators of the domain"),
    };

    private static readonly List<HorizonPool> Pools = new()
    {
        new("d7f1a2b3-sales", "Sales-Win11", "Sales desktops", "AUTOMATED", true, new List<string> { "DEMO\\VDI-Sales" }, 0),
        new("e8a2b3c4-eng", "Engineering", "Engineering workstations", "AUTOMATED", true, new List<string> { "DEMO\\VDI-Engineering", "DEMO\\VDI-Users" }, 2),
        new("f9b3c4d5-kiosk", "Kiosk", "Kiosk (floating)", "AUTOMATED", true, new List<string> { "DEMO\\VDI-Kiosk" }, 0),
        new("a1c4d5e6-legacy", "Legacy-Win10", "Old pool", "MANUAL", false, new List<string>(), 3),
    };

    public string Mode => "mock";

    /// <summary>First start of the demo database: the fictional environment the fixture describes (nothing of this exists anywhere).</summary>
    public static void SeedDemoEnvironment(Db db)
    {
        var id = db.SaveEnvironment(new DomainRec(0, "demo.local", "DEMO", "\\\\demo.local\\Users", "dc1.demo.local", DateTime.UtcNow,
            "dc1.demo.local", "", "none", "", "", false, "group", "", "[]", ""));
        db.SaveServer(null, id, "FS1", "fs1.demo.local", "D:\\Users", "Users$", true);
        db.SaveServer(null, id, "FS2", "fs2.demo.local", "D:\\Users", null, true);
        Log.Info("demo database seeded: demo.local with FS1 + FS2");
    }

    // ------------------------------------------------------------------ fixture

    private static readonly Regex HeaderRx = new(@"^(?<server>[A-Za-z0-9_-]+)\s*\(.*\):\s*(?<root>.+)$", RegexOptions.Compiled);
    private static readonly Regex LineRx = new(@"^(?<date>\d{1,2}/\d{1,2}/\d{4} \d{1,2}:\d{2}:\d{2} [AP]M)\s+(?<name>\S.*)$", RegexOptions.Compiled);

    public static Dictionary<string, List<(string Name, DateTime LastWrite)>> ParseListing(string text)
    {
        var result = new Dictionary<string, List<(string, DateTime)>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('=')) continue;
            var h = HeaderRx.Match(line);
            if (h.Success && !LineRx.IsMatch(line))
            {
                current = h.Groups["server"].Value;
                if (!result.ContainsKey(current)) result[current] = new();
                continue;
            }
            var m = LineRx.Match(line);
            if (m.Success && current is not null)
            {
                var dt = DateTime.ParseExact(m.Groups["date"].Value, "M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                result[current].Add((m.Groups["name"].Value.Trim(), dt));
            }
        }
        return result;
    }

    private static string LoadFixture()
    {
        using var s = typeof(MockProvider).Assembly.GetManifestResourceStream("fixtures/userdata_listing.txt")
                      ?? throw new InvalidOperationException("embedded fixture fixtures/userdata_listing.txt is missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static int Hash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var ch in s.ToLowerInvariant()) h = h * 31 + ch;
            return h & 0x7fffffff;
        }
    }

    private void EnsureSeeded(IEnumerable<ServerRec> servers)
    {
        lock (_gate)
        {
            if (_seeded) return;
            _seeded = true;
            var listing = ParseListing(LoadFixture());
            var byName = servers.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

            foreach (var (serverName, rows) in listing)
            {
                var srv = byName.GetValueOrDefault(serverName);
                var root = srv?.LocalRoot ?? "D:\\Users";
                var dict = new Dictionary<string, ScannedFolder>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, lastWrite) in rows)
                {
                    var h = Hash(serverName + "/" + name);
                    long? quota = h % 10 < 7 ? (h % 3 == 0 ? 10 * GB : 5 * GB) : null;
                    long? used = quota.HasValue ? (long)(quota.Value * ((h % 111) + 3) / 100.0) : null;
                    var aces = StandardAces(name);
                    if (h % 13 == 0) aces.Add(new Ace("DEMO\\Domain Users", "ReadAndExecute, Synchronize", "Allow", false));
                    if (h % 29 == 0) aces.Add(new Ace("S-1-5-21-4444444444-1234567890-987654321-1105", "Modify, Synchronize", "Allow", false));
                    dict[name] = new ScannedFolder(name, Path.Combine(root, name), lastWrite, name.StartsWith('_') ? "BUILTIN\\Administrators" : "DEMO\\" + name, aces, quota, used);
                }
                _folders[serverName] = dict;
                _fsrmInstalled[serverName] = !serverName.Equals("FS2", StringComparison.OrdinalIgnoreCase);
            }

            // DFS links: one per single-server user folder (a few deliberately missing), duplicates point to FS1 only,
            // one link points to the wrong server, two links have no folder at all.
            var all = _folders.SelectMany(kv => kv.Value.Values.Where(f => !f.Name.StartsWith('_')).Select(f => (Server: kv.Key, Folder: f))).ToList();
            var groups = all.GroupBy(x => x.Folder.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var first = g.OrderBy(x => x.Server, StringComparer.OrdinalIgnoreCase).First();
                var h = Hash("link/" + g.Key);
                if (g.Count() == 1 && h % 9 == 0) continue; // FOLDER_WITHOUT_LINK
                var target = first.Server;
                if (g.Key.Equals("u1024", StringComparison.OrdinalIgnoreCase)) target = "FS2"; // LINK_TARGET_MISMATCH
                var fqdn = byName.GetValueOrDefault(target)?.Fqdn ?? target + ".demo.local";
                _links[g.Key] = new ScannedLink($"\\\\demo.local\\Users\\{g.Key}", g.Key, "Online",
                    new List<LinkTarget> { new($"\\\\{fqdn}\\Users$\\{g.Key}", "Online") });
            }
            foreach (var dead in new[] { "u9001", "u9002" })
                _links[dead] = new ScannedLink($"\\\\demo.local\\Users\\{dead}", dead, "Online",
                    new List<LinkTarget> { new($"\\\\fs1.demo.local\\Users$\\{dead}", "Online") });
        }
    }

    private static List<Ace> StandardAces(string user) => new()
    {
        new Ace("NT AUTHORITY\\SYSTEM", "FullControl", "Allow", false),
        new Ace("BUILTIN\\Administrators", "FullControl", "Allow", false),
        new Ace("DEMO\\Domain Admins", "FullControl", "Allow", false),
        new Ace("DEMO\\" + user, "Modify, Synchronize", "Allow", false),
    };

    private static Task Delay(int ms, CancellationToken ct) => Task.Delay(ms, ct);

    // ------------------------------------------------------------------ IServerProvider

    public async Task<AdInfo> ValidateCredentialsAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        await Delay(400, ct);
        if (string.IsNullOrWhiteSpace(cred.User) || string.IsNullOrWhiteSpace(cred.Password))
            throw new AppException("The user name or password is incorrect.");
        if (cred.Password == "wrong") throw new AppException("The user name or password is incorrect.");
        return new AdInfo(DomainSid, string.IsNullOrEmpty(domain.Netbios) ? "DEMO" : domain.Netbios, DomainSid + "-512", "dc1.demo.local");
    }

    public async Task<AdUser?> FindUserAsync(DomainRec domain, string samAccountName, Credential cred, CancellationToken ct)
    {
        await Delay(250, ct);
        lock (_gate) if (_createdUsers.TryGetValue(samAccountName, out var created)) return created;
        if (samAccountName.StartsWith("x", StringComparison.OrdinalIgnoreCase)) return null;
        if (samAccountName.StartsWith("new", StringComparison.OrdinalIgnoreCase)) return null;  // convenient "does not exist yet" names for the wizard demo
        var rid = 1100 + Hash(samAccountName) % 9000;
        return new AdUser(samAccountName, $"{DomainSid}-{rid}", $"CN={samAccountName},OU=VDI Users,DC=demo,DC=local", "User " + samAccountName.ToUpperInvariant(),
            samAccountName.EndsWith("0", StringComparison.Ordinal) && Hash(samAccountName) % 5 == 0);
    }

    public async Task<List<OuInfo>> ListOusAsync(DomainRec domain, Credential cred, CancellationToken ct) { await Delay(300, ct); return Ous.ToList(); }

    public async Task<List<GroupInfo>> ListGroupsAsync(DomainRec domain, Credential cred, CancellationToken ct) { await Delay(300, ct); return Groups.ToList(); }

    public async Task<List<string>> DiscoverNamespaceServersAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        await Delay(200, ct);
        return string.IsNullOrEmpty(domain.DfsRoot) ? new List<string>() : new List<string> { "fs1.demo.local", "fs2.demo.local" };
    }

    public async Task<List<string>> DiscoverNamespaceRootsAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        await Delay(200, ct);
        return new List<string> { $"\\\\{domain.Name}\\Users", $"\\\\{domain.Name}\\Public" };
    }

    public async Task<List<DcInfo>> ListDcsAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        await Delay(200, ct);
        return new List<DcInfo> { new("dc1.demo.local", "10.10.0.1", "Default-First-Site-Name", true), new("dc2.demo.local", "10.10.0.2", "Default-First-Site-Name", true) };
    }

    public async Task<AdUser> CreateUserAsync(DomainRec domain, AdUserSpec spec, string password, Credential cred, CancellationToken ct)
    {
        await Delay(700, ct);
        if (password.Length < 8 || password.Equals("weak", StringComparison.OrdinalIgnoreCase) || password.ToLowerInvariant() == password)
            throw new AppException("Create AD account on DC1.demo.local failed: The password does not meet the length, complexity, or history requirements of the domain.");
        if (!Ous.Any(o => o.Dn.Equals(spec.OuDn, StringComparison.OrdinalIgnoreCase)))
            throw new AppException($"Create AD account failed: Directory object not found: {spec.OuDn}");
        lock (_gate)
        {
            if (_createdUsers.ContainsKey(spec.Sam)) throw new AppException($"Create AD account failed: AD account already exists: {spec.Sam}");
            var u = new AdUser(spec.Sam, $"{DomainSid}-{2000 + _createdUsers.Count}", $"CN={spec.DisplayName},{spec.OuDn}", spec.DisplayName, false);
            _createdUsers[spec.Sam] = u;
            _memberships[spec.Sam] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN=Domain Users,CN=Users,DC=demo,DC=local" };
            return u;
        }
    }

    public async Task<GroupAddResult> AddToGroupsAsync(DomainRec domain, string samAccountName, IReadOnlyList<string> groups, Credential cred, CancellationToken ct)
    {
        await Delay(400, ct);
        var added = new List<string>(); var already = new List<string>(); var failed = new List<string>();
        lock (_gate)
        {
            if (!_memberships.TryGetValue(samAccountName, out var set)) _memberships[samAccountName] = set = new(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                var info = Groups.FirstOrDefault(x => x.Dn.Equals(g, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(g, StringComparison.OrdinalIgnoreCase) || x.Sam.Equals(g, StringComparison.OrdinalIgnoreCase));
                if (info is null) { failed.Add(g + ": Cannot find an object with identity: '" + g + "'"); continue; }
                if (!set.Add(info.Dn)) already.Add(info.Name); else added.Add(info.Name);
            }
        }
        return new GroupAddResult(added, already, failed);
    }

    public async Task<HorizonTestResult> HorizonPoolsAsync(DomainRec domain, Credential cred, string? horizonPassword, CancellationToken ct)
    {
        await Delay(900, ct);
        if (!domain.HorizonConfigured) throw new AppException("Horizon is not configured for this environment (Environment → Horizon).");
        if (domain.HorizonUrl.Contains("bad", StringComparison.OrdinalIgnoreCase))
            throw new AppException($"Cannot reach Horizon at https://{domain.HorizonUrl}/rest: No such host is known.");
        lock (_gate)
            return new HorizonTestResult(true, $"Connected to {(domain.HorizonUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "" : "https://")}{domain.HorizonUrl.TrimEnd('/')}/rest: {Pools.Count} desktop pool(s) (mock)",
                Pools.Select(p => p with { EntitledUsers = p.EntitledUsers + (_poolUsers.TryGetValue(p.Id, out var set) ? set.Count : 0) }).ToList());
    }

    public async Task HorizonEntitleUserAsync(DomainRec domain, string poolId, string userSid, string samAccountName, Credential cred, string? horizonPassword, CancellationToken ct)
    {
        await Delay(600, ct);
        if (!Pools.Any(p => p.Id == poolId)) throw new AppException("Horizon refused the entitlement: desktop pool not found: " + poolId);
        lock (_gate)
        {
            if (!_poolUsers.TryGetValue(poolId, out var set)) _poolUsers[poolId] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(samAccountName);
        }
    }

    public async Task<string> PingAsync(string computerFqdn, Credential cred, CancellationToken ct)
    {
        await Delay(300, ct);
        return computerFqdn.Split('.')[0].ToUpperInvariant() + " as DEMO\\" + cred.User;
    }

    public async Task<ServerProbe> ProbeServerAsync(ServerRec server, Credential cred, CancellationToken ct)
    {
        EnsureSeeded(new[] { server });
        await Delay(600, ct);
        bool fsrm;
        lock (_gate) fsrm = _fsrmInstalled.GetValueOrDefault(server.Name, true);
        return new ServerProbe(server.Name.ToUpperInvariant(), true, fsrm, fsrm, true,
            new List<ShareMap> { new("Users$", server.LocalRoot), new("Software", "D:\\Software") }, "Microsoft Windows Server 2022 Standard (mock)");
    }

    public async Task<DfsHostProbe> ProbeDfsHostAsync(DomainRec domain, bool installTools, Credential cred, CancellationToken ct)
    {
        await Delay(installTools ? 1500 : 500, ct);
        if (installTools) _dfsTools = true;
        return new DfsHostProbe(domain.DfsHostFqdn.Split('.')[0].ToUpperInvariant(), _dfsTools, _dfsTools, _dfsTools ? null : "PowerShell module DFSN is not available on this host", _dfsTools);
    }

    public async Task<bool> InstallFsrmAsync(ServerRec server, Credential cred, CancellationToken ct)
    {
        await Delay(2000, ct);
        lock (_gate) _fsrmInstalled[server.Name] = true;
        return true;
    }

    public async Task<ScanResult> ScanFoldersAsync(ServerRec server, Credential cred, CancellationToken ct)
    {
        EnsureSeeded(new[] { server });
        await Delay(700 + Hash(server.Name) % 900, ct);
        lock (_gate)
        {
            var fsrm = _fsrmInstalled.GetValueOrDefault(server.Name, true);
            var dict = _folders.GetValueOrDefault(server.Name);
            var list = dict?.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                           .Select(f => fsrm ? f : f with { QuotaBytes = null, UsedBytes = null }).ToList() ?? new List<ScannedFolder>();
            return new ScanResult(server.Name.ToUpperInvariant(), fsrm, list);
        }
    }

    public async Task<List<ScannedLink>> ScanDfsLinksAsync(DomainRec domain, Credential cred, CancellationToken ct)
    {
        await Delay(500, ct);
        lock (_gate) return _links.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<QuotaInfo> SetQuotaAsync(ServerRec server, string path, long bytes, Credential cred, CancellationToken ct)
    {
        await Delay(500, ct);
        lock (_gate)
        {
            if (!_fsrmInstalled.GetValueOrDefault(server.Name, true))
                throw new AppException($"Set quota on {server.Fqdn} failed: The term 'Get-FsrmQuota' is not recognized (FSRM is not installed).");
            var (dict, key) = Locate(server, path);
            var f = dict[key];
            var used = f.UsedBytes ?? (long)(bytes * 0.12);
            dict[key] = f with { QuotaBytes = bytes, UsedBytes = used };
            return new QuotaInfo(path, bytes, used);
        }
    }

    public async Task<long> ComputeSizeAsync(ServerRec server, string path, Credential cred, CancellationToken ct)
    {
        await Delay(900, ct);
        lock (_gate)
        {
            var (dict, key) = Locate(server, path);
            var f = dict[key];
            var used = f.UsedBytes ?? (long)(GB * (0.2 + Hash(path) % 700 / 100.0));
            dict[key] = f with { UsedBytes = used };
            return used;
        }
    }

    public async Task CreateFolderAsync(ServerRec server, string path, string userSid, string domainAdminsSid, Credential cred, CancellationToken ct)
    {
        EnsureSeeded(new[] { server });
        await Delay(500, ct);
        lock (_gate)
        {
            var dict = _folders.GetValueOrDefault(server.Name);
            if (dict is null) _folders[server.Name] = dict = new(StringComparer.OrdinalIgnoreCase);
            var name = Path.GetFileName(path);
            if (dict.ContainsKey(name)) throw new AppException($"Create folder on {server.Fqdn} failed: Folder already exists: {path}");
            dict[name] = new ScannedFolder(name, path, DateTime.UtcNow, "DEMO\\" + name, StandardAces(name), null, 0);
        }
    }

    public async Task CreateDfsLinkAsync(DomainRec domain, string linkPath, string targetPath, Credential cred, CancellationToken ct)
    {
        await Delay(400, ct);
        var name = linkPath.Split('\\').Last();
        lock (_gate)
        {
            if (_links.ContainsKey(name)) throw new AppException($"Create DFS link on {domain.DfsHostFqdn} failed: DFS link already exists: {linkPath}");
            _links[name] = new ScannedLink(linkPath, name, "Online", new List<LinkTarget> { new(targetPath, "Online") });
        }
    }

    private (Dictionary<string, ScannedFolder> Dict, string Key) Locate(ServerRec server, string path)
    {
        var dict = _folders.GetValueOrDefault(server.Name) ?? throw new AppException($"Path not found: {path}");
        var name = Path.GetFileName(path);
        if (!dict.ContainsKey(name)) throw new AppException($"Path not found: {path}");
        return (dict, name);
    }
}
