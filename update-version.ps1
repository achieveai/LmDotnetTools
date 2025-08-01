# Version Update Script for LmDotnetTools
# This script helps update versions across all packages from the centralized Directory.Build.props

param(
    [Parameter(Mandatory = $false)]
    [string]$NewVersion,
    
    [Parameter(Mandatory = $false)]
    [string]$PreRelease = "",
    
    [Parameter(Mandatory = $false)]
    [switch]$ShowCurrent,
    
    [Parameter(Mandatory = $false)]
    [switch]$Help
)

function Show-Help {
    Write-Host "LmDotnetTools Version Update Script" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\update-version.ps1 -NewVersion '1.2.0'                 # Update to stable version 1.2.0"
    Write-Host "  .\update-version.ps1 -NewVersion '1.2.0' -PreRelease 'alpha'  # Update to 1.2.0-alpha"
    Write-Host "  .\update-version.ps1 -ShowCurrent                        # Show current version"
    Write-Host ""
    Write-Host "Parameters:" -ForegroundColor Yellow
    Write-Host "  -NewVersion   : New version in format 'major.minor.patch' (e.g., '1.2.0')"
    Write-Host "  -PreRelease   : Pre-release label (e.g., 'alpha', 'beta', 'rc.1')"
    Write-Host "  -ShowCurrent  : Display current version information"
    Write-Host "  -Help         : Show this help message"
    Write-Host ""
    Write-Host "Examples:" -ForegroundColor Yellow
    Write-Host "  .\update-version.ps1 -NewVersion '1.0.1'                 # Bug fix release"
    Write-Host "  .\update-version.ps1 -NewVersion '1.1.0'                 # Minor feature release"
    Write-Host "  .\update-version.ps1 -NewVersion '2.0.0'                 # Major breaking changes"
    Write-Host "  .\update-version.ps1 -NewVersion '1.1.0' -PreRelease 'beta.2'  # Beta release"
}

function Get-CurrentVersion {
    $propsFile = "Directory.Build.props"
    if (!(Test-Path $propsFile)) {
        Write-Error "Directory.Build.props not found!"
        return $null
    }
    
    $content = Get-Content $propsFile -Raw
    
    $major = if ($content -match '<MajorVersion>(\d+)</MajorVersion>') { $matches[1] } else { "?" }
    $minor = if ($content -match '<MinorVersion>(\d+)</MinorVersion>') { $matches[1] } else { "?" }
    $patch = if ($content -match '<PatchVersion>(\d+)</PatchVersion>') { $matches[1] } else { "?" }
    $preRelease = if ($content -match '<PreReleaseLabel>(.*?)</PreReleaseLabel>') { $matches[1] } else { "" }
    
    $currentVersion = "$major.$minor.$patch"
    if ($preRelease -ne "") {
        $currentVersion += "-$preRelease"
    }
    
    return @{
        Major = $major
        Minor = $minor  
        Patch = $patch
        PreRelease = $preRelease
        FullVersion = $currentVersion
    }
}

function Update-Version {
    param(
        [string]$Version,
        [string]$PreReleaseLabel
    )
    
    $propsFile = "Directory.Build.props"
    if (!(Test-Path $propsFile)) {
        Write-Error "Directory.Build.props not found!"
        return $false
    }
    
    # Parse version
    if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
        Write-Error "Invalid version format. Use 'major.minor.patch' (e.g., '1.2.0')"
        return $false
    }
    
    $major = $matches[1]
    $minor = $matches[2]
    $patch = $matches[3]
    
    # Read and update content
    $content = Get-Content $propsFile -Raw
    
    $content = $content -replace '<MajorVersion>\d+</MajorVersion>', "<MajorVersion>$major</MajorVersion>"
    $content = $content -replace '<MinorVersion>\d+</MinorVersion>', "<MinorVersion>$minor</MinorVersion>"
    $content = $content -replace '<PatchVersion>\d+</PatchVersion>', "<PatchVersion>$patch</PatchVersion>"
    $content = $content -replace '<PreReleaseLabel>.*?</PreReleaseLabel>', "<PreReleaseLabel>$PreReleaseLabel</PreReleaseLabel>"
    
    Set-Content $propsFile $content -Encoding UTF8
    
    $fullVersion = "$Version"
    if ($PreReleaseLabel -ne "") {
        $fullVersion += "-$PreReleaseLabel"
    }
    
    Write-Host "✓ Updated version to: $fullVersion" -ForegroundColor Green
    
    return $true
}

# Main script logic
if ($Help) {
    Show-Help
    exit 0
}

if ($ShowCurrent) {
    $current = Get-CurrentVersion
    if ($current) {
        Write-Host "Current Version Information:" -ForegroundColor Cyan
        Write-Host "  Major: $($current.Major)" -ForegroundColor Yellow
        Write-Host "  Minor: $($current.Minor)" -ForegroundColor Yellow
        Write-Host "  Patch: $($current.Patch)" -ForegroundColor Yellow
        Write-Host "  Pre-release: $($current.PreRelease)" -ForegroundColor Yellow
        Write-Host "  Full Version: $($current.FullVersion)" -ForegroundColor Green
    }
    exit 0
}

if (!$NewVersion) {
    Write-Host "Error: NewVersion parameter is required" -ForegroundColor Red
    Write-Host ""
    Show-Help
    exit 1
}

# Show current version
$current = Get-CurrentVersion
if ($current) {
    Write-Host "Current version: $($current.FullVersion)" -ForegroundColor Yellow
}

# Update version
$success = Update-Version -Version $NewVersion -PreReleaseLabel $PreRelease

if ($success) {
    $newCurrent = Get-CurrentVersion
    Write-Host ""
    Write-Host "Version Update Summary:" -ForegroundColor Cyan
    Write-Host "  Previous: $($current.FullVersion)" -ForegroundColor Yellow
    Write-Host "  Updated:  $($newCurrent.FullVersion)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Update CHANGELOG.md with new version details"
    Write-Host "  2. Build and test: dotnet build"
    Write-Host "  3. Publish packages: .\publish-nuget-packages.ps1"
    Write-Host ""
} else {
    Write-Host "Failed to update version" -ForegroundColor Red
    exit 1
}
