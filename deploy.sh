#!/usr/bin/env bash
# Build the plugin and deploy it into Pulsar's Legacy/Local folder, which Pulsar scans
# (recursively, *.dll) at game startup. Restart SE after running this to pick up changes.
set -euo pipefail

# Windows username for the C:\Users\... Pulsar path. Auto-detected; override with WINUSER=... if your
# Windows user differs from your WSL user.
WINUSER="${WINUSER:-$(cmd.exe /c "echo %USERNAME%" 2>/dev/null | tr -d '\r\n')}"
WINUSER="${WINUSER:-$USER}"

PROJ='C:\se-dev\plugins\ShipyardPlugin\ShipyardPlugin.csproj'
OUTDIR="/mnt/c/se-dev/plugins/ShipyardPlugin/bin/Release"
PULSAR_BASE="/mnt/c/Users/$WINUSER/Desktop/Pulsar/Legacy/Local"
PULSAR_LOCAL="$PULSAR_BASE/Shipyard"

echo ">> building (Release)"
# --no-incremental: MSBuild's incremental check can miss source edits made over the /mnt/c bridge
# (mtime quirks), silently shipping a stale DLL. Force a full compile every deploy.
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet.exe build "$PROJ" -c Release -v minimal --no-incremental

mkdir -p "$PULSAR_LOCAL"
# Single plugin DLL (Octokit is embedded inside it). Remove any stale Octokit.dll so Pulsar
# doesn't keep listing it as a separate "plugin".
rm -f "$PULSAR_LOCAL/Octokit.dll"
# Clean up any .old left by a previous locked-file deploy (see below); ignore if still locked.
rm -f "$PULSAR_LOCAL/Shipyard.dll.old" 2>/dev/null || true
if ! cp -f "$OUTDIR/Shipyard.dll" "$PULSAR_LOCAL/" 2>/dev/null; then
    # The DLL is locked (game running, or a lingering handle). A loaded DLL can't be
    # overwritten but CAN be renamed - move it aside and copy fresh. Pulsar only scans
    # *.dll, so the .old is inert and gets cleaned up on the next deploy.
    echo ">> Shipyard.dll is locked - renaming it aside and copying fresh"
    powershell.exe -NoProfile -Command \
        "Rename-Item 'C:\Users\\$WINUSER\Desktop\Pulsar\Legacy\Local\Shipyard\Shipyard.dll' 'Shipyard.dll.old' -Force"
    cp -f "$OUTDIR/Shipyard.dll" "$PULSAR_LOCAL/"
fi
# Logo is embedded in the DLL and self-extracts on Init (single-file plugin); no separate copy needed.
rm -f "$PULSAR_LOCAL/logo.png"
echo ">> deployed to: C:\\Users\\$WINUSER\\Desktop\\Pulsar\\Legacy\\Local\\Shipyard\\"
echo ">> Restart Space Engineers (via Pulsar). In the plugin list, enable 'Shipyard' under Local."
