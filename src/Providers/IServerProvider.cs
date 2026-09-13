using ZweDesk.Core;

namespace ZweDesk.Providers;

/// <summary>
/// Everything the application needs from the infrastructure (AD, file servers, DFS namespace, Horizon).
/// RealProvider talks to the servers; MockProvider answers from the fixture data so the whole UI can run offline.
/// </summary>
public interface IServerProvider
{
    string Mode { get; }

    // ---- Active Directory
    /// <summary>Validates the credential against the domain and returns domain SID / NetBIOS / Domain Admins SID.</summary>
    Task<AdInfo> ValidateCredentialsAsync(DomainRec domain, Credential cred, CancellationToken ct);
    Task<AdUser?> FindUserAsync(DomainRec domain, string samAccountName, Credential cred, CancellationToken ct);
    Task<List<OuInfo>> ListOusAsync(DomainRec domain, Credential cred, CancellationToken ct);
    Task<List<GroupInfo>> ListGroupsAsync(DomainRec domain, Credential cred, CancellationToken ct);
    Task<List<DcInfo>> ListDcsAsync(DomainRec domain, Credential cred, CancellationToken ct);
    /// <summary>Namespace servers (root targets) of the environment's domain-based DFS root, read from AD (no DFSN module needed).</summary>
    Task<List<string>> DiscoverNamespaceServersAsync(DomainRec domain, Credential cred, CancellationToken ct);
    /// <summary>Domain-based DFS namespaces of the domain as UNC roots (\\domain\name), read from CN=Dfs-Configuration in AD.</summary>
    Task<List<string>> DiscoverNamespaceRootsAsync(DomainRec domain, Credential cred, CancellationToken ct);
    Task<AdUser> CreateUserAsync(DomainRec domain, AdUserSpec spec, string password, Credential cred, CancellationToken ct);
    Task<GroupAddResult> AddToGroupsAsync(DomainRec domain, string samAccountName, IReadOnlyList<string> groups, Credential cred, CancellationToken ct);

    // ---- file servers / DFS
    Task<string> PingAsync(string computerFqdn, Credential cred, CancellationToken ct);
    Task<ServerProbe> ProbeServerAsync(ServerRec server, Credential cred, CancellationToken ct);
    Task<DfsHostProbe> ProbeDfsHostAsync(DomainRec domain, bool installTools, Credential cred, CancellationToken ct);
    Task<bool> InstallFsrmAsync(ServerRec server, Credential cred, CancellationToken ct);
    Task<ScanResult> ScanFoldersAsync(ServerRec server, Credential cred, CancellationToken ct);
    Task<List<ScannedLink>> ScanDfsLinksAsync(DomainRec domain, Credential cred, CancellationToken ct);
    Task<QuotaInfo> SetQuotaAsync(ServerRec server, string path, long bytes, Credential cred, CancellationToken ct);
    Task<long> ComputeSizeAsync(ServerRec server, string path, Credential cred, CancellationToken ct);
    Task CreateFolderAsync(ServerRec server, string path, string userSid, string domainAdminsSid, Credential cred, CancellationToken ct);
    Task CreateDfsLinkAsync(DomainRec domain, string linkPath, string targetPath, Credential cred, CancellationToken ct);

    // ---- Horizon Connection Server (REST API)
    /// <summary>Logs in, lists desktop pools with their entitlements. <paramref name="horizonPassword"/> is the stored password when HorizonAuth is "stored".</summary>
    Task<HorizonTestResult> HorizonPoolsAsync(DomainRec domain, Credential cred, string? horizonPassword, CancellationToken ct);
    /// <summary>Entitles one AD user (by SID) directly to a desktop pool.</summary>
    Task HorizonEntitleUserAsync(DomainRec domain, string poolId, string userSid, string samAccountName, Credential cred, string? horizonPassword, CancellationToken ct);
}
