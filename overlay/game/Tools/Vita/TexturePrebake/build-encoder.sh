#!/bin/bash
set -eu
tool_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
root="$(cd "$tool_dir/../../../.." && pwd)"
out="$root/scratchpad/texture-prebake/encoder-correction-20261027"
mkdir -p "$out"
printf '%s  %s\n' '807667ef98e0fd749cdb65cca0c2d980bc148109d2fed6f1873c81ae0f449933' "$tool_dir/stb_dxt.h" | sha256sum -c -
gcc -std=c99 -O2 -Wall -Wextra "$tool_dir/encode-bc3.c" -lm -o "$out/encode-bc3"
