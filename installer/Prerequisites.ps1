# Fetches what the msi does not carry — the .NET Desktop Runtime and the ViGEmBus driver — on the
# installing machine, only what is missing or behind. Run by hand: Prerequisites.ps1 [-NoDriver]

param(
    # The driver is the one part somebody may not want; the installer passes this when its
    # checkbox was cleared. The runtime is not optional — without it nothing starts.
    [switch] $NoDriver
)

# Not Stop: curl writes its progress to the error stream, which under Stop ends the run in the
# middle of a download. Every step below is checked for itself instead.
$ErrorActionPreference = 'Continue'

# What the installer does is worth reading afterwards, and a console window closes with the last
# line still on it.
$transcript = Join-Path $env:TEMP 'Remote-Gamehub-prerequisites.log'
try { Start-Transcript -Path $transcript -Force | Out-Null } catch { }

# The newest build of the band, as a redirect whose address names the version it lands on, and
# the driver's last release. Both are asked at the moment of installing, never before.
$dotNetUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
$viGEmApi  = 'https://api.github.com/repos/nefarius/ViGEmBus/releases/latest'

# ProgramW6432 and not ProgramFiles: the installer runs its actions from a 32-bit process, and to
# that one "Program Files" is the (x86) one, where no .NET runtime has ever lived.
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$dotNetShared = Join-Path $programFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'

# curl.exe, which Windows ships, rather than Invoke-WebRequest: present under any execution policy
# and fast. Silent with errors kept: its progress meter goes to the error stream.
function Read-Address($url) {
    $answer = & curl.exe -sIL -o NUL -w '%{url_effective}' $url
    if ($LASTEXITCODE -ne 0) { return $null }
    return $answer
}

function Read-Text($url) {
    $answer = & curl.exe -sL --fail -H 'User-Agent: Remote-Gamehub' $url
    if ($LASTEXITCODE -ne 0) { return $null }
    return $answer
}

function Get-File($url, $path) {
    & curl.exe -L --fail --silent --show-error -o $path $url
    return $LASTEXITCODE -eq 0 -and (Test-Path $path)
}

function Write-Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }

# Runs one of the two setups and says how it went. Both are WiX bundles: they keep a log of their
# own where they are told to, and 3010 is their way of saying "installed, but not until a restart".
function Invoke-Setup($file, $arguments, $log) {
    $all = @($arguments) + @('/log', $log)

    try {
        $process = Start-Process $file -ArgumentList $all -Wait -PassThru
        $code = $process.ExitCode
    }
    catch {
        # A refused prompt for administrator rights lands here, and nothing was installed.
        Write-Host "  it did not run: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    switch ($code) {
        0 { Write-Host '  installed'; return $true }
        3010 {
            Write-Host '  installed, and Windows needs a restart to finish it' -ForegroundColor Yellow
            return $true
        }
        default {
            Write-Host "  its setup ended with $code; what it did is in $log" -ForegroundColor Red
            return $false
        }
    }
}

# The newest of the framework folders, which are named by version and are what a .NET application
# actually runs on. Nothing there means nothing installed.
function Get-DotNetInstalled {
    if (-not (Test-Path $dotNetShared)) { return $null }

    $versions = Get-ChildItem $dotNetShared -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { $parsed = $null; if ([version]::TryParse($_.Name, [ref] $parsed)) { $parsed } }

    if (-not $versions) { return $null }
    return ($versions | Sort-Object -Descending)[0]
}

# The driver as the list of installed programs has it, and its service beside it: a ViGEmBus that
# was disabled leaves its key behind, and the pads then fail with "not supported".
function Get-ViGEmInstalled {
    $roots = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
               'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')

    foreach ($root in $roots) {
        foreach ($key in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($entry.DisplayName -notlike '*ViGEm*') { continue }

            $parsed = $null
            if ([version]::TryParse("$($entry.DisplayVersion)", [ref] $parsed)) { return $parsed }
        }
    }

    return $null
}

function Test-ViGEmDisabled {
    $service = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\ViGEmBus' -ErrorAction SilentlyContinue
    return $null -ne $service -and $service.Start -eq 4
}

$stopped = $false

# ------------------------------------------------------------------ the runtime

Write-Step 'Looking for the .NET Desktop Runtime'

$installed = Get-DotNetInstalled
$href = Read-Address $dotNetUrl
$offered = $null

# windowsdesktop-runtime-10.0.11-win-x64.exe — the address of the newest build names it.
if ($href -and $href -match 'windowsdesktop-runtime-([0-9]+(\.[0-9]+)+)-win') {
    $parsed = $null
    if ([version]::TryParse($Matches[1], [ref] $parsed)) { $offered = $parsed }
}

if ($installed) { Write-Host "  installed: $installed" }
else            { Write-Host '  installed: none' }

