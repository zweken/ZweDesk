using ZweDesk.Core;

namespace ZweDesk.Remote;

public sealed record PsResult(bool Ok, string Stdout, string Stderr, int ExitCode, string? Error, TimeSpan Duration)
{
    /// <summary>Throws an AppException carrying the remote error text when the script failed.</summary>
    public string Require(string what)
    {
        if (Ok) return Stdout;
        var hint = WinRmRunner.Hint(Error);
        throw new AppException($"{what} failed: {Error ?? "unknown error"}" + (hint is null ? "" : " — " + hint), string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr);
    }
}

/// <summary>Runs a PowerShell script block on a remote computer as the session credential and returns its stdout (JSON).</summary>
public interface IRemoteRunner
{
    /// <param name="secret">Optional secret (e.g. a new account password). Delivered to the script as a trailing
    /// [SecureString] argument via stdin — never on a command line.</param>
    Task<PsResult> RunAsync(string computerFqdn, string script, IReadOnlyList<object?> args, Credential cred,
        CancellationToken ct, TimeSpan? timeout = null, string? secret = null);
}
