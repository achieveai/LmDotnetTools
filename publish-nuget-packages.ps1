# PowerShell script to build and publish all NuGet packages
# This script reads the version from Directory.Build.props automatically
#
# Usage:
#   .\publish-nuget-packages.ps1 -ApiKey "your-nuget-api-key"
#   .\publish-nuget-packages.ps1 -ApiKey $env:NUGET_API_KEY
#   .\publish-nuget-packages.ps1 -ApiKey (Read-Host "Enter API Key" -AsSecureString | ConvertFrom-SecureString -AsPlainText)
#   .\publish-nuget-packages.ps1 -LocalOnly
#   .\publish-nuget-packages.ps1 -LocalFeed -ApiKey "your-api-key"
#   .\publish-nuget-packages.ps1 -LocalFeed -LocalFeedPath "C:\MyLocalFeed"
#
# Parameters:
#   -ApiKey        : Your NuGet API key (required unless -LocalOnly is used)
#   -SkipBuild     : Skip the build step and only publish existing packages
#   -DryRun        : Show what would be published without actually publishing
#   -LocalFeed     : Also publish to local NuGet feed
#   -LocalOnly     : Publish only to local feed (makes ApiKey optional)
#   -LocalFeedPath : Path to local NuGet feed (default: %USERPROFILE%/.nuget/local-feed)

param(
    [Parameter(Mandatory = $false)]
    [string]$ApiKey,
    
    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,
    
    [Parameter(Mandatory = $false)]
    [switch]$DryRun,
    
    [Parameter(Mandatory = $false)]
    [switch]$LocalFeed,
    
    [Parameter(Mandatory = $false)]
    [switch]$LocalOnly,
    
    [Parameter(Mandatory = $false)]
    [string]$LocalFeedPath = "$env:USERPROFILE\.nuget\local-feed"
)

# Validate parameters
if (!$LocalOnly -and !$ApiKey) {
    Write-Error "ApiKey is required unless -LocalOnly is specified"
    exit 1
}

if ($LocalOnly) {
    $LocalFeed = $true
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
    
    $version = "$major.$minor.$patch"
    if ($preRelease -ne "") {
        $version += "-$preRelease"
    }
    
    return $version
}

