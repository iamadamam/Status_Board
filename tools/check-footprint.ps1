<#
.SYNOPSIS
  Verifies the portable build leaves nothing behind: runs it from a temporary folder and diffs
  %APPDATA%, %LOCALAPPDATA%, %TEMP% (incl. .net extraction dir) and HKCU\Software before/after.
.EXAMPLE
  .\tools\check-footprint.ps1            # uses artifacts\StatusBoard
#>
param([string]$BuildDir = (Join-Path $PSScriptRoot "..\artifacts\StatusBoard"), [int]$RunSeconds = 15)
$ErrorActionPreference = "Stop"

function Snapshot {
    $files = foreach ($root in $env:APPDATA, $env:LOCALAPPDATA, $env:TEMP) {
        Get-ChildItem $root -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    }
    $net = Get-ChildItem (Join-Path $env:TEMP ".net") -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    $reg = Get-ChildItem HKCU:\Software -Recurse -Depth 1 -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }
    [pscustomobject]@{ Files = @($files) + @($net); Reg = @($reg) }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("sb-footprint-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
Copy-Item $BuildDir $work -Recurse
$exe = Join-Path $work "StatusBoard.exe"

$before = Snapshot
Write-Host "Running $exe for $RunSeconds s..."
$p = Start-Process $exe -PassThru
Start-Sleep $RunSeconds
$null = $p.CloseMainWindow(); if (-not $p.WaitForExit(10000)) { $p.Kill() }
$after = Snapshot

$inApp = Get-ChildItem $work -Force | ForEach-Object Name
Remove-Item $work -Recurse -Force

$newFiles = Compare-Object $before.Files $after.Files | Where-Object SideIndicator -eq "=>" | ForEach-Object InputObject |
    Where-Object { $_ -notlike "$work*" -and $_ -notlike "*sb-footprint-*" }
$newReg = Compare-Object $before.Reg $after.Reg | Where-Object SideIndicator -eq "=>" | ForEach-Object InputObject

Write-Host "`nFiles in app folder after run: $($inApp -join ', ')"
Write-Host "New items in AppData/Temp: $(if ($newFiles) { "`n  " + ($newFiles -join "`n  ") } else { 'none' })"
Write-Host "New HKCU\Software keys:    $(if ($newReg) { "`n  " + ($newReg -join "`n  ") } else { 'none' })"
Write-Host "(Other processes can create items during the run; check any listed entries are not StatusBoard's.)"
