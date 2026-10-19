#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$VersionOverride)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$config = Get-Content -LiteralPath (Join-Path $root 'config/package.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($config.schemaVersion -ne 1) { throw 'Unsupported package configuration schema.' }
foreach ($field in @('name','version','developer')) {
    if ($config.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($config.$field) -or $config.$field -match '[\r\n\x00]') {
        throw "Invalid package field: $field"
    }
}
if (-not [string]::IsNullOrWhiteSpace($VersionOverride)) { $config.version = $VersionOverride }
if ($config.version -notmatch '^[vV]?(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?(?:\+(\d+))?$') { throw 'Invalid package version; use major.minor.patch, optionally prefixed with v.' }
$numbers=@([int]$Matches[1],[int]$Matches[2],[int]$Matches[3],0)
if ($Matches.ContainsKey(4)) { $numbers[3]=[int]$Matches[4] }
if (@($numbers | Where-Object {$_ -gt 65535}).Count) { throw 'Windows version components cannot exceed 65535.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($field in @('name','version','developer','fileVersion')) {
    $text = if ($field -eq 'fileVersion') { $numbers -join '.' } else { $config.$field }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($field+'.txt')), $text, [Text.UTF8Encoding]::new($false))
}
