#!/usr/bin/env python3
"""Stage two reviewed BC3 map assets in an existing SVPK1 pack; never edits the input.

Usage: patch-content-pack.py input.pak correction-directory output.pak
The correction directory must contain the encoder-correction manifests and XNBs.
"""
import hashlib
import json
from pathlib import Path
import struct
import sys

SOURCES = {
    "spring_outdoorsTileSheet": "170b3c3d4adb8f0b2954b86caca9611a115d8232ac795fe0752c62381bf795b9",
    "summer_outdoorsTileSheet": "c78a286270c48c972bb6d094c902e98111c27a03168e58d4a5c1a9ea16d82d18",
}
ENCODER = "807667ef98e0fd749cdb65cca0c2d980bc148109d2fed6f1873c81ae0f449933"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def unpack(data):
    if data[:8] != b"SVPK1\0\0\0":
        raise ValueError("invalid pack signature")
    count, = struct.unpack_from("<I", data, 8)
    cursor, entries = 12, {}
    for _ in range(count):
        length, = struct.unpack_from("<H", data, cursor)
        cursor += 2
        key = data[cursor:cursor+length].decode("utf-8")
        cursor += length
        offset, size = struct.unpack_from("<QI", data, cursor)
        cursor += 12
        if key in entries or offset + size > len(data):
            raise ValueError("duplicate key or out of bounds entry")
        entries[key] = (offset, size)
    end = cursor
    result = {}
    for key, (offset, size) in entries.items():
        if offset != end:
            raise ValueError("non-contiguous pack entry")
        result[key] = data[offset:offset+size]
        end += size
    if end != len(data):
        raise ValueError("trailing pack bytes")
    return result


def main():
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    source, corrected, output = map(Path, sys.argv[1:])
    if source.resolve() == output.resolve() or output.exists():
        raise ValueError("output must be a new file distinct from the source")
    original = source.read_bytes()
    assets = unpack(original)
    replacement = {}
    for name, source_hash in SOURCES.items():
        key = "maps/" + name.lower()
        folder = corrected / (name + "-bc3-stb")
        manifest = json.loads((folder / "manifest.json").read_text())
        if manifest["sourceSha256"] != source_hash or digest(assets[key]) != source_hash:
            raise ValueError("unreviewed input asset: " + key)
        if manifest["encoderSourceSha256"] != ENCODER or manifest["codedBytes"] != 505600:
            raise ValueError("unreviewed encoder or size")
        blob = (folder / (name + "-bc3.xnb")).read_bytes()
        if digest(blob) != manifest["outputSha256"]:
            raise ValueError("output hash mismatch")
        if blob[:6] != b"XNBw\x05\x00" or struct.unpack_from("<I", blob, 6)[0] != len(blob):
            raise ValueError("invalid output XNB header")
        replacement[key] = blob
    changed = dict(assets)
    changed.update(replacement)
    header = bytearray(b"SVPK1\0\0\0" + struct.pack("<I", len(changed)))
    offset = 12 + sum(14 + len(k.encode()) for k in changed)
    for key, blob in changed.items():
        name = key.encode()
        header += struct.pack("<H", len(name)) + name + struct.pack("<QI", offset, len(blob))
        offset += len(blob)
    result = bytes(header) + b"".join(changed.values())
    parsed = unpack(result)
    actual_changes = [k for k in assets if parsed[k] != assets[k]]
    if set(actual_changes) != set(replacement):
        raise ValueError("unexpected changes")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(result)
    if output.read_bytes() != result:
        raise IOError("write verification failed")
    report = {"sourceSha256": digest(original), "outputSha256": digest(result),
              "entries": len(parsed), "unchangedEntries": len(parsed)-2,
              "changedEntries": actual_changes, "outputBytes": len(result)}
    output.with_suffix(output.suffix + ".json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
