get-process -Name "MemoryServer" | stop-process
$originalLocation = Get-Location

$projectLocation = "d:/Source/repos/LmDotNetTools/McpServers/Memory/MemoryServer"
Set-Location $projectLocation
$env:ASPNETCORE_ENVIRONMENT = 'Development'


try {
    dotnet build
    ./bin/debug/net9.0/MemoryServer.exe
}
catch {
    Write-Host "Process interrupted or error occurred: $($_.Exception.Message)"
}
finally {
    Set-Location $originalLocation
}