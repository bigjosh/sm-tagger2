[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $repositoryRoot 'artifacts\release'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExecutable = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExecutable)) { throw 'Install the .NET SDK pinned in global.json before publishing.' }

# Recreate only a known ordinary output directory below this repository's release tree.
function Reset-ReleaseDirectory([string] $RelativePath) {
    $output = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot $RelativePath))
    if (-not $output.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean an output directory outside this repository release tree.'
    }
    if (Test-Path -LiteralPath $output) {
        $existing = Get-Item -LiteralPath $output -Force
        if (-not $existing.PSIsContainer -or ($existing.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw ('Release output must be an ordinary directory: ' + $output)
        }
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    return $output
}

# Select the repository SDK even when this script is called from another working directory.
Push-Location -LiteralPath $repositoryRoot
try {
    $standaloneRoot = Reset-ReleaseDirectory 'standalone'
    $singleFileBuildRoot = Reset-ReleaseDirectory '.singlefile-build'
    $runtimeVersion = $null
    # Publish fresh self-contained folders so stale deployment files cannot enter the new packages.
    foreach ($application in @(@{ Project = 'SmSorter'; Name = 'sm-sorter' }, @{ Project = 'SmTagger'; Name = 'sm-tagger' })) {
        $projectPath = Join-Path $repositoryRoot ('src\' + $application.Project + '\' + $application.Project + '.csproj')
        $outputPath = Reset-ReleaseDirectory $application.Name
        & $dotnetExecutable publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $outputPath --nologo "-p:PathMap=$repositoryRoot=/_/sm-tagger2"
        if ($LASTEXITCODE -ne 0) { throw ('Publishing ' + $application.Name + ' failed.') }

        # Carry the exact bundled runtime's license and notices into both distribution formats.
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $outputPath ($application.Name + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
        $applicationRuntime = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')[0].version
        if ($runtimeVersion -and $applicationRuntime -ne $runtimeVersion) { throw 'Release applications bundle different runtime versions.' }
        $runtimeVersion = $applicationRuntime
        $assets = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $projectPath) 'obj\project.assets.json') -Raw | ConvertFrom-Json
        $runtimePackage = $null
        foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $packageFolder ('microsoft.netcore.app.runtime.win-x64\' + $runtimeVersion)
            if (Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT')) { $runtimePackage = $candidate; break }
        }
        if (-not $runtimePackage) { throw 'Cannot locate the bundled runtime license and third-party notices.' }
        foreach ($notice in @(@{ Source = 'LICENSE.TXT'; Name = 'DOTNET-LICENSE.txt' }, @{ Source = 'THIRD-PARTY-NOTICES.TXT'; Name = 'THIRD-PARTY-NOTICES.txt' })) {
            Copy-Item -LiteralPath (Join-Path $runtimePackage $notice.Source) -Destination (Join-Path $outputPath $notice.Name)
            Copy-Item -LiteralPath (Join-Path $runtimePackage $notice.Source) -Destination (Join-Path $standaloneRoot $notice.Name) -Force
        }

        # Bundle native runtime libraries as well as managed code, without trimming or compression.
        $singleFileOutput = Join-Path $singleFileBuildRoot $application.Name
        & $dotnetExecutable publish $projectPath --configuration Release --runtime win-x64 --self-contained true --output $singleFileOutput --nologo -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:PublishTrimmed=false "-p:PathMap=$repositoryRoot=/_/sm-tagger2"
        if ($LASTEXITCODE -ne 0) { throw ('Publishing standalone ' + $application.Name + ' failed.') }
        $singleFileItems = @(Get-ChildItem -LiteralPath $singleFileOutput -File -Recurse)
        if ($singleFileItems.Count -ne 1 -or $singleFileItems[0].Name -ne ($application.Name + '.exe')) {
            throw ('Standalone publish unexpectedly requires companion files: ' + $application.Name)
        }
        Copy-Item -LiteralPath $singleFileItems[0].FullName -Destination (Join-Path $standaloneRoot $singleFileItems[0].Name)
    }

    # Hash every deployment file; the manifest itself is excluded from its own inventory.
    $hashRecords = foreach ($applicationName in @('sm-sorter', 'sm-tagger', 'standalone')) {
        $applicationRoot = Join-Path $releaseRoot $applicationName
        foreach ($releaseFile in Get-ChildItem -LiteralPath $applicationRoot -File -Recurse | Sort-Object FullName) {
            [pscustomobject]@{
                Path = $releaseFile.FullName.Substring($releaseRoot.Length + 1)
                Bytes = $releaseFile.Length
                Sha256 = (Get-FileHash -LiteralPath $releaseFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    }
    $packages = foreach ($applicationName in @('sm-sorter', 'sm-tagger')) {
        $archivePath = Join-Path $releaseRoot ($applicationName + '-win-x64.zip')
        Compress-Archive -LiteralPath (Join-Path $releaseRoot $applicationName) -DestinationPath $archivePath -Force
        [pscustomobject]@{
            Path = [System.IO.Path]::GetFileName($archivePath)
            Sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [pscustomobject]@{
        CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Sdk = (& $dotnetExecutable --version).Trim()
        Version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
        RuntimeVersion = $runtimeVersion
        RuntimeIdentifier = 'win-x64'
        SelfContained = $true
        StandaloneExecutables = @('standalone\sm-sorter.exe', 'standalone\sm-tagger.exe')
        Files = @($hashRecords)
        Packages = @($packages)
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'manifest.json') -Encoding utf8
    Write-Output ('Release folders, standalone EXEs, runtime notices, and SHA-256 manifest: ' + $releaseRoot)
}
finally {
    Pop-Location
}
