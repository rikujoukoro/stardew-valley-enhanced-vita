#!/usr/bin/env bash
# Linux (experimental): builds Stardew Valley for the PS Vita from your own Steam copy (see README.md). Needs the
# .NET 8 SDK and vita-toolchain-linux.zip in this folder. Options are passed on to the builder.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
command -v dotnet > /dev/null || { echo "Install the .NET 8 SDK first: https://dotnet.microsoft.com/download"; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
exec dotnet run --project builder -c Release -- "$@"
