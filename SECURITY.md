# Security

ZweDesk runs with domain-administrator credentials and executes PowerShell on domain controllers and file servers.
Security reports are taken seriously and handled before anything else.

## Reporting a vulnerability

Please **do not** open a public issue for a security problem. Use GitHub's private vulnerability reporting on this
repository ("Security" tab → "Report a vulnerability"). Include the version (shown in the title bar and in the file
properties of `ZweDesk.exe`), what you did, what happened and — if you can — the relevant lines of
`%LocalAppData%\ZweDesk\logs\zwedesk-YYYYMMDD.log` (the log never contains passwords).

You will get an acknowledgement within a few days. Fixes are published as a new release with the advisory; reporters are
credited unless they prefer not to be.

## Scope

In scope: anything that lets a credential, a password or a secret leak (command line, file, log, audit row, memory of
another process, network in clear text); anything that makes the tool execute something on a server that the signed-in
account did not ask for; privilege escalation on the machine running the tool; tampering with the embedded interface or
the WebView2 installer path.

Out of scope: attacks that require an already compromised administrator account or administrator rights on the machine
running the tool, and the behaviour of the Windows components the tool drives (WinRM, FSRM, DFS-N, Active Directory,
Horizon).

## How the tool handles secrets

* The sign-in password and a new account's password reach `powershell.exe` on **stdin only** — never on a command line,
  in an environment variable, a file, the audit log or the application log (`src/Remote/WinRmRunner.cs`).
* Remote scripts run through `Invoke-Command` with Kerberos; a new account's password crosses the network only as a
  `SecureString` inside the encrypted WinRM session.
* "Remember on this computer" and the Horizon administrator password are protected with DPAPI for the current Windows
  account (`DataProtectionScope.CurrentUser`).
* The interface is served from an internal virtual host, loads nothing from the network, and the WebView2 settings
  disable autofill, password saving, script dialogs, error pages and developer tools (unless `--debug`).
* The complete list of scripts the tool can execute remotely is `src/Remote/PsScripts.cs`; the only ones that write to
  Active Directory are `CreateAdUser` (`New-ADUser`) and `AddToGroups` (`Add-ADGroupMember`).

## Supported versions

Only the latest release receives security fixes.
