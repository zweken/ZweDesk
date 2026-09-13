#requires -Version 5.1
<#
.SYNOPSIS
    Builds ZweDesk as ONE portable executable: dist\ZweDesk.exe.

.DESCRIPTION
    One command, no manual steps:
      1. Finds a .NET SDK 10.x. If none is installed, installs one in user scope
         (%LOCALAPPDATA%\Microsoft\dotnet, no administrator rights needed) using the
         official dotnet-install script.
      2. Downloads the official WebView2 Runtime installers into prereq\cache\
         (Evergreen Standalone x64 for offline machines, Evergreen Bootstrapper for
         machines with internet). They are embedded into the executable so a Windows
         Server without the runtime can be provisioned from the single file.
      3. dotnet publish  ->  dist\ZweDesk.exe  (single file, self-contained, compressed).

    Copy dist\ZweDesk.exe anywhere and run it. Nothing else is needed.

.PARAMETER Configuration      Release (default) or Debug.
.PARAMETER NoEmbeddedRuntime  Build the small (~50 MB) executable without the WebView2 installers.
                              The target must then already have the WebView2 Runtime, or the
                              installers are placed in a prereq\ folder next to the exe.
.PARAMETER SkipPublish        Only make sure the SDK is present (used by CI / first setup).
.PARAMETER NoSdkInstall       Fail instead of installing a missing SDK.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoEmbeddedRuntime,
    [switch]$SkipPublish,
    [switch]$NoSdkInstall
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$root       = $PSScriptRoot
$dist       = Join-Path $root 'dist'
$cacheDir   = Join-Path $root 'prereq\cache'
$sdkChannel = '10.0'
$userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

function Write-Step([string]$Text) { Write-Host ("`n==> " + $Text) -ForegroundColor Cyan }

function Test-SdkAt([string]$Exe) {
    if (-not (Test-Path -LiteralPath $Exe)) { return $false }
    try {
        $sdks = & $Exe --list-sdks 2>$null
        return [bool]($sdks | Where-Object { $_ -match ('^' + [regex]::Escape($sdkChannel.Split('.')[0]) + '\.') })
    } catch { return $false }
}

function Find-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $userDotnet 'dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($c in $candidates) { if (Test-SdkAt $c) { return $c } }
    return $null
}

function Install-Sdk {
    if ($NoSdkInstall) { throw ".NET SDK $sdkChannel not found and -NoSdkInstall was given." }
    Write-Step "Installing .NET SDK $sdkChannel in user scope: $userDotnet (no administrator rights needed)"
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel $sdkChannel -InstallDir $userDotnet -NoPath
    $exe = Join-Path $userDotnet 'dotnet.exe'
    if (-not (Test-SdkAt $exe)) { throw "SDK installation finished but $exe has no $sdkChannel SDK." }
    return $exe
}

# ---------------------------------------------------------------- 1. SDK
Write-Step "Locating .NET SDK $sdkChannel"
$dotnet = Find-Dotnet
if (-not $dotnet) { $dotnet = Install-Sdk }
Write-Host "Using $dotnet"
& $dotnet --version
if ($SkipPublish) { Write-Step 'Done (SDK only)'; return }

# ---------------------------------------------------------------- 2. WebView2 Runtime installers (build input)
$downloads = @(
    @{ Name = 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'; Url = 'https://go.microsoft.com/fwlink/?linkid=2124701';   Note = 'Evergreen Standalone x64 (offline install, ~200 MB)' },
    @{ Name = 'MicrosoftEdgeWebview2Setup.exe';               Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'; Note = 'Evergreen Bootstrapper (online install, ~2 MB)' }
)
$embedded = @()
if (-not $NoEmbeddedRuntime) {
    Write-Step "WebView2 Runtime installers in $cacheDir"
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    foreach ($d in $downloads) {
        $target = Join-Path $cacheDir $d.Name
        if (-not (Test-Path -LiteralPath $target)) {
            Write-Host "  downloading $($d.Note) ..."
            try { Invoke-WebRequest -Uri $d.Url -OutFile $target -UseBasicParsing }
            catch {
                Write-Warning "  could not download $($d.Name): $($_.Exception.Message)"
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
                continue
            }
        }
        $sig = Get-AuthenticodeSignature -FilePath $target
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') {
            Write-Warning "  $($d.Name) is not a validly signed Microsoft binary ($($sig.Status)); it will not be embedded."
            Remove-Item -LiteralPath $target -Force
            continue
        }
        $mb = [math]::Round((Get-Item $target).Length / 1MB, 1)
        Write-Host "  ok: $($d.Name) ($mb MB, signature valid)"
        $embedded += $d.Name
    }
    if ($embedded.Count -eq 0) { Write-Warning 'No installer available: the executable will be built without an embedded WebView2 Runtime.' }
}

# ---------------------------------------------------------------- 3. Publish
Write-Step "Publishing single-file executable ($Configuration, win-x64, self-contained)"
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$embedFlag = if ($NoEmbeddedRuntime) { 'false' } else { 'true' }
& $dotnet publish (Join-Path $root 'ZweDesk.csproj') -c $Configuration -r win-x64 --self-contained true -o $dist -nologo "-p:EmbedRuntimeInstaller=$embedFlag"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force
$exe = Join-Path $dist 'ZweDesk.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish produced no $exe" }
$extra = Get-ChildItem -LiteralPath $dist -File | Where-Object { $_.Name -ne 'ZweDesk.exe' }
if ($extra) { throw "Publish is not single-file; unexpected files: $($extra.Name -join ', ')" }

if ($NoEmbeddedRuntime) {
    # small build: stage the installers next to the exe when we have them, so the first start can still install the runtime
    $have = Get-ChildItem -LiteralPath $cacheDir -File -ErrorAction SilentlyContinue
    if ($have) {
        $prereqOut = Join-Path $dist 'prereq'
        New-Item -ItemType Directory -Force -Path $prereqOut | Out-Null
        $have | Copy-Item -Destination $prereqOut
        Write-Host "Staged WebView2 installers in $prereqOut (needed only if the target lacks the runtime)"
    }
}

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Step 'Done'
Write-Host "Deliverable: $exe ($mb MB)" -ForegroundColor Green
if ($embedded.Count) { Write-Host "  embedded WebView2 installers: $($embedded -join ', ')" }
Write-Host '  run ZweDesk.exe, or ZweDesk.exe --mock for the demo mode; --selftest checks the scripts and embedded payload'
