[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$nugetDirectory = Join-Path $repositoryRoot "Nuget"
$instrumentationProjectDirectory = Join-Path $repositoryRoot "Source\VBProcedureLogger.Instrumentation"
$packagesDirectory = Join-Path $instrumentationProjectDirectory "packages"
$packagesConfig = Join-Path $instrumentationProjectDirectory "packages.config"
$outputDirectory = $nugetDirectory
$stagingDirectory = Join-Path $nugetDirectory "lib"
$nuspecPath = Join-Path $nugetDirectory "VBProcedureLogger.nuspec"
$runtimeBuildOutputDirectory = Join-Path $repositoryRoot "Source\VBProcedureLogger.Runtime\bin\$Configuration"
$instrumentationBuildOutputDirectory = Join-Path $instrumentationProjectDirectory "bin\$Configuration"

function Get-NuGetExecutable {
    $command = Get-Command "nuget.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $downloadDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "VBProcedureLogger"
    $downloadPath = Join-Path $downloadDirectory "nuget.exe"
    if (-not (Test-Path -LiteralPath $downloadPath)) {
        New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
        Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $downloadPath
    }
    return $downloadPath
}

function Get-VisualStudio2026MsBuild {
    $programFiles = [Environment]::GetEnvironmentVariable("ProgramFiles")
    $programFilesX86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
    $vswhereCandidates = @(
        (Join-Path $programFiles "Microsoft Visual Studio\Installer\vswhere.exe"),
        (Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe")
    )

    foreach ($vswhere in $vswhereCandidates) {
        if (Test-Path -LiteralPath $vswhere) {
            $installationPath = & $vswhere -version "[18.0,19.0)" -products * -requires Microsoft.Component.MSBuild -property installationPath -latest
            if (-not [string]::IsNullOrWhiteSpace($installationPath)) {
                $candidate = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
                if (Test-Path -LiteralPath $candidate) {
                    return $candidate
                }
            }
        }
    }

    $fallback = Join-Path $programFiles "Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path -LiteralPath $fallback) {
        return $fallback
    }

    throw "Visual Studio 2026 MSBuild was not found."
}

function Invoke-CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @ArgumentList
    }
    finally {
        Pop-Location
    }

    if ($LASTEXITCODE -ne 0) {
        throw ("Command failed with exit code {0}: {1}" -f $LASTEXITCODE, $FilePath)
    }
}

$nuget = Get-NuGetExecutable
$msbuild = Get-VisualStudio2026MsBuild

Write-Host "Restoring instrumentation dependencies..."
Invoke-CheckedProcess -FilePath $nuget -ArgumentList @("restore", $packagesConfig, "-PackagesDirectory", $packagesDirectory, "-NonInteractive") -WorkingDirectory $instrumentationProjectDirectory

Write-Host "Building runtime with Visual Studio 2026 MSBuild..."
Invoke-CheckedProcess -FilePath $msbuild -ArgumentList @($($repositoryRoot + "\Source\VBProcedureLogger.Runtime\VBProcedureLogger.Runtime.vbproj"), "/t:Rebuild", "/p:Configuration=$Configuration", "/m", "/v:minimal") -WorkingDirectory $repositoryRoot

Write-Host "Building instrumentation with Visual Studio 2026 MSBuild..."
Invoke-CheckedProcess -FilePath $msbuild -ArgumentList @($($repositoryRoot + "\Source\VBProcedureLogger.Instrumentation\VBProcedureLogger.Instrumentation.vbproj"), "/t:Rebuild", "/p:Configuration=$Configuration", "/m", "/v:minimal") -WorkingDirectory $repositoryRoot

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

$runtimeAssemblyPath = Join-Path $runtimeBuildOutputDirectory "VBProcedureLogger.Runtime.dll"
$runtimeDocumentationPath = Join-Path $runtimeBuildOutputDirectory "VBProcedureLogger.Runtime.xml"
$instrumentationAssemblyPaths = @(
    Get-ChildItem -LiteralPath $instrumentationBuildOutputDirectory -Filter "*.dll" -File |
        Where-Object { $_.Name -ne "VBProcedureLogger.Runtime.dll" }
)

foreach ($requiredArtifact in @($runtimeAssemblyPath, $runtimeDocumentationPath)) {
    if (-not (Test-Path -LiteralPath $requiredArtifact)) {
        throw "Expected build artifact was not found: $requiredArtifact"
    }
}

if ($instrumentationAssemblyPaths.Count -eq 0) {
    throw "No instrumentation assemblies were found in: $instrumentationBuildOutputDirectory"
}

Write-Host "Copying build artifacts to the NuGet staging folder..."
Get-ChildItem -LiteralPath $stagingDirectory -Filter "*.dll" -File -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item -LiteralPath $runtimeAssemblyPath -Destination $stagingDirectory -Force
Copy-Item -LiteralPath $runtimeDocumentationPath -Destination $stagingDirectory -Force
Copy-Item -LiteralPath $instrumentationAssemblyPaths.FullName -Destination $stagingDirectory -Force

$packagePath = Join-Path $outputDirectory ("VBProcedureLogger." + $Version + ".nupkg")
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

Write-Host "Creating NuGet package..."
Invoke-CheckedProcess -FilePath $nuget -ArgumentList @("pack", $nuspecPath, "-Version", $Version, "-Properties", "configuration=$Configuration", "-BasePath", $nugetDirectory, "-OutputDirectory", $outputDirectory, "-NonInteractive") -WorkingDirectory $repositoryRoot

if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "NuGet did not produce the expected package: $packagePath"
}

Write-Host "Package created: $packagePath"
