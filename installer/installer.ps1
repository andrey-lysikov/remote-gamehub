#Requires -Version 5.1

param(
    [string] $Version = ''
)

$ErrorActionPreference = 'Stop'

$wixDir  = $PSScriptRoot
$repo    = Split-Path -Parent $PSScriptRoot
$source  = Join-Path $repo 'src'
$project = Join-Path $source 'RemoteGameHub.csproj'
$output  = Join-Path $repo 'build'
$exe     = Join-Path $output 'Remote-Gamehub.exe'

$msi     = Join-Path $output 'Remote-Gamehub.msi'

$packageWxs   = Join-Path $wixDir 'Package.wxs'
$shortcutsWxs = Join-Path $wixDir 'ShortcutsDlg.wxs'

$wixVersion = '6.0.2'

$projects  = @($source, (Join-Path $repo 'test'), $wixDir)
$leftovers = @([IO.Path]::ChangeExtension($msi, '.wixpdb'))

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
    foreach ($one in $projects) {
        $paths += (Join-Path $one 'bin'), (Join-Path $one 'obj')
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

if (-not (Test-Path $packageWxs)) {
    throw "$packageWxs not found. This script belongs in the installer folder, beside the sources it packages."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0'
}

if (-not (Test-Path $exe)) {
    throw "$exe is not there. Run build.ps1 first - this packages what that produces."
}

try {
    $properties = [xml](Get-Content $project -Raw)

    if (-not $Version) {
        $Version = "$($properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText)".Trim()
        if (-not $Version) { throw "No <Version> in $project" }
    }

    $manufacturer = "$($properties.SelectSingleNode('/Project/PropertyGroup/Company').InnerText)".Trim()
    if (-not $manufacturer) { throw "No <Company> in $project" }

    $msiVersion = if ($Version -match '^\d+\.\d+$') { "$Version.0" } else { $Version }

    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"

    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        Write-Step 'Installing the WiX toolset'
        & dotnet tool install --global wix --version $wixVersion
        if ($LASTEXITCODE -ne 0) { throw 'The WiX toolset was not installed.' }
        $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
    }

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
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        $packageWxs $shortcutsWxs `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'The msi was not built.' }

    Write-Ok "$msi ($(Show-Size $msi))"
}
finally {
    Remove-Leftovers
}
