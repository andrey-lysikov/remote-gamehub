#Requires -Version 5.1

# Builds the server: tests, then the exe; with -Installer the msi around it (WiX is installed if
# missing). Nothing is downloaded here — see installer/Prerequisites.ps1 for the runtime and driver.

param(
    # Package the installer as well as the exe.
    [switch] $Installer,

    # The version the installer announces. Taken from the csproj when not given, so it cannot
    # drift from the app; the workflow passes the one on the release page.
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty in a default parameter value under PowerShell 5.1 — paths are set here.
$repo    = $PSScriptRoot
$source  = Join-Path $repo 'src'
$tests   = Join-Path $repo 'test'
$project = Join-Path $source 'RemoteGameHub.csproj'
$testProj = Join-Path $tests 'RemoteGameHub.Tests.csproj'
$output  = Join-Path $repo 'build'
$exe     = Join-Path $output 'Remote-Gamehub.exe'

$wixDir  = Join-Path $repo 'installer'
$msi     = Join-Path $output 'Remote-Gamehub.msi'
$wixWork = Join-Path $wixDir 'obj'

$wixVersion = '6.0.2'

# What MSBuild and WiX leave beside the sources and packages. Removed when the script ends, whatever
# the outcome: the release build happens in a GitHub Action, and a local run leaves only its result.
$leftovers = @(
    (Join-Path $source 'bin'), (Join-Path $source 'obj'),
    (Join-Path $tests  'bin'), (Join-Path $tests  'obj'),
    $wixWork,
    [IO.Path]::ChangeExtension($msi, '.wixpdb')
)

function Write-Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "  + $text" -ForegroundColor Green }
function Show-Size($path)  { "$([math]::Round((Get-Item $path).Length / 1MB, 1)) MB" }

function Remove-Leftovers {
    foreach ($path in $leftovers) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
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

    Write-Step 'Running tests'
    & dotnet test $testProj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Nothing is built: those come first.' }

    Write-Step 'Building the exe'

    # The server keeps its own files beside the exe, here. Never remove the folder wholesale: that
    # would wipe the configuration, the log, the database and the certificate with the build.
    if (Test-Path $exe) { Remove-Item $exe -Force }

    # Every publish switch is already in the csproj: single-file, framework-dependent, win-x64.
    & dotnet publish $project -c Release -o $output
    if ($LASTEXITCODE -ne 0) { throw 'The build failed. The full log is above.' }
    if (-not (Test-Path $exe)) { throw "Expected $exe, but it is not there." }

    $properties = ([xml](Get-Content $project)).Project.PropertyGroup
    if (-not $Version) {
        $Version = "$($properties.Version | Where-Object { $_ })".Trim()
        if (-not $Version) { throw "No <Version> in $project" }
    }

    Write-Ok "$exe ($(Show-Size $exe), version $Version, .NET not bundled)"

    if (-not $Installer) { return }

    # --- The installer ---

    # The publisher shown in the list of installed programs. Taken from the csproj, where the exe
    # gets its own company name: two places to write it down is one place to forget.
    $manufacturer = "$($properties.Company | Where-Object { $_ })".Trim()
    if (-not $manufacturer) { throw "No <Company> in $project" }

    # Two-number versions everywhere (0.1, never 0.1.0), but Windows Installer wants three. The
    # third is added here and nowhere else: the msi says 0.1.0, the exe, log and release page 0.1.
    $msiVersion = if ($Version -match '^\d+\.\d+$') { "$Version.0" } else { $Version }

    New-Item -ItemType Directory -Force -Path $wixWork | Out-Null

    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"

    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        Write-Step 'Installing the WiX toolset'
        & dotnet tool install --global wix --version $wixVersion
        if ($LASTEXITCODE -ne 0) { throw 'The WiX toolset was not installed.' }
        $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
    }

    # Idempotent: an extension already added is reported and nothing changes.
    foreach ($extension in 'WixToolset.UI.wixext', 'WixToolset.Util.wixext') {
        & wix extension add -g "$extension/$wixVersion" | Out-Null
    }

    Write-Step "Building the msi for $Version, published by $manufacturer"

    & wix build -arch x64 `
        -d "Version=$msiVersion" `
        -d "Manufacturer=$manufacturer" `
        -d "Exe=$exe" `
        -d "Icon=$(Join-Path $repo 'pictures\RemoteGameHub.ico')" `
        -d "License=$(Join-Path $wixDir 'License.rtf')" `
        -d "Prerequisites=$(Join-Path $wixDir 'Prerequisites.ps1')" `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        (Join-Path $wixDir 'Package.wxs') (Join-Path $wixDir 'ShortcutsDlg.wxs') `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'The msi was not built.' }

    Write-Ok "$msi ($(Show-Size $msi))"

}
finally {
    Remove-Leftovers
}
