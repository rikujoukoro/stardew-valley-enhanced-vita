#!/usr/bin/env bash
# Builds stardew.vpk in a Linux container: the way to build on macOS (Docker Desktop, OrbStack or Colima) and on any
# other system with Docker. It runs the builder's Linux path in Microsoft's .NET 8 SDK image for x86_64 (Apple
# Silicon runs it through Rosetta); the results land in work/out as with a normal run.
#
#   tools/docker-build.sh --game "<folder with Stardew Valley.exe>" [--toolchain <vita-toolchain-linux.zip>] [builder options]
set -euo pipefail
# macOS ships bash 3.2: no empty "${array[@]}" under set -u, hence ${REST[@]+...} below.

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE=mcr.microsoft.com/dotnet/sdk:8.0
GAME=
TOOLCHAIN=
REST=()
while [ $# -gt 0 ]; do
	case "$1" in
		--game) GAME="${2:?--game needs a folder}"; shift 2 ;;
		--toolchain) TOOLCHAIN="${2:?--toolchain needs a zip}"; shift 2 ;;
		*) REST+=("$1"); shift ;;
	esac
done
[ -n "$GAME" ] || { echo "usage: tools/docker-build.sh --game \"<folder with Stardew Valley.exe>\" [--toolchain <vita-toolchain-linux.zip>]"; exit 2; }
[ -f "$GAME/Stardew Valley.exe" ] || { echo "No Stardew Valley.exe in $GAME (the builder needs the game's Windows files, see the README)."; exit 1; }
command -v docker > /dev/null || { echo "Docker isn't installed: install Docker Desktop (or OrbStack, Colima) and run this again."; exit 1; }

# The release bundle has the toolchain zip next to the builder.
if [ -z "$TOOLCHAIN" ] && [ -f "$REPO/vita-toolchain-linux.zip" ]; then
	TOOLCHAIN="$REPO/vita-toolchain-linux.zip"
fi
MOUNTS=(-v "$REPO:/builder" -v "$(cd "$GAME" && pwd):/game:ro")
ARGS=(--game /game)
if [ -n "$TOOLCHAIN" ]; then
	[ -f "$TOOLCHAIN" ] || { echo "No such file: $TOOLCHAIN"; exit 1; }
	MOUNTS+=(-v "$(cd "$(dirname "$TOOLCHAIN")" && pwd)/$(basename "$TOOLCHAIN"):/toolchain.zip:ro")
	ARGS+=(--toolchain /toolchain.zip)
fi
# The game's saves on this Mac or Linux PC, so the builder copies them into the output like everywhere else.
SAVES="$HOME/.config/StardewValley/Saves"
if [ -d "$SAVES" ]; then
	MOUNTS+=(-v "$SAVES:/saves:ro")
	ARGS+=(--saves /saves)
fi

# As root in the container (apt-get), then the files it made are handed back to the caller.
docker run --rm --platform linux/amd64 "${MOUNTS[@]}" -w /builder \
	-e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e OWNER="$(id -u):$(id -g)" \
	"$IMAGE" bash -c '
		command -v xz > /dev/null || { apt-get update -qq && apt-get install -y -qq xz-utils > /dev/null; }
		status=0
		dotnet run --project builder -c Release -- "$@" || status=$?
		chown -R "$OWNER" /builder/work /builder/builder/bin /builder/builder/obj 2> /dev/null || true
		exit $status
	' docker-build "${ARGS[@]}" ${REST[@]+"${REST[@]}"}
