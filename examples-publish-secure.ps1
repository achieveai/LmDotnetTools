# Example script showing secure ways to publish NuGet packages
# DO NOT COMMIT THIS FILE WITH REAL API KEYS

# Method 1: Use environment variable
$env:NUGET_API_KEY = "your-api-key-here"
.\publish-nuget-packages.ps1 -ApiKey $env:NUGET_API_KEY

# Method 2: Read from secure prompt
$apiKey = Read-Host "Enter NuGet API Key" -AsSecureString | ConvertFrom-SecureString -AsPlainText
.\publish-nuget-packages.ps1 -ApiKey $apiKey

# Method 3: Read from external file (add file to .gitignore)
$apiKey = Get-Content "nuget-api-key.txt" -Raw
.\publish-nuget-packages.ps1 -ApiKey $apiKey.Trim()

# Method 4: Dry run to test without publishing
.\publish-nuget-packages.ps1 -ApiKey "dummy-key" -DryRun

# Method 5: Use with Azure DevOps pipeline variable
# .\publish-nuget-packages.ps1 -ApiKey $env:NUGET_API_KEY_SECRET
