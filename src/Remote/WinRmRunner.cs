using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZweDesk.Core;

namespace ZweDesk.Remote;

/// <summary>
/// Executes a script block on a remote computer through Windows PowerShell 5.1 + Invoke-Command (WinRM, Kerberos).
/// The wrapper is passed with -EncodedCommand; the password travels over stdin only — never on a command line,
/// in an environment variable, or in a log.
/// </summary>
public sealed class WinRmRunner : IRemoteRunner
{
    private static readonly string PowerShellExe =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public async Task<PsResult> RunAsync(string computerFqdn, string script, IReadOnlyList<object?> args, Credential cred,
        CancellationToken ct, TimeSpan? timeout = null, string? secret = null)
    {
        var wrapper = BuildWrapper(computerFqdn, script, args, cred.LogonName, secret is not null);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellExe,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"winrm: cannot start {PowerShellExe}", ex);
            return new PsResult(false, "", "", -1, "Cannot start powershell.exe: " + ex.Message, sw.Elapsed);
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.StandardInput.WriteLineAsync(cred.Password.AsMemory(), ct);
            if (secret is not null) await p.StandardInput.WriteLineAsync(secret.AsMemory(), ct);
            await p.StandardInput.FlushAsync(ct);
            p.StandardInput.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("winrm: writing stdin failed: " + ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            var why = ct.IsCancellationRequested ? "cancelled" : $"timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s";
            Log.Warn($"winrm: {computerFqdn}: {why}");
            return new PsResult(false, "", "", -1, $"Remote command {why}", sw.Elapsed);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        sw.Stop();

        if (p.ExitCode == 0)
        {
            Log.Info($"winrm: {computerFqdn}: ok in {sw.Elapsed.TotalSeconds:0.0}s ({stdout.Length} chars)");
            return new PsResult(true, stdout, stderr, 0, null, sw.Elapsed);
        }

        var error = ExtractError(stdout) ?? (string.IsNullOrWhiteSpace(stderr) ? "exit code " + p.ExitCode : stderr);
        Log.Warn($"winrm: {computerFqdn}: FAILED in {sw.Elapsed.TotalSeconds:0.0}s: {error}");
        return new PsResult(false, stdout, stderr, p.ExitCode, error, sw.Elapsed);
    }

    /// <summary>
    /// One sentence an administrator can act on, for the WinRM failures met most often; null when the message is not one of them.
    /// The texts matched are the fixed English fragments of the WinRM client error messages.
    /// </summary>
    public static string? Hint(string? error)
    {
        if (string.IsNullOrEmpty(error)) return null;
        bool Has(params string[] fragments) => fragments.Any(f => error.Contains(f, StringComparison.OrdinalIgnoreCase));
        if (Has("server name cannot be resolved", "No such host is known"))
            return "DNS does not resolve this name: check the server's FQDN under Environment.";
        if (Has("TrustedHosts", "not joined to a domain"))
            return "This computer is not a member of the domain (or the server is addressed without its FQDN): run ZweDesk on a domain-joined computer, or add the server to WinRM TrustedHosts on this computer.";
        if (Has("Kerberos"))
            return "Kerberos cannot identify the server: use the FQDN registered in DNS; this computer and the server must be members of the same (or a trusted) domain.";
        if (Has("Access is denied", "0x80070005"))
            return "The account is not a member of the Administrators group on that server.";
        if (Has("user name or password is incorrect", "Logon failure", "0x8009030d"))
            return "That server rejected the credentials: sign in with a domain account that is an administrator there.";
        if (Has("WinRM cannot complete the operation", "cannot connect to the destination", "winrm quickconfig", "0x80338012", "0x80338126", "actively refused", "timed out"))
            return "WinRM is not enabled on the server or TCP 5985 is blocked by a firewall: on the server run  Enable-PSRemoting -Force  (Windows Server enables it by default; workstations do not).";
        return null;
    }

    private static string? ExtractError(string stdout)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Single-quoted PowerShell literal (the only safe way to inject text into a script).</summary>
    public static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    public static string PsLiteral(object? value) => value switch
    {
        null => "$null",
        bool b => b ? "$true" : "$false",
        int i => "[int64]" + i.ToString(CultureInfo.InvariantCulture),
        long l => "[int64]" + l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        _ => PsQuote(value.ToString() ?? ""),
    };

    internal static string BuildWrapper(string computerFqdn, string script, IReadOnlyList<object?> args, string logonName, bool withSecret = false)
    {
        if (script.Split('\n').Any(l => l.TrimEnd('\r').Trim() == "'@"))
            throw new ArgumentException("script must not contain a line consisting of '@");

        var literals = args.Select(PsLiteral).ToList();
        if (withSecret) literals.Add("$secret");
        var argList = literals.Count == 0 ? "@()" : "@(" + string.Join(", ", literals) + ")";
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        sb.AppendLine("[Console]::InputEncoding = [System.Text.Encoding]::UTF8");
        sb.AppendLine("try {");
        sb.AppendLine("  $plain = [Console]::In.ReadLine()");
        sb.AppendLine("  if ([string]::IsNullOrEmpty($plain)) { throw 'No credential received on stdin' }");
        sb.AppendLine("  $pw = ConvertTo-SecureString -String $plain -AsPlainText -Force");
        sb.AppendLine("  Remove-Variable plain");
        if (withSecret)
        {
            sb.AppendLine("  $secretPlain = [Console]::In.ReadLine()");
            sb.AppendLine("  if ([string]::IsNullOrEmpty($secretPlain)) { throw 'No secret received on stdin' }");
            sb.AppendLine("  $secret = ConvertTo-SecureString -String $secretPlain -AsPlainText -Force");
            sb.AppendLine("  Remove-Variable secretPlain");
        }
        sb.AppendLine("  $cred = New-Object System.Management.Automation.PSCredential(" + PsQuote(logonName) + ", $pw)");
        sb.AppendLine("  $sb = [scriptblock]::Create(@'");
        sb.AppendLine(script.Replace("\r\n", "\n").TrimEnd());
        sb.AppendLine("'@)");
        sb.AppendLine("  $out = Invoke-Command -ComputerName " + PsQuote(computerFqdn) + " -Credential $cred -ScriptBlock $sb -ArgumentList " + argList);
        sb.AppendLine("  [Console]::Out.Write((($out | Out-String -Width 1000000).Trim()))");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  $msg = $_.Exception.Message");
        sb.AppendLine("  if ($_.Exception.InnerException) { $msg += ' | ' + $_.Exception.InnerException.Message }");
        sb.AppendLine("  [Console]::Out.Write((@{ error = $msg; category = $_.CategoryInfo.ToString(); computer = " + PsQuote(computerFqdn) + " } | ConvertTo-Json -Compress))");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
