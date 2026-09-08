# Run the Triple Triad backend with the LOCAL in-memory database.
# Usage:  pwsh run-local-db.ps1     (or run from VS Code terminal:  .\run-local-db.ps1)
$here = Split-Path -Parent $PSCommandPath
Set-Location $here
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:UseInMemoryDatabase = 'true'
dotnet run --project TripleTriadApi.csproj