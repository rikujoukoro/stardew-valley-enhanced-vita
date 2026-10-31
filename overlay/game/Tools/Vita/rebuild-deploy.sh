#!/bin/bash
# Rebuilds the VPK from the current game build and copies the result to vita-deploy.
# Run inside WSL as root, after `dotnet build Stardew-Valley.csproj -c Release` (it also builds FNA.dll):
#   wsl.exe -u root -- bash "/mnt/c/Users/mamik/Desktop/stardew vita 1.6/SV_PC_source/Tools/Vita/rebuild-deploy.sh"
# Then copy vita-deploy\stardew.vpk to F:\ and "Stardew Valley.exe" + xTile.dll to F:\data\StardewValley\.
set -euo pipefail
REPO="/mnt/c/Users/mamik/Desktop/stardew vita 1.6/SV_PC_source"
GAME="/mnt/c/Users/mamik/Desktop/stardew vita 1.6/vita-install/StardewValley"
DEPLOY="/mnt/c/Users/mamik/Desktop/stardew vita 1.6/vita-deploy"
cp "$REPO/bin/Release/net40/Stardew Valley.exe" "$REPO/bin/Release/net40/xTile.dll" "$REPO/bin/Release/net40/Lidgren.Network.dll" "$GAME/"
cd "$REPO/Tools/Vita"
AOT_PROFILE="/mnt/c/Users/mamik/Desktop/stardew vita 1.6/aot-profiles/stardew-1.aotprofile" \
PROFILE_ONLY="System:System.Xml:Stardew Valley" \
	bash aot-stardew.sh "$GAME" 2>&1 | tail -3
AOT_DIR=/root/vita/build-aot bash build-stardew.sh /root/vita/build-stardew 2>&1 | grep -v "same file" | tail -3 || true
ls -la /root/vita/build-stardew/stardew.vpk
cp /root/vita/build-stardew/stardew.vpk "$DEPLOY/stardew.vpk"
cp "$REPO/bin/Release/net40/Stardew Valley.exe" "$REPO/bin/Release/net40/xTile.dll" "$REPO/bin/Release/net40/Lidgren.Network.dll" "$DEPLOY/"
