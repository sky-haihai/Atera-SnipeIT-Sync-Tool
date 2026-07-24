[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.1',

    [Parameter()]
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assemblyVersion = '1.0.0.0'
$fileVersion = "$Version.0"

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$CandidatePath
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\') + '\'
    $resolvedCandidate = [System.IO.Path]::GetFullPath($CandidatePath)
    if (-not $resolvedCandidate.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $resolvedCandidate"
    }

    return $resolvedCandidate
}

function Reset-ArtifactDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $safePath = Assert-ChildPath -RepositoryRoot $RepositoryRoot -CandidatePath $Path
    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
    return $safePath
}

function Convert-GuidToNetworkBytes {
    param([Parameter(Mandatory)][Guid]$Guid)

    $bytes = $Guid.ToByteArray()
    [Array]::Reverse($bytes, 0, 4)
    [Array]::Reverse($bytes, 4, 2)
    [Array]::Reverse($bytes, 6, 2)
    return $bytes
}

function New-DeterministicProductCode {
    param(
        [Parameter(Mandatory)]
        [Guid]$Namespace,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $namespaceBytes = Convert-GuidToNetworkBytes -Guid $Namespace
    $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($Name)
    $inputBytes = New-Object byte[] ($namespaceBytes.Length + $nameBytes.Length)
    [Array]::Copy($namespaceBytes, 0, $inputBytes, 0, $namespaceBytes.Length)
    [Array]::Copy($nameBytes, 0, $inputBytes, $namespaceBytes.Length, $nameBytes.Length)

    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        $hash = $sha1.ComputeHash($inputBytes)
    }
    finally {
        $sha1.Dispose()
    }

    $uuidBytes = New-Object byte[] 16
    [Array]::Copy($hash, $uuidBytes, 16)
    $uuidBytes[6] = ($uuidBytes[6] -band 0x0F) -bor 0x50
    $uuidBytes[8] = ($uuidBytes[8] -band 0x3F) -bor 0x80
    [Array]::Reverse($uuidBytes, 0, 4)
    [Array]::Reverse($uuidBytes, 4, 2)
    [Array]::Reverse($uuidBytes, 6, 2)
    return ([Guid]::new($uuidBytes)).ToString('B').ToUpperInvariant()
}

function Merge-PublishDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,

        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    foreach ($sourceFile in Get-ChildItem -LiteralPath $SourceRoot -File -Recurse) {
        $relativePath = $sourceFile.FullName.Substring($SourceRoot.Length).TrimStart('\')
        $destinationPath = Join-Path $DestinationRoot $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

        if (Test-Path -LiteralPath $destinationPath) {
            $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
            $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
            if ($sourceHash -ne $destinationHash) {
                throw "Publish merge collision has different content: $relativePath"
            }

            continue
        }

        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath
    }
}

