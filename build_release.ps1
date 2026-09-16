<#
.SYNOPSIS
    Builds, publishes, and packages releases for CyberpunkTerminalWidget for GitHub.

.DESCRIPTION
    Compiles both:
      1. Standalone / Self-Contained single-file Windows x64 binary (runs standalone without requiring .NET 8 runtime)
      2. Framework-Dependent single-file Windows x64 binary (ultra-compact, requires .NET 8 runtime)
    Packages both into release zip archives with documentation, icon assets, and computes SHA256 checksums.

.PARAMETER Version
    The release version tag (e.g., '1.0.0' or 'v1.0.0'). Defaults to '1.0.0'.
#>

[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$cleanVersion = $Version.TrimStart('v')
$tagName = "v$cleanVersion"
$rootDir = $PSScriptRoot
$publishDir = Join-Path $rootDir "publish"
$releaseDir = Join-Path $rootDir "release"

$frameworkDepPublishDir = Join-Path $publishDir "framework-dependent"
$standalonePublishDir = Join-Path $publishDir "standalone"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  CyberpunkTerminalWidget Release Builder - $tagName" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Clean previous directories
Write-Host "`n[1/5] Preparing output directories..." -ForegroundColor Yellow
if (Test-Path $publishDir) { Remove-Item -Path $publishDir -Recurse -Force }
if (Test-Path $releaseDir) { Remove-Item -Path $releaseDir -Recurse -Force }

New-Item -ItemType Directory -Force -Path $frameworkDepPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $standalonePublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

# 1. Publish Standalone (Self-Contained)
Write-Host "`n[2/5] Publishing Standalone (Self-Contained) win-x64 Release..." -ForegroundColor Green
dotnet publish "$rootDir\CyberpunkTerminalWidget.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$cleanVersion `
    -p:AssemblyVersion="$cleanVersion.0" `
    -p:FileVersion="$cleanVersion.0" `
    -o $standalonePublishDir

# 2. Publish Framework-Dependent
Write-Host "`n[3/5] Publishing Framework-Dependent win-x64 Release..." -ForegroundColor Green
dotnet publish "$rootDir\CyberpunkTerminalWidget.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$cleanVersion `
    -p:AssemblyVersion="$cleanVersion.0" `
    -p:FileVersion="$cleanVersion.0" `
    -o $frameworkDepPublishDir

# Copy documentation and assets into publish folders
Write-Host "`n[4/5] Copying branding and documentation assets..." -ForegroundColor Green
$assets = @("README.md", "LICENSE", "icon.png")
foreach ($asset in $assets) {
    $src = Join-Path $rootDir $asset
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $frameworkDepPublishDir -Force
        Copy-Item -Path $src -Destination $standalonePublishDir -Force
    }
}

# 3. Create Zip Packages
Write-Host "`n[5/5] Compressing Release Archives & Computing Checksums..." -ForegroundColor Green
$frameworkDepZip = Join-Path $releaseDir "CyberpunkTerminalWidget-$tagName-win-x64-FrameworkDependent.zip"
$standaloneZip = Join-Path $releaseDir "CyberpunkTerminalWidget-$tagName-win-x64-Standalone.zip"

Compress-Archive -Path "$frameworkDepPublishDir\*" -DestinationPath $frameworkDepZip -Force
Compress-Archive -Path "$standalonePublishDir\*" -DestinationPath $standaloneZip -Force

# Copy standalone exe to release root for direct download convenience
Copy-Item -Path "$standalonePublishDir\CyberpunkTerminalWidget.exe" -Destination "$releaseDir\CyberpunkTerminalWidget.exe" -Force

# Generate SHA256 Checksums
$checksumFile = Join-Path $releaseDir "SHA256SUMS.txt"
if (Test-Path $checksumFile) { Remove-Item $checksumFile -Force }

Get-ChildItem -Path "$releaseDir\*.zip", "$releaseDir\*.exe" | ForEach-Object {
    $hash = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLower()
    $line = "$hash  $($_.Name)"
    $line | Out-File -FilePath $checksumFile -Append -Encoding ascii
    Write-Host "  $line" -ForegroundColor Gray
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "  Full Release Build Complete!" -ForegroundColor Cyan
Write-Host "  Publish Folders:" -ForegroundColor White
Write-Host "    - $frameworkDepPublishDir" -ForegroundColor Gray
Write-Host "    - $standalonePublishDir" -ForegroundColor Gray
Write-Host "  Release Archives ($releaseDir):" -ForegroundColor White
Get-ChildItem -Path $releaseDir | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host "    - $($_.Name) ($sizeMB MB)" -ForegroundColor Yellow
}
Write-Host "==========================================================" -ForegroundColor Cyan

