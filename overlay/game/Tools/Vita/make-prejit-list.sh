#!/usr/bin/env bash
# Builds vita-prejit.txt (VitaPlatform.PrepareHotMethods) from a session logged with the jit-log switch
# (stardew/main.c): the game's own methods compiled after <seconds> - pass the time the save finished
# loading, as console.txt shows it. Closed generic instantiations (Dictionary`2<Point, bool>:TryInsert,
# Enumerable:TryGetLast<Point>) are kept when they took >= 1 ms - from any assembly, since the JIT compiles
# those even for System types (PreJitResolver parses them). Non-generic mscorlib/System methods are left
# out: they are AOT-compiled.
#
#   make-prejit-list.sh <vita-log.txt> <seconds> [existing list ...] > vita-prejit.txt
#
# Existing lists are merged in, so sessions that played different parts of the game add up.
set -euo pipefail

LOG="${1:?usage: make-prejit-list.sh <vita-log.txt> <seconds> [existing list ...]}"
FROM="${2:?usage: make-prejit-list.sh <vita-log.txt> <seconds> [existing list ...]}"
shift 2
# Game methods (any time) and generic entries (>= 1 ms), from the log; the existing lists are merged as they are.
JIT_LINES() { awk -F'[][]' -v from="$FROM" '$2 + 0 >= from && / jit [0-9.]+ ms: /' "$LOG" | tr -d '\r'; }
{
	JIT_LINES | sed 's/.* ms: //' | grep -E '^(StardewValley|Netcode|xTile|Microsoft\.Xna|BmFont)' | grep -v '`' || true
	JIT_LINES | awk '{ if ($4 + 0 >= 1) print }' | sed 's/.* ms: //' | grep -v '^(wrapper' \
		| awk '{ i = index($0, ":"); t = substr($0, 1, i - 1); m = substr($0, i + 1); lt = index(m, "<");
			g = (index(t, "`") > 0) || (lt > 1 && substr(m, length(m), 1) == ">" && index(substr(m, 1, lt), ".") == 0);
			if (g && m != ".cctor") print }' || true
	for list in "$@"; do tr -d '\r' < "$list"; done
} | sort -u
