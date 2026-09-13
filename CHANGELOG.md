# Changelog

## 1.0.2 — first public release

* Single portable executable; embedded WebView2 Runtime installers for machines without the runtime.
* New User wizard: AD account, group memberships, home folder with standard ACL, FSRM quota, DFS link, Horizon entitlement.
* Scan, issue engine, user-folder table with usage bars, DFS link sync (dry run first), diagnostics with one-click
  role installation, multi-environment configuration, audit log.
* First start reads the computer's domain membership; the DFS root is taken from Active Directory at the first sign-in
  when the domain has exactly one namespace; **Find namespaces** lists all of them.
* Link checks are judged per target server; a server without a successful scan yields **not verified** instead of a
  false "no folder".
* Unreachable servers keep the reason (with the usual remedy) on the Dashboard and under Environment.
* Frameless window with the interface's own title bar; dark and light theme.
* Demo mode (`--mock`) with fictional data.
