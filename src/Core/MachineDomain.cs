using System.Runtime.InteropServices;

namespace ZweDesk.Core;

/// <summary>
/// Domain membership of this computer, read from the local LSA policy (DsRoleGetPrimaryDomainInformation).
/// No network access: it answers even when no domain controller is reachable.
/// </summary>
public static class MachineDomain
{
    /// <param name="DnsName">DNS name of the domain the computer is joined to (lower case).</param>
    /// <param name="Netbios">NetBIOS (flat) name of that domain.</param>
    /// <param name="IsDomainController">True on a domain controller.</param>
    public sealed record Info(string DnsName, string Netbios, bool IsDomainController);

    /// <summary>Null on a workgroup computer (or when the query fails).</summary>
    public static Info? Query()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buf = IntPtr.Zero;
        try
        {
            if (DsRoleGetPrimaryDomainInformation(null, 1 /* DsRolePrimaryDomainInfoBasic */, out buf) != 0 || buf == IntPtr.Zero) return null;
            var info = Marshal.PtrToStructure<DsRolePrimaryDomainInfoBasic>(buf);
            var dns = info.DomainNameDns == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.DomainNameDns);
            if (string.IsNullOrWhiteSpace(dns)) return null;
            var flat = info.DomainNameFlat == IntPtr.Zero ? "" : Marshal.PtrToStringUni(info.DomainNameFlat) ?? "";
            return new Info(dns.TrimEnd('.').ToLowerInvariant(), flat.ToUpperInvariant(), info.MachineRole is 4 or 5);
        }
        catch (Exception ex)
        {
            Log.Warn("machine domain query failed: " + ex.Message);
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) DsRoleFreeMemory(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsRolePrimaryDomainInfoBasic
    {
        /// <summary>0 standalone workstation, 1 member workstation, 2 standalone server, 3 member server, 4 backup DC, 5 primary DC.</summary>
        public int MachineRole;
        public uint Flags;
        public IntPtr DomainNameFlat;
        public IntPtr DomainNameDns;
        public IntPtr DomainForestName;
        public Guid DomainGuid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int DsRoleGetPrimaryDomainInformation(string? server, int infoLevel, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern void DsRoleFreeMemory(IntPtr buffer);
}
