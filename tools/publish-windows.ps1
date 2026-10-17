#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory, [string]$Version = '0.1.0', [switch]$SingleFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $folder = if ($SingleFile) { 'artifacts/PureKHelper-win-x64-single' } else { 'artifacts/PureKHelper-win-x64' }
    $OutputDirectory = Join-Path $root $folder
}
$single = if ($SingleFile) { 'true' } else { 'false' }
& dotnet restore (Join-Path $root 'src/PureKBridge.App/PureKBridge.App.csproj') -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw 'ChunK Helper restore failed.' }
& (Join-Path $PSScriptRoot 'update-runtime-notices.ps1')
& dotnet publish (Join-Path $root 'src/PureKBridge.App/PureKBridge.App.csproj') `
    -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    "-p:PublishSingleFile=$single" "-p:Version=$Version" -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'ChunK Helper publish failed.' }
Write-Host "ChunK Helper: $OutputDirectory"
