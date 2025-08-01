# PowerShell script to update NuGet metadata in .csproj files
# This script adds specific PackageId, Description, and PackageTags to each project

$projects = @{
    "src\LmConfig\AchieveAi.LmDotnetTools.LmConfig.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.LmConfig"
        Description = "Configuration management library for language models, providing flexible configuration loading and validation."
        Tags = "`$(PackageTags);config;configuration;settings"
    }
    "src\LmEmbeddings\AchieveAi.LmDotnetTools.LmEmbeddings.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.LmEmbeddings"
        Description = "Embeddings support for language models, providing vector operations and similarity calculations."
        Tags = "`$(PackageTags);embeddings;vectors;similarity"
    }
    "src\AnthropicProvider\AchieveAi.LmDotnetTools.AnthropicProvider.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.AnthropicProvider"
        Description = "Anthropic AI provider for the LmDotnetTools library, enabling integration with Claude and other Anthropic models."
        Tags = "`$(PackageTags);anthropic;claude;provider"
    }
    "src\OpenAIProvider\AchieveAi.LmDotnetTools.OpenAIProvider.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.OpenAIProvider"
        Description = "OpenAI provider for the LmDotnetTools library, enabling integration with GPT models and OpenAI services."
        Tags = "`$(PackageTags);openai;gpt;provider"
    }
    "src\McpMiddleware\AchieveAi.LmDotnetTools.McpMiddleware.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.McpMiddleware"
        Description = "Model Context Protocol (MCP) middleware for integrating with MCP servers and handling context management."
        Tags = "`$(PackageTags);mcp;middleware;context"
    }
    "src\McpSampleServer\AchieveAi.LmDotnetTools.McpSampleServer.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.McpSampleServer"
        Description = "Sample MCP server implementation demonstrating Model Context Protocol integration patterns."
        Tags = "`$(PackageTags);mcp;server;sample;demo"
    }
    "src\Misc\AchieveAi.LmDotnetTools.Misc.csproj" = @{
        PackageId = "AchieveAi.LmDotnetTools.Misc"
        Description = "Miscellaneous utilities and helper functions for the LmDotnetTools library ecosystem."
        Tags = "`$(PackageTags);utilities;helpers;misc"
    }
}

foreach ($projectPath in $projects.Keys) {
    $metadata = $projects[$projectPath]
    $fullPath = Join-Path $PSScriptRoot $projectPath
    
    if (Test-Path $fullPath) {
        Write-Host "Updating $projectPath..." -ForegroundColor Green
        
        # Read the current content
        $content = Get-Content $fullPath -Raw
        
        # Find the PropertyGroup with TargetFrameworks
        $pattern = '(<PropertyGroup>\s*<TargetFrameworks>.*?</TargetFrameworks>\s*<ImplicitUsings>.*?</ImplicitUsings>\s*<Nullable>.*?</Nullable>\s*</PropertyGroup>)'
        
        if ($content -match $pattern) {
            $newPropertyGroup = @"
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    
    <!-- NuGet Package Metadata -->
    <PackageId>$($metadata.PackageId)</PackageId>
    <Description>$($metadata.Description)</Description>
    <PackageTags>$($metadata.Tags)</PackageTags>
  </PropertyGroup>
"@
            
            $newContent = $content -replace $pattern, $newPropertyGroup
            Set-Content $fullPath $newContent -Encoding UTF8
            Write-Host "  ✓ Updated successfully" -ForegroundColor Yellow
        } else {
            Write-Host "  ✗ Could not find PropertyGroup pattern in $projectPath" -ForegroundColor Red
        }
    } else {
        Write-Host "  ✗ File not found: $fullPath" -ForegroundColor Red
    }
}

Write-Host "`nAll projects updated! You can now build and publish the packages." -ForegroundColor Cyan
