#!/usr/bin/env python3
"""Filters an AOT profile (Mono's .aotprofile format, mono/profiler/aot.h) for aot-stardew.sh.

Usage: filter-aotprofile.py <in> <out> <assembly> <class regex>

Methods of <assembly> are kept only if their class's full name ("Namespace.Type" or
"Namespace.Outer/Nested") matches <class regex>; methods of every other assembly are kept. Image, type
and generic instance records are all kept (method records only reference them).

Why: Mono's AOT code checks a class's static constructor on each static field access and can't inline
methods of classes with one, where the JIT runs the constructor first and drops both. The game's
per-frame code (Game1.player, Game1.currentLocation...) ran about twice as slow AOT-compiled, so only
code that runs rarely (menus, save serializers, events), where compiling at first use is what stalls,
is worth precompiling.
"""
import re
import struct
import sys


def main(src, dst, assembly, pattern):
    data = open(src, "rb").read()
    keep_re = re.compile(pattern)
    out = bytearray(data[:14])  # magic + version
    p = 14
    images, types = {}, {}
    kept = dropped = 0

    def i32():
        nonlocal p
        v = struct.unpack_from("<i", data, p)[0]
        p += 4
        return v

    def string():
        nonlocal p
        n = i32()
        v = data[p:p + n].decode("utf-8", "replace")
        p += n
        return v

    while True:
        start = p
        kind = data[p]
        p += 1
        rid = i32()
        if kind == 0:
            out += data[start:p]
            break
        if kind == 1:
            images[rid] = string()
            string()
        elif kind == 2:
            p += 1
            image = i32()
            i32()
            types[rid] = (image, string())
        elif kind == 3:
            count = i32()
            p += 4 * count
        elif kind == 4:
            cls = i32()
            i32()
            i32()
            string()
            string()
            image, name = types.get(cls, (-1, ""))
            if images.get(image) == assembly and not keep_re.search(name):
                dropped += 1
                continue
            kept += 1
        else:
            sys.exit(f"unknown record type {kind}")
        out += data[start:p]
    open(dst, "wb").write(out)
    print(f"{dst}: {kept} methods kept, {dropped} of {assembly} dropped")


if __name__ == "__main__":
    main(*sys.argv[1:5])