function Assert-ReleaseArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$MsiPath,

        [Parameter(Mandatory)]
        [string]$ChecksumPath,

        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [Parameter(Mandatory)]
        [string]$ExpectedCommit,

        [Parameter(Mandatory)]
        [string]$ExpectedProductCode,

        [Parameter(Mandatory)]
        [bool]$ExpectedDirty
    )

    $expectedAssemblyVersion = '1.0.0.0'
    $expectedFileVersion = "$Version.0"

    $validationRoot = Reset-ArtifactDirectory `
        -RepositoryRoot $RepositoryRoot `
        -Path (Join-Path $RepositoryRoot "artifacts\.validation\v$Version")
    $administrativeLog = Join-Path $validationRoot 'administrative-extraction.log'
    $msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
    $arguments = @(
        '/a',
        "`"$MsiPath`"",
        "TARGETDIR=`"$validationRoot`"",
        '/qn',
        '/norestart',
        '/L*v',
        "`"$administrativeLog`""
    )
    $process = Start-Process `
        -FilePath $msiExec `
        -ArgumentList $arguments `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "MSI administrative extraction failed with exit code $($process.ExitCode). See $administrativeLog"
    }

    $workerMatches = @(Get-ChildItem -LiteralPath $validationRoot -File -Recurse -Filter 'AteraSnipeSync.WorkerService.exe')
    $trayMatches = @(Get-ChildItem -LiteralPath $validationRoot -File -Recurse -Filter 'AteraSnipeSync.TrayApp.exe')
    if ($workerMatches.Count -ne 1 -or $trayMatches.Count -ne 1) {
        throw 'Administrative extraction must contain exactly one Worker executable and one Tray executable.'
    }

    $workerExecutable = $workerMatches[0]
    $trayExecutable = $trayMatches[0]
    if ($workerExecutable.DirectoryName -ne $trayExecutable.DirectoryName) {
        throw 'The extracted Worker and Tray executables must be in the same directory.'
    }

    foreach ($executable in @($workerExecutable, $trayExecutable)) {
        if ($executable.VersionInfo.ProductVersion -ne $Version -or
            $executable.VersionInfo.FileVersion -ne $expectedFileVersion -or
            $executable.VersionInfo.CompanyName -ne 'Vue IT Inc.') {
            throw "Unexpected version/company metadata in $($executable.Name)."
        }
    }

    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($trayExecutable.FullName)
    try {
        if ($null -eq $icon -or $icon.Width -lt 16 -or $icon.Height -lt 16) {
            throw 'The extracted Tray executable does not expose a usable associated icon.'
        }
    }
    finally {
        if ($null -ne $icon) {
            $icon.Dispose()
        }
    }

    $applicationRoot = $workerExecutable.DirectoryName
    $forbiddenFiles = @(Get-ChildItem -LiteralPath $applicationRoot -File -Recurse | Where-Object {
        $_.Extension -ieq '.pdb' -or
        $_.Name -ieq 'appsettings.Development.json' -or
        $_.Name -ieq 'appsettings.local.json' -or
        $_.Name -like '*.local.json' -or
        $_.Name -ieq 'secrets.json' -or
        $_.Name -like '*Tests.dll'
    })
    if ($forbiddenFiles.Count -ne 0) {
        throw "Forbidden files were found in the extracted MSI: $($forbiddenFiles.FullName -join ', ')"
    }

    $actualHash = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedChecksumLine = "$actualHash *$([System.IO.Path]::GetFileName($MsiPath))"
    if ((Get-Content -LiteralPath $ChecksumPath -Raw).Trim() -ne $expectedChecksumLine) {
        throw 'The SHA-256 sidecar does not match the MSI.'
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $Version -or
        $manifest.assemblyVersion -ne $expectedAssemblyVersion -or
        $manifest.fileVersion -ne $expectedFileVersion -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or
        -not $manifest.selfContained -or
        $manifest.manufacturer -ne 'Vue IT Inc.' -or
        $manifest.productCode -ne $ExpectedProductCode -or
        $manifest.commit -ne $ExpectedCommit -or
        [bool]$manifest.dirty -ne $ExpectedDirty -or
        $manifest.sha256 -ne $actualHash) {
        throw 'The release manifest does not match the validated artifact or build context.'
    }
}

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'This release script supports Windows only.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'AteraSnipeSync.sln'
$workerProject = Join-Path $repositoryRoot 'src\AteraSnipeSync.WorkerService\AteraSnipeSync.WorkerService.csproj'
$trayProject = Join-Path $repositoryRoot 'src\AteraSnipeSync.TrayApp\AteraSnipeSync.TrayApp.csproj'
$installerProject = Join-Path $repositoryRoot 'installer\AteraSnipeSync.Installer\AteraSnipeSync.Installer.wixproj'
$publishBase = Join-Path $repositoryRoot "artifacts\.staging\$Version\win-x64"
$workerPublish = Join-Path $publishBase 'worker'
$trayPublish = Join-Path $publishBase 'tray'
$mergedPublish = Join-Path $publishBase 'merged'
$releaseRoot = Join-Path $repositoryRoot "artifacts\release\v$Version"

if (-not (Test-Path -LiteralPath $solutionPath) -or
    -not (Test-Path -LiteralPath $workerProject) -or
    -not (Test-Path -LiteralPath $trayProject) -or
    -not (Test-Path -LiteralPath $installerProject)) {
    throw 'One or more required release inputs are missing.'
}

$gitStatus = (& git -C $repositoryRoot status --porcelain=v1)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git working tree.'
}

$isDirty = [bool]$gitStatus
if ($isDirty -and -not $AllowDirty) {
    throw 'The working tree is dirty. Commit or stash changes, or use -AllowDirty for development verification.'
}

