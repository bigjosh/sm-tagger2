[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validationDotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$validationDotnet = if ($validationDotnetCommand) { $validationDotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $validationDotnet)) { throw 'Install the SDK pinned in global.json.' }

# Exercise the same build, formatting, packaged executables, and contracts used for local release validation.
Push-Location -LiteralPath $validationRoot
try {
    & $validationDotnet restore SmTagger.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & $validationDotnet build SmTagger.slnx --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & $validationDotnet format SmTagger.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }
    & (Join-Path $PSScriptRoot 'Publish-Release.ps1')
    & $validationDotnet test SmTagger.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=local-contracts.trx' --results-directory artifacts\test-results --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Contract or release executable tests failed.' }
}
finally {
    Pop-Location
}