function Ensure-LocalFeedDirectory {
    param([string]$Path)
    
    if (!(Test-Path $Path)) {
        Write-Host "Creating local feed directory: $Path" -ForegroundColor Yellow
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    
    return Test-Path $Path
}

$projects = @(
    "src/LmCore/AchieveAi.LmDotnetTools.LmCore.csproj",
    "src/LmConfig/AchieveAi.LmDotnetTools.LmConfig.csproj",
    "src/LmEmbeddings/AchieveAi.LmDotnetTools.LmEmbeddings.csproj",
    "src/AnthropicProvider/AchieveAi.LmDotnetTools.AnthropicProvider.csproj",
    "src/OpenAIProvider/AchieveAi.LmDotnetTools.OpenAIProvider.csproj",
    "src/McpMiddleware/AchieveAi.LmDotnetTools.McpMiddleware.csproj",
    "src/McpSampleServer/AchieveAi.LmDotnetTools.McpSampleServer.csproj",
    "src/Misc/AchieveAi.LmDotnetTools.Misc.csproj",
    "src/ClaudeAgentSdkProvider/AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.csproj",
    "src/LmMultiTurn/AchieveAi.LmDotnetTools.LmMultiTurn.csproj"
)

# Get current version from Directory.Build.props
$currentVersion = Get-CurrentVersion
if (!$currentVersion) {
    Write-Host "Failed to get current version!" -ForegroundColor Red
    exit 1
}

Write-Host "NuGet Package Build and Publish Process" -ForegroundColor Cyan
Write-Host "Version: $currentVersion" -ForegroundColor Green
Write-Host "Packages: $($projects.Count)" -ForegroundColor Green

if ($LocalFeed) {
    Write-Host "Local Feed: $LocalFeedPath" -ForegroundColor Green
    if (!(Ensure-LocalFeedDirectory $LocalFeedPath)) {
        Write-Host "Failed to create local feed directory!" -ForegroundColor Red
        exit 1
    }
}

if ($LocalOnly) {
    Write-Host "Mode: Local Only" -ForegroundColor Yellow
} elseif ($LocalFeed) {
    Write-Host "Mode: NuGet.org + Local Feed" -ForegroundColor Yellow
} else {
    Write-Host "Mode: NuGet.org Only" -ForegroundColor Yellow
}

if ($DryRun) {
    Write-Host "DRY RUN MODE - No packages will be published" -ForegroundColor Yellow
}

Write-Host ""

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    Write-Host "Processing: $projectName" -ForegroundColor Green
    
    if (!$SkipBuild) {
        # Build and pack
        Write-Host "  Building and packing..." -ForegroundColor Yellow
        dotnet pack $project -c Release --include-symbols --include-source
        
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ Failed to pack $projectName" -ForegroundColor Red
            continue
        }
    }
    
    # Find the generated .nupkg file
    $projectDir = [System.IO.Path]::GetDirectoryName($project)
    $nupkgPath = Get-ChildItem -Path "$projectDir/bin/Release" -Filter "*.nupkg" | Where-Object { $_.Name -notlike "*symbols*" } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    
    if ($nupkgPath) {
        if ($DryRun) {
            Write-Host "  [DRY RUN] Would publish: $($nupkgPath.FullName)" -ForegroundColor Cyan
            if ($LocalFeed) {
                Write-Host "  [DRY RUN] Would publish to local feed: $LocalFeedPath" -ForegroundColor Cyan
            }
            if (!$LocalOnly) {
                Write-Host "  [DRY RUN] Would publish to NuGet.org" -ForegroundColor Cyan
            }
        } else {
            $publishSuccess = $true
            
            # Publish to local feed first if enabled
            if ($LocalFeed) {
                Write-Host "  Publishing to local feed: $LocalFeedPath" -ForegroundColor Yellow
                dotnet nuget push $nupkgPath.FullName --source $LocalFeedPath --skip-duplicate
                
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  ✓ Successfully published $projectName v$currentVersion to local feed" -ForegroundColor Green
                } else {
                    Write-Host "  ✗ Failed to publish $projectName to local feed" -ForegroundColor Red
                    $publishSuccess = $false
                }
            }
            
            # Publish to NuGet.org if not local-only
            if (!$LocalOnly) {
                Write-Host "  Publishing to NuGet.org..." -ForegroundColor Yellow
                dotnet nuget push $nupkgPath.FullName --api-key $ApiKey --source https://api.nuget.org/v3/index.json
                
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "  ✓ Successfully published $projectName v$currentVersion to NuGet.org" -ForegroundColor Green
                } else {
                    Write-Host "  ✗ Failed to publish $projectName to NuGet.org" -ForegroundColor Red
                    $publishSuccess = $false
                }
            }
            
            if (!$publishSuccess) {
                Write-Host "  ⚠ Some publishing operations failed for $projectName" -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host "  ✗ Could not find .nupkg file for $projectName" -ForegroundColor Red
    }
    
    Write-Host ""
}

if ($DryRun) {
    Write-Host "DRY RUN completed - No packages were published" -ForegroundColor Yellow
} else {
    Write-Host "NuGet publishing process completed!" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Published version: $currentVersion" -ForegroundColor Green
    
    if ($LocalFeed) {
        Write-Host "Local feed: $LocalFeedPath" -ForegroundColor Green
    }
    if (!$LocalOnly) {
        Write-Host "Packages should be available on NuGet.org within a few minutes" -ForegroundColor Yellow
    }
    if ($LocalOnly) {
        Write-Host "Packages are immediately available in your local feed" -ForegroundColor Green
    }
}
