#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory, [switch]$SingleFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $folder = if ($SingleFile) { 'artifacts/ChunKHelper-win-x64-single' } else { 'artifacts/ChunKHelper-win-x64' }
    $OutputDirectory = Join-Path $root $folder
}
$single = if ($SingleFile) { 'true' } else { 'false' }
& dotnet publish (Join-Path $root 'src/PureKBridge.App/PureKBridge.App.csproj') `
    -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    "-p:PublishSingleFile=$single" -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'ChunK Helper publish failed.' }
$licenseRoot = Join-Path $OutputDirectory 'licenses/dotnet-runtime'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$runtimeConfig = Join-Path $root 'src/PureKBridge.App/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/ChunK Helper.runtimeconfig.json'
$frameworks = (Get-Content -LiteralPath $runtimeConfig -Raw -Encoding utf8 | ConvertFrom-Json).runtimeOptions.includedFrameworks
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($framework in $frameworks) {
    $packageName = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $package = Join-Path $packages ($packageName + '/' + $framework.version)
    foreach ($name in @('LICENSE','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
        $source = Join-Path $package $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $licenseRoot ($framework.name + '-' + $name)) -Force
        }
    }
}
Write-Host "ChunK Helper: $OutputDirectory"
