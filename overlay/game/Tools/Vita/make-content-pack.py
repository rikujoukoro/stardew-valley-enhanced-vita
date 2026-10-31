#!/usr/bin/env python3
"""Builds Content.pak for the PS Vita port: the game's .xnb files in one file (see StardewValley/VitaContentPack.cs).

Usage: make-content-pack.py <Content dir> <output Content.pak> [locale ...]

On the Vita's memory card finding and opening a file costs ~100 ms (the Content folders hold thousands of
files, every asset in up to 11 languages), so a 3 KB texture took 150 ms to load. The pack is opened once
and each asset is one seek and read. It holds every .xnb, all languages (~60 MB), unless locales are listed:
then only the files without a locale suffix plus those; anything not packed is read from the loose files.

Format (little endian): b"SVPK1\\0\\0\\0", u32 count, then per entry u16 name length, name (UTF-8, the
path under Content without ".xnb", "/" separated, lower case), u64 offset, u32 length; then the data.
"""
import os
import struct
import sys


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    root, out = sys.argv[1], sys.argv[2]
    locales = set(sys.argv[3:])
    files = []
    for directory, _, names in os.walk(root):
        for name in names:
            if not name.endswith(".xnb"):
                continue
            stem = name[:-4]
            parts = stem.split(".")
            locale = parts[-1] if len(parts) > 1 and "-" in parts[-1] else None
            if locales and locale is not None and locale not in locales:
                continue
            path = os.path.join(directory, name)
            key = os.path.relpath(path, root)[:-4].replace(os.sep, "/").lower()
            files.append((key, path))
    files.sort()
    header = bytearray(b"SVPK1\0\0\0")
    header += struct.pack("<I", len(files))
    index_size = len(header) + sum(2 + len(k.encode("utf-8")) + 12 for k, _ in files)
    offset = index_size
    entries = []
    for key, path in files:
        size = os.path.getsize(path)
        entries.append((key, path, offset, size))
        offset += size
    for key, _, off, size in entries:
        name = key.encode("utf-8")
        header += struct.pack("<H", len(name)) + name + struct.pack("<QI", off, size)
    assert len(header) == index_size
    with open(out, "wb") as f:
        f.write(header)
        for _, path, _, _ in entries:
            with open(path, "rb") as src:
                f.write(src.read())
    print(f"{len(entries)} files, {offset / 1e6:.1f} MB -> {out}")


if __name__ == "__main__":
    main()
