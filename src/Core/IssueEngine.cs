using System.Text.Json;

namespace ZweDesk.Core;

/// <summary>Turns scan results into folder rows (user rights / extra access) and the domain-wide issue list.</summary>
public static class IssueEngine
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsUserFolder(string name) => !name.StartsWith('_');

    // ------------------------------------------------------------------ per-folder analysis

    public static FolderRec BuildFolder(long serverId, ScannedFolder f, string netbios, string domainName)
    {
        var (userRights, extra) = AnalyseAcl(f.Name, f.Aces, netbios, domainName);
        return new FolderRec(0, serverId, f.Name, Db.Key(f.Name), f.Path, f.Owner, userRights,
            JsonSerializer.Serialize(extra, Json), JsonSerializer.Serialize(f.Aces, Json),
            f.QuotaBytes, f.UsedBytes, f.LastWriteUtc, DateTime.UtcNow);
    }

    /// <summary>Summarises the folder's ACL: what the user itself has, and every other identity that is not part of the standard pattern.</summary>
    public static (string UserRights, List<Ace> Extra) AnalyseAcl(string folderName, IReadOnlyList<Ace> aces, string netbios, string domainName)
    {
        var nb = string.IsNullOrEmpty(netbios) ? domainName.Split('.')[0] : netbios;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NT AUTHORITY\\SYSTEM", "BUILTIN\\Administrators", "CREATOR OWNER",
            nb + "\\Domain Admins", nb + "\\Enterprise Admins",
            nb + "\\" + folderName, folderName + "@" + domainName,
        };
        var userRights = "None";
        var rank = 0;
        var extra = new List<Ace>();
        foreach (var a in aces)
        {
            var isUser = a.Id.Equals(nb + "\\" + folderName, StringComparison.OrdinalIgnoreCase) ||
                         a.Id.Equals(folderName + "@" + domainName, StringComparison.OrdinalIgnoreCase);
            var isAllow = a.Type.Equals("Allow", StringComparison.OrdinalIgnoreCase);
            if (isUser && isAllow)
            {
                var (label, r) = Summarise(a.Rights);
                if (r > rank) { rank = r; userRights = label; }
                continue;
            }
            if (!allowed.Contains(a.Id) && isAllow) extra.Add(a);
        }
        return (userRights, extra);
    }

    /// <summary>Maps a FileSystemRights string (or raw number for generic rights) to Full / Modify / Write / Read.</summary>
    public static (string Label, int Rank) Summarise(string rights)
    {
        if (rights.Contains("FullControl", StringComparison.OrdinalIgnoreCase)) return ("Full", 4);
        if (long.TryParse(rights, out var n))
        {
            if ((n & 0x10000000) != 0 || n == -1) return ("Full", 4);            // GENERIC_ALL
            if ((n & 0x40000000) != 0) return ("Write", 2);                      // GENERIC_WRITE
            if ((n & unchecked((long)0x80000000)) != 0) return ("Read", 1);      // GENERIC_READ
            return (rights, 0);
        }
        if (rights.Contains("Modify", StringComparison.OrdinalIgnoreCase)) return ("Modify", 3);
        if (rights.Contains("Write", StringComparison.OrdinalIgnoreCase)) return ("Write", 2);
        if (rights.Contains("Read", StringComparison.OrdinalIgnoreCase) || rights.Contains("ListDirectory", StringComparison.OrdinalIgnoreCase)) return ("Read", 1);
        return (rights, 0);
    }

    // ------------------------------------------------------------------ domain-wide issues

    public sealed record LinkView(string Name, string LinkPath, string? State, List<LinkTarget> Targets, List<string> TargetHosts);

    public static List<LinkTarget> ParseTargets(string json)
    {
        try { return JsonSerializer.Deserialize<List<LinkTarget>>(json, Json) ?? new(); }
        catch { return new(); }
    }

    public static string? HostOfUnc(string unc)
    {
        if (string.IsNullOrEmpty(unc) || !unc.StartsWith("\\\\")) return null;
        var rest = unc[2..];
        var i = rest.IndexOf('\\');
        return i < 0 ? rest : rest[..i];
    }

    public static bool SameHost(string? host, ServerRec s)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var h = host.Split('.')[0];
        return host.Equals(s.Fqdn, StringComparison.OrdinalIgnoreCase) || host.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ||
               h.Equals(s.Name, StringComparison.OrdinalIgnoreCase) || h.Equals(s.Fqdn.Split('.')[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file server whose folder inventory can be trusted: scanned at least once and the last contact did not fail.</summary>
    public static bool IsInventoried(ServerRec s) => s.LastScanAt.HasValue && s.LastError is null;

    public static void Recompute(Db db, DomainRec domain)
    {
        var servers = db.ListServers(domain.Id).ToDictionary(s => s.Id);
        var folders = db.ListFolders(domain.Id).Where(f => IsUserFolder(f.Name) && servers.ContainsKey(f.ServerId)).ToList();
        var links = db.ListLinks(domain.Id).ToDictionary(l => l.NameKey, l => l);
        var issues = new List<IssueRec>();
        var now = DateTime.UtcNow;
        // Cross-checks between folders and links are only meaningful when both sides were actually read: a file server that
        // could not be scanned makes every "no folder" verdict void, and a failed link scan makes every "no link" verdict void.
        var foldersComplete = servers.Values.Where(s => s.Enabled).All(IsInventoried);
        var linksComplete = db.DfsScanOk(domain.Id);

        void Add(string kind, string nameKey, object details) =>
            issues.Add(new IssueRec(0, domain.Id, kind, nameKey, JsonSerializer.Serialize(details, Json), now));

        var groups = folders.GroupBy(f => f.NameKey).ToList();
        foreach (var g in groups)
        {
            var list = g.ToList();
            var name = list[0].Name;
            links.TryGetValue(g.Key, out var link);
            var targets = link is null ? new List<LinkTarget>() : ParseTargets(link.TargetsJson);

            if (list.Count > 1)
            {
                Add(IssueKinds.DuplicateFolder, g.Key, new
                {
                    name,
                    copies = list.Select(f => new { server = servers[f.ServerId].Name, serverId = f.ServerId, folderId = f.Id, path = f.Path, lastWriteUtc = f.LastWriteUtc, usedBytes = f.UsedBytes, quotaBytes = f.QuotaBytes }),
                    link = link?.LinkPath,
                    targets = targets.Select(t => t.Target),
                });
            }

            if (link is null)
            {
                if (linksComplete)
                    foreach (var f in list)
                        Add(IssueKinds.FolderWithoutLink, g.Key, new { name, server = servers[f.ServerId].Name, serverId = f.ServerId, folderId = f.Id, path = f.Path, duplicate = list.Count > 1 });
            }
            else if (foldersComplete)
            {
                // every target must point at a server that actually holds the folder
                var bad = targets.Where(t => !list.Any(f => SameHost(HostOfUnc(t.Target), servers[f.ServerId]))).ToList();
                if (bad.Count > 0 || targets.Count == 0)
                    Add(IssueKinds.LinkTargetMismatch, g.Key, new
                    {
                        name,
                        linkPath = link.LinkPath,
                        targets = targets.Select(t => t.Target),
                        badTargets = bad.Select(t => t.Target),
                        foldersOn = list.Select(f => servers[f.ServerId].Name),
                    });
            }

            foreach (var f in list)
            {
                if (f.QuotaBytes is null)
                    Add(IssueKinds.NoQuota, g.Key + "@" + servers[f.ServerId].Name.ToLowerInvariant(), new { name, server = servers[f.ServerId].Name, serverId = f.ServerId, folderId = f.Id, path = f.Path, usedBytes = f.UsedBytes });
                var extra = ParseAces(f.ExtraAccessJson);
                if (extra.Count > 0)
                    Add(IssueKinds.ExtraAccess, g.Key + "@" + servers[f.ServerId].Name.ToLowerInvariant(), new { name, server = servers[f.ServerId].Name, serverId = f.ServerId, folderId = f.Id, path = f.Path, extra });
            }
        }

        var folderKeys = new HashSet<string>(groups.Select(g => g.Key));
        foreach (var (key, link) in links)
        {
            if (folderKeys.Contains(key)) continue;
            // judged against the link's own targets: reported when every target is a configured, inventoried file server
            // and none of them has the folder; links to servers not managed here are left alone
            var targets = ParseTargets(link.TargetsJson);
            var hosts = targets.Select(t => HostOfUnc(t.Target)).Where(h => h is not null).ToList();
            var targetServers = servers.Values.Where(s => hosts.Any(h => SameHost(h, s))).ToList();
            var checkable = hosts.Count == 0 ? foldersComplete : targetServers.Count > 0 && targetServers.All(IsInventoried);
            if (!checkable) continue;
            Add(IssueKinds.LinkWithoutFolder, key, new { name = link.Name, linkPath = link.LinkPath, targets = targets.Select(t => t.Target), state = link.State });
        }

        db.ReplaceIssues(domain.Id, issues);
    }

    public static List<Ace> ParseAces(string json)
    {
        try { return JsonSerializer.Deserialize<List<Ace>>(json, Json) ?? new(); }
        catch { return new(); }
    }
}
