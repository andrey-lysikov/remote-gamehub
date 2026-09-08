#Requires -Version 5.1

$ErrorActionPreference = 'Stop'

$repo    = $PSScriptRoot
$source  = Join-Path $repo 'src'
$tests   = Join-Path $repo 'test'
$project = Join-Path $source 'RemoteGameHub.csproj'
$output  = Join-Path $repo 'build'
$exe     = Join-Path $output 'Remote-Gamehub.exe'

$projects  = @($source, $tests, (Join-Path $repo 'installer'))
$leftovers = @()

function Write-Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "  + $text" -ForegroundColor Green }
function Write-Kept($text) { Write-Host "  ! $text" -ForegroundColor Yellow }
function Show-Size($path)  {
    $bytes = (Get-Item $path).Length
    if ($bytes -lt 1MB) { "$([math]::Round($bytes / 1KB)) KB" }
    else                { "$([math]::Round($bytes / 1MB, 1)) MB" }
}

function Remove-Leftovers {
    $paths = @()
    foreach ($project in $projects) {
        $paths += (Join-Path $project 'bin'), (Join-Path $project 'obj')
    }
    $paths += $leftovers

    $kept = @()

    foreach ($path in $paths) {
        if (-not (Test-Path $path)) { continue }

        for ($attempt = 0; $attempt -lt 2 -and (Test-Path $path); $attempt++) {
            if ($attempt) { Start-Sleep -Milliseconds 400 }
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        }

        if (Test-Path $path) { $kept += $path }
    }

    if ($kept) { Write-Kept "could not be removed: $($kept -join ', ')" }
}

if (-not (Test-Path $project)) {
    throw "$project not found. This script belongs in the project root, next to the src folder."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0'
}

try {
    Write-Step 'Restoring packages'
    & dotnet restore $project
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed. Check the connection and access to nuget.org.' }

    Write-Step 'Building the exe'

    if (Test-Path $exe) { Remove-Item $exe -Force }

    & dotnet publish $project -c Release -o $output
    if ($LASTEXITCODE -ne 0) { throw 'The build failed. The full log is above.' }
    if (-not (Test-Path $exe)) { throw "Expected $exe, but it is not there." }

    $version = "$(([xml](Get-Content $project -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText)".Trim()

    Write-Ok "$exe ($(Show-Size $exe), version $version, .NET not bundled)"
}
finally {
    Remove-Leftovers
}