$workerPublish = Reset-ArtifactDirectory -RepositoryRoot $repositoryRoot -Path $workerPublish
$trayPublish = Reset-ArtifactDirectory -RepositoryRoot $repositoryRoot -Path $trayPublish
$mergedPublish = Reset-ArtifactDirectory -RepositoryRoot $repositoryRoot -Path $mergedPublish
$releaseRoot = Reset-ArtifactDirectory -RepositoryRoot $repositoryRoot -Path $releaseRoot

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', $solutionPath, '--locked-mode')
    Invoke-DotNet -Arguments @('restore', $installerProject, '--locked-mode')
    Invoke-DotNet -Arguments @('build', $solutionPath, '--configuration', 'Release', '--no-restore')
    Invoke-DotNet -Arguments @('test', $solutionPath, '--configuration', 'Release', '--no-build', '--no-restore')

    $publishProperties = @(
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--configuration', 'Release',
        '--no-restore',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugSymbols=false',
        '-p:DebugType=None',
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$fileVersion"
    )

    Invoke-DotNet -Arguments (@('publish', $workerProject, '--output', $workerPublish) + $publishProperties)
    Invoke-DotNet -Arguments (@('publish', $trayProject, '--output', $trayPublish) + $publishProperties)
}
finally {
    Pop-Location
}

Merge-PublishDirectory -SourceRoot $workerPublish -DestinationRoot $mergedPublish
Merge-PublishDirectory -SourceRoot $trayPublish -DestinationRoot $mergedPublish

$forbiddenFiles = Get-ChildItem -LiteralPath $mergedPublish -File -Recurse | Where-Object {
    $_.Extension -ieq '.pdb' -or
    $_.Name -ieq 'appsettings.Development.json' -or
    $_.Name -ieq 'appsettings.local.json' -or
    $_.Name -like '*.local.json' -or
    $_.Name -ieq 'secrets.json'
}
if ($forbiddenFiles) {
    throw "Forbidden release files were found: $($forbiddenFiles.FullName -join ', ')"
}

$workerExecutable = Join-Path $mergedPublish 'AteraSnipeSync.WorkerService.exe'
$trayExecutable = Join-Path $mergedPublish 'AteraSnipeSync.TrayApp.exe'
if (-not (Test-Path -LiteralPath $workerExecutable) -or -not (Test-Path -LiteralPath $trayExecutable)) {
    throw 'The merged publish directory must contain both application executables at its root.'
}

$productNamespace = [Guid]'AD4D8FDE-7A95-4D4E-8A44-988FAE44D807'
$productCode = New-DeterministicProductCode -Namespace $productNamespace -Name $Version
Invoke-DotNet -Arguments @(
    'build', $installerProject,
    '--configuration', 'Release',
    '--no-restore',
    "-p:Version=$Version",
    "-p:ProductCode=$productCode",
    "-p:PublishRoot=$mergedPublish",
    "-p:ReleaseOutputRoot=$releaseRoot\"
)

$msiName = "AteraSnipeSync-$Version-win-x64.msi"
$msiPath = Join-Path $releaseRoot $msiName
if (-not (Test-Path -LiteralPath $msiPath)) {
    throw "WiX did not produce the expected MSI: $msiPath"
}

$wixPdbPath = Join-Path $releaseRoot "AteraSnipeSync-$Version-win-x64.wixpdb"
if (Test-Path -LiteralPath $wixPdbPath) {
    Remove-Item -LiteralPath $wixPdbPath -Force
}

$msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$msiPath.sha256"
Set-Content -LiteralPath $checksumPath -Encoding ascii -Value "$msiHash *$msiName"

$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to read the Git commit.'
}

$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
$manifest = [ordered]@{
    product = 'Atera Snipe-IT Auto Sync'
    version = $Version
    assemblyVersion = $assemblyVersion
    fileVersion = $fileVersion
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    manufacturer = 'Vue IT Inc.'
    upgradeCode = '549B4FDF-C466-4CF0-A356-0EC6380C24CD'
    productCode = $productCode
    commit = $commit
    dirty = $isDirty
    artifact = $msiName
    sha256 = $msiHash
    unsigned = $true
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Assert-ReleaseArtifact `
    -RepositoryRoot $repositoryRoot `
    -Version $Version `
    -MsiPath $msiPath `
    -ChecksumPath $checksumPath `
    -ManifestPath $manifestPath `
    -ExpectedCommit $commit `
    -ExpectedProductCode $productCode `
    -ExpectedDirty $isDirty

Write-Host "Release artifact: $msiPath"
Write-Host "SHA-256: $msiHash"
Write-Host "Manifest: $manifestPath"
