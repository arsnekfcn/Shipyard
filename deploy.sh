#!/usr/bin/env bash
# Build the plugin and deploy it — WITH its NuGet deps as separate files — into Pulsar's Legacy/Local
# folder, which Pulsar scans (recursively, *.dll) at startup. Restart SE after running to pick up changes.
# Pulsar will list the dep DLLs (Octokit, LibGit2Sharp) as their own entries; that's cosmetic — they load.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Windows username for the C:\Users\... Pulsar path. Auto-detected; override with WINUSER=... if it differs.
WINUSER="${WINUSER:-$(cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r\n')}"
WINUSER="${WINUSER:-$USER}"

PROJ_WIN="$(wslpath -w "$SCRIPT_DIR/ShipyardPlugin.csproj" 2>/dev/null || echo "$SCRIPT_DIR/ShipyardPlugin.csproj")"
OUTDIR="$SCRIPT_DIR/bin/Release"
PULSAR_LOCAL="/mnt/c/Users/$WINUSER/Desktop/Pulsar/Legacy/Local/Shipyard"

echo ">> building (Release)"
# --no-incremental: MSBuild's incremental check can miss source edits over the /mnt/c bridge (mtime quirks),
# silently shipping a stale DLL. Force a full compile every deploy.
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet.exe build "$PROJ_WIN" -c Release -v minimal --no-incremental

mkdir -p "$PULSAR_LOCAL"

# copy SRC into the Local plugin folder; rename-aside on lock (a loaded DLL can be renamed but not
# overwritten, and Pulsar only scans *.dll so the .old is inert until the next deploy cleans it up).
copy() {
    local src="$1" name; name="$(basename "$src")"
    [ -f "$src" ] || { echo ">> WARN missing (skipped): $src"; return 0; }
    rm -f "$PULSAR_LOCAL/$name.old" 2>/dev/null || true
    if ! cp -f "$src" "$PULSAR_LOCAL/" 2>/dev/null; then
        echo ">> $name is locked - renaming aside and copying fresh"
        powershell.exe -NoProfile -Command \
            "Rename-Item -LiteralPath 'C:\\Users\\$WINUSER\\Desktop\\Pulsar\\Legacy\\Local\\Shipyard\\$name' '$name.old' -Force" 2>/dev/null || true
        cp -f "$src" "$PULSAR_LOCAL/"
    fi
}

copy "$OUTDIR/Shipyard.dll"
copy "$OUTDIR/Octokit.dll"
copy "$OUTDIR/LibGit2Sharp.dll"
# Native libgit2 (x64) — LibGit2Sharp restores it via NuGet to lib/win32/<arch>; ship it next to the DLL
# where Plugin.FindNativeGitDir looks (offline-mode engine).
copy "$OUTDIR/lib/win32/x64/git2-a418d9d.dll"
# Logo asset next to the DLL (Brand.LogoPath falls back here when Pulsar's LoadAssets isn't called for a
# bare Local install).
cp -f "$SCRIPT_DIR/assets/logo.png" "$PULSAR_LOCAL/logo.png"

echo ">> deployed (Shipyard.dll + Octokit + LibGit2Sharp + native git2 + logo) to:"
echo ">>   C:\\Users\\$WINUSER\\Desktop\\Pulsar\\Legacy\\Local\\Shipyard\\"
echo ">> Restart Space Engineers (via Pulsar) and enable 'Shipyard' under Local."
