# Contributing

Thanks for taking the time. A few things make contributions easy to review and safe to ship.

## Before you start

* Open an issue for anything bigger than a bug fix so the approach can be discussed first.
* The tool runs with domain-administrator credentials. Changes to `src/Remote/PsScripts.cs`, `src/Remote/WinRmRunner.cs`,
  the credential handling in `src/Core/AppService.cs` or the WebView2 settings in `src/App/MainForm.cs` need a
  description of what runs where and why — they are reviewed with more care than the rest.

## Building and testing

* `.\build.ps1 -NoEmbeddedRuntime` builds the small executable quickly; the interface needs no build step.
* `dist\ZweDesk.exe --selftest` checks every remote script for syntax errors.
* `dist\ZweDesk.exe --mock` exercises the whole interface against fictional data — please walk through the screens
  you touched.
* Real-world changes (WinRM, FSRM, DFS-N, AD, Horizon) should be tried against a lab domain; say in the pull request what
  you tested against.

## Style

* C#: the existing style — file-scoped namespaces, records for data, no abbreviations in names, XML doc comments on
  public members that are not obvious. Keep the provider boundary: everything that touches a server goes through
  `IServerProvider`, and every remote script returns one JSON document.
* Interface: plain HTML / CSS / JavaScript, no framework, no external resource, no build step. Every string a user
  sees is English.
* Commit messages: one line saying what changed and why; the body only when the "why" is not obvious.

## Licensing

By contributing you agree that your contribution is licensed under the Apache License 2.0 like the rest of the project.
Do not copy code from sources whose license is not compatible with Apache-2.0, and say where borrowed code comes from.