if ($offered)   { Write-Host "  newest:    $offered" }
else            { Write-Host '  newest:    could not be asked for' }

if (-not $installed -and -not $offered) {
    Write-Host '  The runtime is missing and its address could not be read. Install it by hand:' -ForegroundColor Red
    Write-Host '  https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    $stopped = $true
}
elseif ($offered -and (-not $installed -or $installed -lt $offered)) {
    $file = Join-Path $env:TEMP 'windowsdesktop-runtime.exe'
    Write-Host "  fetching $href"

    if (Get-File $href $file) {
        Write-Host '  installing it — Windows will ask for administrator rights'

        $went = Invoke-Setup $file @('/install', '/passive', '/norestart') `
                             (Join-Path $env:TEMP 'Remote-Gamehub-dotnet-setup.log')

        Remove-Item $file -Force -ErrorAction SilentlyContinue

        # Said against the folders again rather than against the setup's word for it.
        $now = Get-DotNetInstalled
        if ($now) { Write-Host "  the runtime is now $now" }
        elseif (-not $went) { $stopped = $true }
    }
    elseif (-not $installed) {
        Write-Host '  It could not be fetched, and the server will not start without it.' -ForegroundColor Red
        $stopped = $true
    }
    else {
        Write-Host '  It could not be fetched; what is installed will do.'
    }
}
else {
    Write-Host '  nothing to do'
}

# ------------------------------------------------------------------ the driver

if ($NoDriver) {
    Write-Step 'The ViGEmBus driver was not asked for; gamepads will not reach a game.'
}
else {
    Write-Step 'Looking for the ViGEmBus driver'

    $installed = Get-ViGEmInstalled
    $disabled = Test-ViGEmDisabled
    $offered = $null
    $asset = $null

    $release = Read-Text $viGEmApi
    if ($release) {
        try {
            $json = $release | ConvertFrom-Json
            $parsed = $null
            if ([version]::TryParse("$($json.tag_name)".TrimStart('v'), [ref] $parsed)) { $offered = $parsed }

            $asset = $json.assets |
                Where-Object { $_.name -like 'ViGEmBus_*_x64*.exe' } |
                Select-Object -First 1
        }
        catch {
            # An answer that is not the release means the driver is left alone, which is what
            # happens on a machine with no way out to the internet anyway.
        }
    }

    if ($installed) { Write-Host "  installed: $installed$(if ($disabled) { ' (disabled)' })" }
    else            { Write-Host '  installed: none' }

    if ($offered)   { Write-Host "  newest:    $offered" }
    else            { Write-Host '  newest:    could not be asked for' }

    if ($asset -and (-not $installed -or $installed -lt $offered -or $disabled)) {
        $file = Join-Path $env:TEMP $asset.name
        Write-Host "  fetching $($asset.browser_download_url)"

        if (Get-File $asset.browser_download_url $file) {
            Write-Host '  installing it — Windows will ask for administrator rights'

            Invoke-Setup $file @('/passive', '/norestart') `
                         (Join-Path $env:TEMP 'Remote-Gamehub-vigem-setup.log') | Out-Null

            Remove-Item $file -Force -ErrorAction SilentlyContinue

            # The list of programs again, and the driver's own file: a setup that said nothing and
            # left nothing behind is what sent this script looking.
            $now = Get-ViGEmInstalled
            $sys = Join-Path $env:SystemRoot 'System32\drivers\ViGEmBus.sys'

            if ($now -and (Test-Path $sys)) {
                Write-Host "  the driver is now $now"
            }
            elseif ((Test-ViGEmDisabled) -and -not (Test-Path $sys)) {
                Write-Host '  It is not in yet: an earlier copy of the driver is still being' -ForegroundColor Yellow
                Write-Host '  removed, and Windows finishes that at the next restart. Restart, then' -ForegroundColor Yellow
                Write-Host '  run this script again — it is beside the server, Prerequisites.ps1.' -ForegroundColor Yellow
            }
            else {
                Write-Host '  It is not there. Gamepads will not reach a game until it is.'
            }
        }
        else {
            # Never fatal: everything but the pads works without it, and the server says at every
            # start whether the driver is there.
            Write-Host '  It could not be fetched. Gamepads will not reach a game until it is.'
        }
    }
    else {
        Write-Host '  nothing to do'
    }
}

# A window that vanishes takes its message with it, and the one message worth reading here is the
# one that says the server will not run.
Write-Host "`nWhat happened here is also in $transcript"
try { Stop-Transcript | Out-Null } catch { }

if ($stopped) {
    Write-Host "`nThis window closes in a minute." -ForegroundColor Red
    Start-Sleep -Seconds 60
}

# Nothing of this belongs on the machine once it has run.
Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
