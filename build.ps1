param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\AppleMusicPresence\AppleMusicPresence.csproj'
$tests = Join-Path $PSScriptRoot 'tests\AppleMusicPresence.Tests\AppleMusicPresence.Tests.csproj'
& $Dotnet run --project $tests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
& $Dotnet publish $project -c Release -o (Join-Path $PSScriptRoot 'artifacts\portable')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $PSScriptRoot 'artifacts\portable\README.md')
Write-Host ('Built: ' + (Join-Path $PSScriptRoot 'artifacts\portable\AppleMusicPresence.exe'))
