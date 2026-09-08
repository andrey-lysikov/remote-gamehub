#!/usr/bin/env bash

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$repo/src/RemoteGameHub.csproj"
output="$repo/build"
exe="$output/Remote-Gamehub.exe"

step() { printf '\n\033[36m=== %s\033[0m\n' "$1"; }
ok()   { printf '\033[32m  + %s\033[0m\n' "$1"; }

cleanup() {
    local kept=""

    for path in "$repo/src/bin" "$repo/src/obj" \
                "$repo/test/bin" "$repo/test/obj" \
                "$repo/installer/bin" "$repo/installer/obj"; do
        if [[ -e "$path" ]]; then
            rm -rf "$path" 2>/dev/null || true
            if [[ -e "$path" ]]; then kept="$kept $path"; fi
        fi
    done

    if [[ -n "$kept" ]]; then
        printf '\033[33m  ! could not be removed:%s\033[0m\n' "$kept" >&2
    fi
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

dotnet publish "$project" -c Release -o "$output"

[[ -f "$exe" ]] || { echo "Expected $exe, but it is not there." >&2; exit 1; }

size=$(du -m "$exe" | cut -f1)
ok "$exe (${size} MB, .NET not bundled)"
