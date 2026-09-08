#!/usr/bin/env bash
#
# Builds the Windows exe on macOS or Linux — the counterpart of build.ps1 for machines
# without PowerShell. The result is the same file: build/Remote-Gamehub.exe (win-x64, needs
# .NET Desktop Runtime 10 on the target PC).
#
#     ./build.sh
#
# What stays on Windows: the tests (they load WPF, which the Mac runtime does not have) and the
# msi. Both run in GitHub Actions on every release, see .github/workflows/release.yml.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$repo/src/RemoteGameHub.csproj"
output="$repo/build"
exe="$output/Remote-Gamehub.exe"

step() { printf '\n\033[36m=== %s\033[0m\n' "$1"; }
ok()   { printf '\033[32m  + %s\033[0m\n' "$1"; }

# MSBuild leaves these next to the sources; the folder with the exe is left alone, the app
# keeps its settings and log there.
cleanup() {
    rm -rf "$repo/src/bin" "$repo/src/obj" "$repo/test/bin" "$repo/test/obj"
}
trap cleanup EXIT

[[ -f "$project" ]] || { echo "$project not found. Run the script from the project root." >&2; exit 1; }

command -v dotnet >/dev/null 2>&1 || {
    echo 'The .NET 10 SDK is required: brew install --cask dotnet-sdk, or' >&2
    echo 'https://dotnet.microsoft.com/download/dotnet/10.0' >&2
    exit 1
}

step 'Restoring packages'
dotnet restore "$project"

step 'Building the exe'
rm -f "$exe"

# Every publish switch is already in the csproj: single-file, framework-dependent, win-x64.
# Directory.Build.props turns on EnableWindowsTargeting, so no flag is needed here.
dotnet publish "$project" -c Release -o "$output"

[[ -f "$exe" ]] || { echo "Expected $exe, but it is not there." >&2; exit 1; }

size=$(du -m "$exe" | cut -f1)
ok "$exe (${size} MB, .NET not bundled)"
