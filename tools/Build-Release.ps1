[CmdletBinding()]
param(
    [switch]$RunIntegrationTests
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'FrameLock.slnx'
$projectPath = Join-Path $repositoryRoot 'src\FrameLock.App\FrameLock.App.csproj'
$testsPath = Join-Path $repositoryRoot 'tests\FrameLock.Tests\FrameLock.Tests.csproj'
$artifactRoot = Join-Path $repositoryRoot 'artifacts'

[xml]$project = Get-Content -LiteralPath $projectPath
$version = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'FrameLock.App.csproj does not declare a Version.'
}

$releaseName = "FrameLock-$version-windows"
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot $releaseName))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "$releaseName.zip"))
$checksumPath = "$archivePath.sha256"
$artifactRootWithSeparator = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $publishDirectory.StartsWith($artifactRootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish outside the artifact directory: $publishDirectory"
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    Invoke-DotNet @('restore', $solutionPath)
    Invoke-DotNet @('build', $solutionPath, '-c', 'Release', '--no-restore')

    $testArguments = @('run', '--project', $testsPath, '-c', 'Release', '--no-build')
    if ($RunIntegrationTests) {
        $testArguments += @('--', '--integration')
    }
    Invoke-DotNet $testArguments

    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    foreach ($existingFile in @($archivePath, $checksumPath)) {
        if (Test-Path -LiteralPath $existingFile) {
            Remove-Item -LiteralPath $existingFile -Force
        }
    }

    Invoke-DotNet @(
        'publish',
        $projectPath,
        '-c', 'Release',
        '--no-restore',
        '-o', $publishDirectory
    )

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Destination $publishDirectory

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($archivePath))" |
        Set-Content -LiteralPath $checksumPath -Encoding ascii

    Write-Host ''
    Write-Host "Release folder: $publishDirectory" -ForegroundColor Green
    Write-Host "Release archive: $archivePath" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
}
finally {
    Pop-Location
}
