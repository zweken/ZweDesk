namespace ZweDesk.Remote;

/// <summary>
/// PowerShell that runs ON the target server inside Invoke-Command. Every script emits exactly one JSON document
/// (ConvertTo-Json -Compress) and lets errors surface; nothing here swallows a failure of a mutating operation.
/// </summary>
public static class PsScripts
{
    /// <summary>A: folders under the root with owner, ACL and FSRM quota/usage. Args: $Root</summary>
    public const string ScanFolders = """
        param([string]$Root)
        $ErrorActionPreference = 'Stop'
        if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "User data root not found on this server: $Root" }
        $quotas = @{}
        $fsrmModule = [bool](Get-Module -ListAvailable FileServerResourceManager)
        if ($fsrmModule) {
            Import-Module FileServerResourceManager
            Get-FsrmQuota -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Root\*" } |
                ForEach-Object { $quotas[$_.Path.ToLowerInvariant()] = $_ }
        }
        $rows = Get-ChildItem -LiteralPath $Root -Directory -Force -ErrorAction Stop |
            ForEach-Object {
                $acl = $null; try { $acl = Get-Acl -LiteralPath $_.FullName } catch {}
                $q = $quotas[$_.FullName.ToLowerInvariant()]
                [pscustomobject]@{
                    name       = $_.Name
                    path       = $_.FullName
                    lastWrite  = $_.LastWriteTimeUtc.ToString('o')
                    owner      = if ($acl) { $acl.Owner } else { $null }
                    aces       = @(if ($acl) { $acl.Access | ForEach-Object {
                                    [pscustomobject]@{ id = $_.IdentityReference.Value
                                                       rights = $_.FileSystemRights.ToString()
                                                       type = $_.AccessControlType.ToString()
                                                       inherited = $_.IsInherited } } })
                    quotaBytes = if ($q) { [int64]$q.Size } else { $null }
                    usedBytes  = if ($q) { [int64]$q.Usage } else { $null }
                }
            }
        [pscustomobject]@{ computer = $env:COMPUTERNAME; fsrmModule = $fsrmModule; folders = @($rows | Where-Object { $_ }) } |
            ConvertTo-Json -Depth 6 -Compress
        """;

    /// <summary>B: create or change an FSRM hard quota. Args: $Path, $Bytes</summary>
    public const string SetQuota = """
        param([string]$Path, [int64]$Bytes)
        $ErrorActionPreference = 'Stop'
        Import-Module FileServerResourceManager
        if (-not (Test-Path -LiteralPath $Path)) { throw "Path not found: $Path" }
        if (Get-FsrmQuota -Path $Path -ErrorAction SilentlyContinue) {
            Set-FsrmQuota -Path $Path -Size $Bytes -Confirm:$false | Out-Null
        } else {
            New-FsrmQuota -Path $Path -Size $Bytes -Confirm:$false | Out-Null
        }
        $q = Get-FsrmQuota -Path $Path
        [pscustomobject]@{ path = $q.Path; size = [int64]$q.Size; usage = [int64]$q.Usage } | ConvertTo-Json -Compress
        """;

