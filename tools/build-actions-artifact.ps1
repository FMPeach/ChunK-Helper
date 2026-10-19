#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/actions-test/artifact' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and
    @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) {
  throw 'OutputDirectory must be empty; use a new directory for each build.'
}
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Helper source commit.' }
& (Join-Path $PSScriptRoot 'publish-windows.ps1') -OutputDirectory $OutputDirectory
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'ChunK Helper.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'coreclr.dll'))) {
  throw 'Self-contained Helper publish output is incomplete.'
}
$prefix = $OutputDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
  [ordered]@{ path = $_.FullName.Substring($prefix.Length).Replace('\', '/')
    size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{
  schemaVersion = 1; component = 'chunk-helper'; target = 'windows-x64'
  source = [ordered]@{ repository = 'FMPeach/ChunK-Helper'; commit = $commit }
  build = [ordered]@{
    runId = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local' }
    runAttempt = if ($env:GITHUB_RUN_ATTEMPT) { $env:GITHUB_RUN_ATTEMPT } else { '1' }
  }
  entryPoint = 'ChunK Helper.exe'; files = $files
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'component-manifest.json'),
  ($manifest | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Helper artifact: $OutputDirectory"
