<#
.SYNOPSIS
  Tests and publishes Status Board to artifacts\StatusBoard\ plus a release zip.

.DESCRIPTION
  Default: a portable, self-contained folder (StatusBoard.exe + 5 small WPF native DLLs); no .NET install needed, nothing
  written outside the folder at runtime. The zip is made from a clean publish, so it never contains settings or cached logos;
  your profiles (statusboard.json, profiles.json, profile-*.json), history.json and logos\ in an existing artifacts\StatusBoard are kept.

  -FrameworkDependent: a ~1 MB build that needs the .NET 10 Desktop Runtime installed on the PC.
  -SingleExe: one file; .NET extracts the WPF native DLLs to %TEMP%\.net on first run (so not zero-footprint).
  The exe is uncompressed by default: it is mapped from the file and shared with Windows' file cache, so it uses far less RAM
  than a compressed one (which is unpacked into private memory at startup). The zip download is about the same size either way.
  -Compact: a compressed exe (~55 MB instead of ~118 MB on disk) that uses ~70 MB more RAM while running.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param([switch]$FrameworkDependent, [switch]$SingleExe, [switch]$Compact, [switch]$SkipTests, [ValidateSet("win-x64", "win-arm64")] [string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
if (-not ((& dotnet --list-sdks) -match '^10\.')) { throw ".NET 10 SDK required: winget install Microsoft.DotNet.SDK.10" }
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$shortVersion = ($version -split '\.')[0..1] -join '.'

if (-not $SkipTests) {
    Write-Host "==> Tests" -ForegroundColor Cyan
    & dotnet test tests/StatusBoard.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$artifacts = Join-Path $PSScriptRoot "artifacts"
$out = Join-Path $artifacts "StatusBoard"
$staging = Join-Path $artifacts ".staging"   # publish here first, so a running copy can't block the build
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> Publish Status Board $version ($Runtime, $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))" -ForegroundColor Cyan
# ReadyToRun: compiled ahead of time, so the app starts without waiting for most code to be compiled on first use.
$props = @("-p:PublishSingleFile=true", "-p:PublishReadyToRun=true", "-p:DebugType=none", "-p:GenerateDocumentationFile=false")
if ($FrameworkDependent) { $props += "--self-contained", "false" }
else { $props += "--self-contained", "true", "-p:EnableCompressionInSingleFile=$($Compact.IsPresent.ToString().ToLower())", "-p:IncludeNativeLibrariesForSelfExtract=$($SingleExe.IsPresent.ToString().ToLower())" }
& dotnet publish src/StatusBoard -c Release -r $Runtime @props -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Copy-Item release\README.txt $staging
Copy-Item LICENSE (Join-Path $staging "LICENSE.txt")

$zip = Join-Path $artifacts "StatusBoard-v$shortVersion-$Runtime$(if ($FrameworkDependent) { '-fd' }).zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
"{0}  {1}" -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(), (Split-Path $zip -Leaf) | Set-Content "$zip.sha256" -Encoding ascii

# Swap in the new build, keeping local settings and logos. If Status Board is running from it, leave it alone (the zip is up to date).
$inUse = (Test-Path "$out\StatusBoard.exe") -and $(try { [IO.File]::Open("$out\StatusBoard.exe", 'Open', 'ReadWrite', 'None').Close(); $false } catch { $true })
if ($inUse) {
    Write-Warning "artifacts\StatusBoard is in use (Status Board is running from it), so it was left untouched. Close the app and re-run to refresh it."
    $out = $staging
} else {
    # Keep local settings: every profile (statusboard.json, profiles.json, profile-*.json), the 24-hour history, cached and uploaded logos.
    $keepList = @("statusboard.json", "profiles.json", "history.json", "logos") + @(Get-ChildItem $out -Filter "profile-*.json" -ErrorAction SilentlyContinue | ForEach-Object Name)
    foreach ($keep in $keepList) { if (Test-Path (Join-Path $out $keep)) { Copy-Item (Join-Path $out $keep) $staging -Recurse -Force } }
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item $staging $out
}

Write-Host "`nDone." -ForegroundColor Green
Get-ChildItem $out -File | ForEach-Object { "  {0,-30} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
"  -> $zip ({0:N1} MB), SHA-256 in $(Split-Path $zip -Leaf).sha256" -f ((Get-Item $zip).Length / 1MB)
