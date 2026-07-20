# Builds the Release DLL and assembles the Thunderstore package zip in dist/.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

dotnet build "$root\MonstersGordion.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$manifest = Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json
$version = $manifest.version_number

$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item "$root\manifest.json" $stage
Copy-Item "$root\README.md" $stage
Copy-Item "$root\CHANGELOG.md" $stage
Copy-Item "$root\icon.png" $stage
Copy-Item "$root\bin\Release\netstandard2.1\MonstersGordion.dll" $stage

$zip = Join-Path $root "dist\MonstersGordion-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

Remove-Item $stage -Recurse -Force
Write-Host "Thunderstore package ready: $zip"
