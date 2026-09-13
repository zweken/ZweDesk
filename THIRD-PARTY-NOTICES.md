# Third-party notices

ZweDesk is published under the Apache License 2.0 (see `LICENSE`). The self-contained `ZweDesk.exe` bundles the
components below; the source repository contains none of them (they are restored from NuGet or downloaded from
Microsoft by `build.ps1` at build time). Nothing else — no fonts, icons, CSS or JavaScript libraries — is taken from a
third party: the interface is hand-written and loads no external resource.

| Component | Version | License | Use |
|---|---|---|---|
| .NET Runtime and Windows Desktop Runtime (Windows Forms) | 10.0 | MIT | bundled in the self-contained executable |
| Microsoft.Web.WebView2 SDK | 1.0.3179.45 | BSD-style (below) | hosts the interface; loader and interop assemblies bundled |
| Microsoft Edge WebView2 Runtime — Evergreen installers | current at build time | Microsoft license terms (below) | embedded unchanged in the full build; installed system-wide on first start when the runtime is missing |
| Microsoft.Data.Sqlite | 10.0.12 | MIT | local database |
| SQLitePCLRaw (core, provider.e_sqlite3, bundle_e_sqlite3, lib.e_sqlite3) | 2.1.12 | Apache-2.0 | native SQLite binding bundled |
| SQLite | as bundled by SQLitePCLRaw | Public domain | database engine |

---

## .NET Runtime, Windows Desktop Runtime, Microsoft.Data.Sqlite

MIT License

Copyright (c) .NET Foundation and Contributors
Copyright (c) Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Sources: https://github.com/dotnet/runtime · https://github.com/dotnet/winforms · https://github.com/dotnet/efcore

## Microsoft.Web.WebView2 SDK

Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

* Redistributions of source code must retain the above copyright notice, this list of conditions and the following
  disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
  disclaimer in the documentation and/or other materials provided with the distribution.
* The name of Microsoft Corporation, or the names of its contributors may not be used to endorse or promote products
  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Source: https://www.nuget.org/packages/Microsoft.Web.WebView2 (LICENSE.txt in the package)

## Microsoft Edge WebView2 Runtime (Evergreen installers)

The full build of `ZweDesk.exe` embeds the official Evergreen Bootstrapper and Evergreen Standalone Installer exactly
as downloaded from Microsoft (`https://go.microsoft.com/fwlink/?linkid=2124701`, `https://go.microsoft.com/fwlink/p/?LinkId=2124703`;
`build.ps1` refuses to embed a file whose Authenticode signature is not a valid Microsoft signature). Microsoft's
distribution guidance for WebView2 explicitly provides for packaging these installers with an application:
https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution. The runtime itself is Microsoft software
under Microsoft's license terms, installed on the target computer by Microsoft's installer; ZweDesk does not modify it.

## SQLitePCLRaw

Copyright 2014-2024 SourceGear, LLC

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with
the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0. Unless required by
applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language
governing permissions and limitations under the License.

Source: https://github.com/ericsink/SQLitePCL.raw

## SQLite

SQLite is in the public domain: https://www.sqlite.org/copyright.html