    /// <summary>C: size of a folder that has no quota (walks the tree on the server, not over SMB). Args: $Path</summary>
    public const string ComputeSize = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        if (-not (Test-Path -LiteralPath $Path)) { throw "Path not found: $Path" }
        $s = (Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
              Measure-Object -Property Length -Sum).Sum
        [pscustomobject]@{ path = $Path; usedBytes = [int64]($s + 0) } | ConvertTo-Json -Compress
        """;

    /// <summary>D: every DFS folder (link) under the namespace root with its targets. Args: $DfsRoot</summary>
    public const string ListDfsLinks = """
        param([string]$DfsRoot)
        $ErrorActionPreference = 'Stop'
        Import-Module DFSN
        $rows = Get-DfsnFolder -Path "$DfsRoot\*" -ErrorAction SilentlyContinue | ForEach-Object {
            $p = $_.Path
            [pscustomobject]@{
                path    = $p
                name    = Split-Path $p -Leaf
                state   = $_.State.ToString()
                targets = @(Get-DfsnFolderTarget -Path $p | ForEach-Object {
                             [pscustomobject]@{ target = $_.TargetPath; state = $_.State.ToString() } })
            }
        }
        ConvertTo-Json -InputObject @($rows | Where-Object { $_ }) -Depth 4 -Compress
        """;

    /// <summary>E: create one DFS link. Fails if the link already exists (policy lives in C#). Args: $LinkPath, $TargetPath</summary>
    public const string CreateDfsLink = """
        param([string]$LinkPath, [string]$TargetPath)
        $ErrorActionPreference = 'Stop'
        Import-Module DFSN
        if (Get-DfsnFolder -Path $LinkPath -ErrorAction SilentlyContinue) { throw "DFS link already exists: $LinkPath" }
        New-DfsnFolder -Path $LinkPath -TargetPath $TargetPath | Out-Null
        [pscustomobject]@{ link = $LinkPath; target = $TargetPath; ok = $true } | ConvertTo-Json -Compress
        """;

    /// <summary>F: FSRM role status, optionally installing it. Args: $Install</summary>
    public const string FsrmStatus = """
        param([bool]$Install)
        $ErrorActionPreference = 'Stop'
        $f = Get-WindowsFeature -Name FS-Resource-Manager
        if (-not $f.Installed -and $Install) {
            Install-WindowsFeature FS-Resource-Manager -IncludeManagementTools | Out-Null
            $f = Get-WindowsFeature -Name FS-Resource-Manager
        }
        [pscustomobject]@{ fsrmInstalled = [bool]$f.Installed
                           dfsnModule    = [bool](Get-Module -ListAvailable DFSN)
                           computer      = $env:COMPUTERNAME } | ConvertTo-Json -Compress
        """;

    /// <summary>G: SMB shares with their local paths.</summary>
    public const string ListShares = """
        $ErrorActionPreference = 'Stop'
        $rows = Get-SmbShare | Where-Object { $_.Path } | Select-Object Name, Path
        ConvertTo-Json -InputObject @($rows | Where-Object { $_ }) -Compress
        """;

    /// <summary>
    /// H: create a user folder with the standard ACL: inheritance off, owner = user, user Modify,
    /// SYSTEM / BUILTIN\Administrators / Domain Admins FullControl. Args: $Path, $UserSid, $DomainAdminsSid
    /// </summary>
    public const string CreateFolder = """
        param([string]$Path, [string]$UserSid, [string]$DomainAdminsSid)
        $ErrorActionPreference = 'Stop'
        if (Test-Path -LiteralPath $Path) { throw "Folder already exists: $Path" }
        New-Item -ItemType Directory -Path $Path | Out-Null
        $user = New-Object System.Security.Principal.SecurityIdentifier($UserSid)
        $da   = New-Object System.Security.Principal.SecurityIdentifier($DomainAdminsSid)
        $acl  = Get-Acl -LiteralPath $Path
        $acl.SetAccessRuleProtection($true, $false)
        $inh  = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
        $prop = [System.Security.AccessControl.PropagationFlags]::None
        $full = [System.Security.AccessControl.FileSystemRights]::FullControl
        $sys  = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
        $adm  = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
        foreach ($id in @($sys, $adm, $da)) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, $full, $inh, $prop, 'Allow')))
        }
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($user, [System.Security.AccessControl.FileSystemRights]::Modify, $inh, $prop, 'Allow')))
        $acl.SetOwner($user)
        Set-Acl -LiteralPath $Path -AclObject $acl
        [pscustomobject]@{ path = $Path; ok = $true } | ConvertTo-Json -Compress
        """;

    /// <summary>Diagnostics probe for a file server. Args: $Root</summary>
    public const string ProbeServer = """
        param([string]$Root)
        $ErrorActionPreference = 'Continue'
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $fsrm = $null; try { $fsrm = [bool](Get-WindowsFeature -Name FS-Resource-Manager -ErrorAction Stop).Installed } catch {}
        $fsrmModule = [bool](Get-Module -ListAvailable FileServerResourceManager)
        $rootExists = [bool](Test-Path -LiteralPath $Root)
        $shares = @(Get-SmbShare -ErrorAction SilentlyContinue | Where-Object { $_.Path } | ForEach-Object { [pscustomobject]@{ name = $_.Name; path = $_.Path } })
        $os = $null; try { $os = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).Caption } catch {}
        [pscustomobject]@{ computer = $env:COMPUTERNAME; isAdmin = $isAdmin; fsrmInstalled = $fsrm; fsrmModule = $fsrmModule
                           rootExists = $rootExists; shares = $shares; os = $os } | ConvertTo-Json -Depth 4 -Compress
        """;

    /// <summary>Diagnostics probe for the DFS namespace host. Args: $DfsRoot, $InstallTools</summary>
    public const string ProbeDfsHost = """
        param([string]$DfsRoot, [bool]$InstallTools)
        $ErrorActionPreference = 'Continue'
        $rsat = $null
        try {
            $f = Get-WindowsFeature -Name RSAT-DFS-Mgmt-Con -ErrorAction Stop
            if (-not $f.Installed -and $InstallTools) { Install-WindowsFeature RSAT-DFS-Mgmt-Con -ErrorAction Stop | Out-Null; $f = Get-WindowsFeature -Name RSAT-DFS-Mgmt-Con }
            $rsat = [bool]$f.Installed
        } catch {}
        $mod = [bool](Get-Module -ListAvailable DFSN)
        $rootOk = $false; $err = $null
        if ($mod) {
            try { Import-Module DFSN; $r = Get-DfsnRoot -Path $DfsRoot -ErrorAction Stop; $rootOk = [bool]$r } catch { $err = $_.Exception.Message }
        } else { $err = 'PowerShell module DFSN is not available on this host' }
        [pscustomobject]@{ computer = $env:COMPUTERNAME; dfsnModule = $mod; rootReachable = $rootOk; error = $err; rsatInstalled = $rsat } |
            ConvertTo-Json -Compress
        """;

    /// <summary>OUs and the Users/Computers containers of the domain the DC belongs to.</summary>
    public const string ListOus = """
        $ErrorActionPreference = 'Stop'
        Import-Module ActiveDirectory
        $dom = Get-ADDomain
        $rows = @([pscustomobject]@{ dn = $dom.DistinguishedName; name = $dom.DNSRoot; canonical = $dom.DNSRoot + '/'; kind = 'domain' })
        $rows += @(Get-ADOrganizationalUnit -Filter * -Properties CanonicalName | ForEach-Object {
            [pscustomobject]@{ dn = $_.DistinguishedName; name = $_.Name; canonical = $_.CanonicalName; kind = 'ou' } })
        $rows += @(Get-ADObject -LDAPFilter '(objectClass=container)' -SearchBase $dom.DistinguishedName -SearchScope OneLevel -Properties CanonicalName |
            Where-Object { $_.Name -in @('Users', 'Computers') } | ForEach-Object {
            [pscustomobject]@{ dn = $_.DistinguishedName; name = $_.Name; canonical = $_.CanonicalName; kind = 'container' } })
        ConvertTo-Json -InputObject @($rows | Where-Object { $_ } | Sort-Object canonical) -Compress
        """;

    /// <summary>Security groups of the domain.</summary>
    public const string ListGroups = """
        $ErrorActionPreference = 'Stop'
        Import-Module ActiveDirectory
        $rows = Get-ADGroup -Filter 'GroupCategory -eq "Security"' -Properties Description | Sort-Object Name | ForEach-Object {
            [pscustomobject]@{ name = $_.Name; sam = $_.SamAccountName; dn = $_.DistinguishedName; scope = $_.GroupScope.ToString(); description = $_.Description } }
        ConvertTo-Json -InputObject @($rows | Where-Object { $_ }) -Compress
        """;

    /// <summary>Domain controllers of the domain.</summary>
    public const string ListDcs = """
        $ErrorActionPreference = 'Stop'
        Import-Module ActiveDirectory
        $rows = Get-ADDomainController -Filter * | Sort-Object HostName | ForEach-Object {
            [pscustomobject]@{ hostName = $_.HostName; ip = $_.IPv4Address; site = $_.Site; gc = [bool]$_.IsGlobalCatalog } }
        ConvertTo-Json -InputObject @($rows | Where-Object { $_ }) -Compress
        """;

    /// <summary>
    /// Creates an enabled AD user in the given OU. The password arrives as the trailing SecureString argument
    /// (see WinRmRunner secret handling). Args: $Sam, $Upn, $GivenName, $Surname, $DisplayName, $OuDn, $MustChange, $Password
    /// </summary>
    public const string CreateAdUser = """
        param([string]$Sam, [string]$Upn, [string]$GivenName, [string]$Surname, [string]$DisplayName, [string]$OuDn, [bool]$MustChange, [SecureString]$Password)
        $ErrorActionPreference = 'Stop'
        Import-Module ActiveDirectory
        if (-not $Password) { throw 'No password was supplied' }
        $existing = Get-ADUser -LDAPFilter "(sAMAccountName=$Sam)" -ErrorAction SilentlyContinue
        if ($existing) { throw "AD account already exists: $Sam ($($existing.DistinguishedName))" }
        $u = New-ADUser -SamAccountName $Sam -UserPrincipalName $Upn -Name $DisplayName -GivenName $GivenName -Surname $Surname -DisplayName $DisplayName `
                        -Path $OuDn -AccountPassword $Password -Enabled $true -ChangePasswordAtLogon $MustChange -PassThru
        [pscustomobject]@{ sid = $u.SID.Value; dn = $u.DistinguishedName; sam = $u.SamAccountName; upn = $u.UserPrincipalName } | ConvertTo-Json -Compress
        """;

    /// <summary>Adds an existing user to groups (identified by DN or name); reports added / already member / failed. Args: $Sam, $GroupsJson</summary>
    public const string AddToGroups = """
        param([string]$Sam, [string]$GroupsJson)
        $ErrorActionPreference = 'Stop'
        Import-Module ActiveDirectory
        $u = Get-ADUser -LDAPFilter "(sAMAccountName=$Sam)" -Properties memberOf
        if (-not $u) { throw "AD account not found: $Sam" }
        $groups = @(); if ($GroupsJson) { $groups = @(ConvertFrom-Json $GroupsJson) }
        $memberOf = @($u.memberOf)
        $added = @(); $already = @(); $failed = @()
        foreach ($g in $groups) {
            try {
                $grp = Get-ADGroup -Identity $g -ErrorAction Stop
                if ($memberOf -contains $grp.DistinguishedName) { $already += $grp.Name; continue }
                Add-ADGroupMember -Identity $grp -Members $u -ErrorAction Stop
                $added += $grp.Name
            } catch { $failed += ("$g" + ': ' + $_.Exception.Message) }
        }
        [pscustomobject]@{ added = @($added); alreadyMember = @($already); failed = @($failed) } | ConvertTo-Json -Compress
        """;

    /// <summary>Cheapest possible round trip: proves WinRM + credentials work.</summary>
    public const string Ping = """
        [pscustomobject]@{ computer = $env:COMPUTERNAME; user = [Security.Principal.WindowsIdentity]::GetCurrent().Name } | ConvertTo-Json -Compress
        """;
}
