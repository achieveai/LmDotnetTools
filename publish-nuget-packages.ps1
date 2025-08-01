# PowerShell script to build and publish all NuGet packages
# This script reads the version from Directory.Build.props automatically
#
# Usage:
#   .\publish-nuget-packages.ps1 -ApiKey "your-nuget-api-key"
#   .\publish-nuget-packages.ps1 -ApiKey $env:NUGET_API_KEY
#   .\publish-nuget-packages.ps1 -ApiKey (Read-Host "Enter API Key" -AsSecureString | ConvertFrom-SecureString -AsPlainText)
#
# Parameters:
#   -ApiKey      : Your NuGet API key (required)
#   -SkipBuild   : Skip the build step and only publish existing packages
#   -DryRun      : Show what would be published without actually publishing

param(
    [Parameter(Mandatory = $true)]
    [string]$ApiKey,
    
    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,
    
    [Parameter(Mandatory = $false)]
    [switch]$DryRun
)

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

$projects = @(
    "src/LmCore/AchieveAi.LmDotnetTools.LmCore.csproj",
    "src/LmConfig/AchieveAi.LmDotnetTools.LmConfig.csproj", 
    "src/LmEmbeddings/AchieveAi.LmDotnetTools.LmEmbeddings.csproj",
    "src/AnthropicProvider/AchieveAi.LmDotnetTools.AnthropicProvider.csproj",
    "src/OpenAIProvider/AchieveAi.LmDotnetTools.OpenAIProvider.csproj",
    "src/McpMiddleware/AchieveAi.LmDotnetTools.McpMiddleware.csproj",
    "src/McpSampleServer/AchieveAi.LmDotnetTools.McpSampleServer.csproj",
    "src/Misc/AchieveAi.LmDotnetTools.Misc.csproj"
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
        } else {
            Write-Host "  Publishing: $($nupkgPath.FullName)" -ForegroundColor Yellow
            dotnet nuget push $nupkgPath.FullName --api-key $ApiKey --source https://api.nuget.org/v3/index.json
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Successfully published $projectName v$currentVersion" -ForegroundColor Green
            } else {
                Write-Host "  ✗ Failed to publish $projectName" -ForegroundColor Red
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
    Write-Host "Packages should be available on NuGet.org within a few minutes" -ForegroundColor Yellow
}
