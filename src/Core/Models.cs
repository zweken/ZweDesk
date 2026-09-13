namespace ZweDesk.Core;

// ---------------------------------------------------------------- persisted rows

/// <summary>One environment: an AD domain with its directory server, DFS namespace, file servers and (optionally) a Horizon Connection Server.</summary>
public sealed record DomainRec(
    long Id, string Name, string Netbios, string DfsRoot, string DfsHostFqdn, DateTime CreatedAt,
    string DcFqdn, string HorizonUrl, string HorizonAuth, string HorizonUser, string HorizonDomain, bool HorizonIgnoreCert, string HorizonEntitleMode,
    string DefaultOuDn, string DefaultGroupsJson, string DefaultPoolId)
{
    public bool HorizonConfigured => !string.IsNullOrWhiteSpace(HorizonUrl) && HorizonAuth != "none";
    /// <summary>Host that runs the Active Directory cmdlets (a domain controller). Falls back to the DFS host, which is the DC by default.</summary>
    public string DirectoryHost => string.IsNullOrWhiteSpace(DcFqdn) ? DfsHostFqdn : DcFqdn;
}

public sealed record HorizonPoolRec(long Id, long DomainId, string PoolId, string Name, string? DisplayName, string? Type, bool Enabled, string EntitledGroupsJson, int EntitledUsers, DateTime FetchedAt);

public sealed record ServerRec(
    long Id, long DomainId, string Name, string Fqdn, string? ShareName, string LocalRoot, bool Enabled,
    DateTime? LastScanAt, string? LastError, bool? FsrmInstalled);

public sealed record FolderRec(
    long Id, long ServerId, string Name, string NameKey, string Path, string? Owner, string? UserRights,
    string ExtraAccessJson, string AcesJson, long? QuotaBytes, long? UsedBytes, DateTime? LastWriteUtc, DateTime ScannedAt);

public sealed record DfsLinkRec(long Id, long DomainId, string Name, string NameKey, string LinkPath, string TargetsJson, string? State, DateTime ScannedAt);

public sealed record IssueRec(long Id, long DomainId, string Kind, string NameKey, string DetailsJson, DateTime DetectedAt);

public sealed record AuditRec(long Id, DateTime Ts, string Actor, string? Domain, string? Server, string Op, string? Target, string? ParamsJson, bool Ok, string? Error);

// ---------------------------------------------------------------- provider payloads (mirror the JSON the PowerShell scripts emit)

public sealed record Ace(string Id, string Rights, string Type, bool Inherited);

public sealed record ScannedFolder(string Name, string Path, DateTime? LastWriteUtc, string? Owner, List<Ace> Aces, long? QuotaBytes, long? UsedBytes);

public sealed record ScanResult(string Computer, bool FsrmModule, List<ScannedFolder> Folders);

public sealed record LinkTarget(string Target, string State);

public sealed record ScannedLink(string Path, string Name, string State, List<LinkTarget> Targets);

public sealed record QuotaInfo(string Path, long Size, long Usage);

public sealed record ShareMap(string Name, string Path);

public sealed record ServerProbe(string Computer, bool IsAdmin, bool? FsrmInstalled, bool FsrmModule, bool RootExists, List<ShareMap> Shares, string? Os);

public sealed record DfsHostProbe(string Computer, bool DfsnModule, bool RootReachable, string? Error, bool? RsatInstalled);

public sealed record AdUser(string SamAccountName, string Sid, string DistinguishedName, string? DisplayName, bool Disabled);

public sealed record AdInfo(string DomainSid, string Netbios, string DomainAdminsSid, string? DcHost = null);

public sealed record OuInfo(string Dn, string Name, string Canonical, string Kind);

public sealed record GroupInfo(string Name, string Sam, string Dn, string Scope, string? Description);

public sealed record DcInfo(string HostName, string? Ip, string? Site, bool GlobalCatalog);

public sealed record AdUserSpec(string Sam, string Upn, string GivenName, string Surname, string DisplayName, string OuDn, bool MustChangePassword);

public sealed record GroupAddResult(List<string> Added, List<string> AlreadyMember, List<string> Failed);

public sealed record HorizonPool(string Id, string Name, string? DisplayName, string? Type, bool Enabled, List<string> EntitledGroups, int EntitledUsers);

public sealed record HorizonTestResult(bool Ok, string Message, List<HorizonPool> Pools);

/// <summary>Credentials entered at login. Lives in memory for the session only; never serialized, never logged.</summary>
public sealed class Credential
{
    public required string DomainName { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
    public string Netbios { get; set; } = "";

    /// <summary>NETBIOS\user when the NetBIOS name is known, otherwise dns.domain\user (both are valid Kerberos logon names).</summary>
    public string LogonName => (string.IsNullOrEmpty(Netbios) ? DomainName : Netbios) + "\\" + User;
    public string Upn => User + "@" + DomainName;
}

public static class IssueKinds
{
    public const string DuplicateFolder = "DUPLICATE_FOLDER";
    public const string LinkTargetMismatch = "LINK_TARGET_MISMATCH";
    public const string FolderWithoutLink = "FOLDER_WITHOUT_LINK";
    public const string LinkWithoutFolder = "LINK_WITHOUT_FOLDER";
    public const string ExtraAccess = "EXTRA_ACCESS";
    public const string NoQuota = "NO_QUOTA";
}

public sealed class AppException : Exception
{
    public string? Details { get; }
    public AppException(string message, string? details = null, Exception? inner = null) : base(message, inner) => Details = details;
}
