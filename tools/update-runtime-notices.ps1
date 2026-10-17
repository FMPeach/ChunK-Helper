#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/PureKBridge.App/PureKBridge.App.csproj'
$raw = & dotnet msbuild $project -nologo -t:ProcessFrameworkReferences -p:SelfContained=true -p:RuntimeIdentifier=win-x64 -getItem:RuntimeFramework
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve .NET runtime versions.' }
$frameworks = ($raw -join "`n" | ConvertFrom-Json).Items.RuntimeFramework
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$output = Join-Path $root 'licenses/runtime'
New-Item -ItemType Directory -Path $output -Force | Out-Null
function WriteNotice([string]$Source, [string]$Destination) {
    [IO.File]::WriteAllText($Destination, [IO.File]::ReadAllText($Source).TrimEnd() + "`n", [Text.UTF8Encoding]::new($false))
}
foreach ($spec in @(
    @{ framework='Microsoft.NETCore.App'; name='Runtime'; license='LICENSE.TXT' },
    @{ framework='Microsoft.WindowsDesktop.App'; name='WPF'; license='LICENSE' })) {
    $version = @($frameworks | Where-Object { $_.Identity -eq $spec.framework })[0].Version
    $source = Join-Path $packages ($spec.framework.ToLowerInvariant() + '.runtime.win-x64/' + $version)
    WriteNotice (Join-Path $source $spec.license) (Join-Path $output ($spec.name + '-LICENSE.txt'))
    $notices = Join-Path $source 'THIRD-PARTY-NOTICES.TXT'
    if (Test-Path -LiteralPath $notices) {
        WriteNotice $notices (Join-Path $output ($spec.name + '-NOTICES.txt'))
    }
}
