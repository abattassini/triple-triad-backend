# Run the Triple Triad backend with the REMOTE Supabase PostgreSQL database.
# Usage:  pwsh run-remote-db.ps1   (or run from VS Code terminal:  .\run-remote-db.ps1)
$here = Split-Path -Parent $PSCommandPath
Set-Location $here
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:UseInMemoryDatabase = 'false'
dotnet run --project TripleTriadApi.csproj