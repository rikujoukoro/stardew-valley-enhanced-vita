# Maintainer tool: turns our SV_PC_source tree into the builder's patches/ and overlay/.
#
# SV_PC_source started as an ILSpy GUI export made on a Turkish Windows (commit 6a1cfd7). That export differs
# from the builder's ilspycmd export in a few hundred identifiers only (tr-TR lowercasing: "ıtem", plus the
# numbering that follows from it, and the order of using lines). So we replay our edits (6a1cfd7 -> working
# tree) onto the clean export with a 3-way merge and diff the result against the clean export.
#
#   dotnet run --project builder -- --work <dir> --decompile-only
#   py -3 -I make-patches.py --source <SV_PC_source> --clean <dir>/src/SV_PC_source --fna <our FNA checkout>
#                            --out <vita-builder>
#
# The clean tree must come from the builder itself (it pins the decompiler, its settings and the XNA references).
import argparse, os, re, shutil, subprocess, sys, tempfile

GIT_BASE = '6a1cfd7'
DEPS = ['xTile', 'BmFont', 'StardewValley.GameData', 'Lidgren.Network']
# Our own files that are generated at build time instead of shipped.
GENERATED = {'StardewValley.SaveSerialization/Generated/XmlSerializers.cs'}
SKIP_DIRS = {'.git', 'bin', 'obj', 'bin_fna', 'obj_fna', '.vs', 'Tools', 'Deps'}
IDENT = re.compile(r'[A-Za-z_ı][\wı]*')
BS = chr(92)


def undot(text):
    """Replaces the tr-TR dotless i in identifiers (not in strings, chars or comments)."""
    out = []
    for line in text.split('\n'):
        res = []; i = 0; q = None
        while i < len(line):
            c = line[i]
            if q:
                res.append(c)
                if c == BS and q != '@':
                    res.append(line[i+1:i+2]); i += 2; continue
                if c == '"': q = None
                i += 1; continue
            if c == '"':
                q = '@' if i and line[i-1] == '@' else '"'; res.append(c); i += 1; continue
            if c == "'" and i + 2 < len(line):
                j = line.find("'", i + 2 if line[i+1] == BS else i + 1)
                if j > i:
                    res.append(line[i:j+1]); i = j + 1; continue
            if line.startswith('//', i):
                res.append(line[i:]); break
            m = IDENT.match(line, i)
            if m and (i == 0 or not (line[i-1].isalnum() or line[i-1] == '_')):
                res.append(m.group().replace('ı', 'i')); i = m.end(); continue
            res.append(c); i += 1
        out.append(''.join(res))
    return '\n'.join(out)


def read(p):
    return open(p, encoding='utf-8-sig').read().replace('\r\n', '\n')


def write(p, s, bom=False):
    os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(s.replace('\n', '\r\n'))


def has_bom(p):
    return open(p, 'rb').read(3) == b'\xef\xbb\xbf'


def using_block(text):
    lines = text.split('\n'); n = 0
    while n < len(lines) and (lines[n].startswith(('using ', '<<<<<<< ', '=======', '>>>>>>> ')) or lines[n] == ''):
        n += 1
    return lines[:n], lines[n:]


def merge(ours, base, clean, tmp):
    paths = [os.path.join(tmp, n) for n in ('ours', 'base', 'clean')]
    for p, s in zip(paths, (ours, base, clean)):
        open(p, 'w', encoding='utf-8', newline='').write(s)
    # base -> clean only ever changes how the decompiler spells things (names, casts, using order), so where
    # it overlaps one of our edits, our side wins. Conflicts are reported so the build can be checked.
    r = subprocess.run(['git', 'merge-file', '-p', '-L', 'ours', '-L', 'base', '-L', 'clean'] + paths,
                       capture_output=True)
    conflicted = r.returncode != 0 and '<<<<<<< ours' in '\n'.join(using_block(r.stdout.decode('utf-8'))[1])
    subprocess.run(['git', 'merge-file', '--ours', '-L', 'ours', '-L', 'base', '-L', 'clean'] + paths)
    merged = open(paths[0], encoding='utf-8').read()
    # The GUI export sorted using lines with tr-TR collation; take ours verbatim so removed usings stay removed.
    merged = '\n'.join(using_block(ours)[0] + using_block(merged)[1])
    return merged, conflicted


def walk(root, skip=()):
    for d, dirs, fs in os.walk(root):
        dirs[:] = [x for x in dirs if x not in skip]
        for f in fs:
            yield os.path.relpath(os.path.join(d, f), root).replace(os.sep, '/')


def diff(a_root, b_root, rel, a_text, b_text, tmp, out):
    """Writes a git-style patch for one file, with a/ b/ paths relative to the tree root."""
    a = os.path.join(tmp, 'a', rel); b = os.path.join(tmp, 'b', rel)
    for p, s in ((a, a_text), (b, b_text)):
        if s is not None:
            os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, 'w', encoding='utf-8', newline='').write(s.replace('\n', '\r\n'))
    r = subprocess.run(['git', 'diff', '--no-index', '--no-color', '--full-index', '--binary',
                        'a/' + rel if a_text is not None else '/dev/null',
                        'b/' + rel if b_text is not None else '/dev/null'],
                       cwd=tmp, capture_output=True)
    patch = r.stdout.decode('utf-8').replace('a/a/', 'a/').replace('b/b/', 'b/')
    for p in (a, b):
        if os.path.exists(p): os.remove(p)
    if patch:
        p = os.path.join(out, rel + '.patch'); os.makedirs(os.path.dirname(p), exist_ok=True)
        open(p, 'w', encoding='utf-8', newline='').write(patch)


def game(src, clean, out, tmp):
    base = os.path.join(tmp, 'gitbase')
    os.makedirs(base)
    arch = subprocess.run(['git', '-C', src, 'archive', GIT_BASE], capture_output=True, check=True).stdout
    subprocess.run(['tar', '-x', '-C', base], input=arch, check=True)
    pdir = os.path.join(out, 'patches', 'game'); odir = os.path.join(out, 'overlay', 'game')
    deleted = []; conflicts = []; changed = 0
    for rel in sorted(walk(clean, {'Deps'})):
        if not rel.endswith('.cs'): continue
        cur = os.path.join(src, rel)
        if not os.path.exists(cur):
            deleted.append(rel); continue
        if read(cur) == read(os.path.join(base, rel)): continue
        c = read(os.path.join(clean, rel))
        merged, bad = merge(undot(read(cur)), undot(read(os.path.join(base, rel))), c, tmp)
        if bad: conflicts.append(rel)
        changed += 1
        diff(clean, src, rel, c, merged, tmp, pdir)
    clean_files = set(walk(clean, {'Deps'}))
    added = 0
    for rel in sorted(walk(src, SKIP_DIRS)):
        if rel in clean_files or rel in GENERATED or rel.startswith('Content/') and not rel.endswith('.png'): continue
        if rel in ('.gitignore',): continue
        dst = os.path.join(odir, rel); os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(os.path.join(src, rel), dst); added += 1
    write(os.path.join(out, 'patches', 'game.deleted.txt'), '\n'.join(deleted) + '\n')
    return changed, added, len(deleted), conflicts


def deps(src, clean_root, out, tmp):
    res = {}
    for name in DEPS:
        clean = os.path.join(clean_root, 'Deps', name); ours = os.path.join(src, 'Deps', name)
        pdir = os.path.join(out, 'patches', 'deps', name); odir = os.path.join(out, 'overlay', 'deps', name)
        changed = 0; seen = set()
        for rel in sorted(walk(clean)):
            if not rel.endswith('.cs'): continue
            r2 = rel; seen.add(r2)
            cur = os.path.join(ours, r2)
            c = read(os.path.join(clean, rel))
            mine = read(cur) if os.path.exists(cur) else None
            if mine == c: continue
            changed += 1
            diff(clean, ours, r2, c, mine, tmp, pdir)
        added = 0
        for rel in walk(ours, {'bin', 'obj'}):
            if rel in seen: continue
            dst = os.path.join(odir, rel); os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(os.path.join(ours, rel), dst); added += 1
        res[name] = (changed, added)
    shutil.copyfile(os.path.join(src, 'Deps', 'Directory.Build.props'),
                    os.path.join(out, 'overlay', 'deps', 'Directory.Build.props'))
    return res


def tools(src, out):
    # Build-time tools of ours that the builder runs (SerializerGen writes XmlSerializers.cs).
    for rel in ('Tools/SerializerGen/Program.cs', 'Tools/SerializerGen/SerializerGen.csproj',
                'Tools/WaveBankTool/Program.cs', 'Tools/WaveBankTool/WaveBankTool.csproj',
                'Tools/Vita/stardew/vita-prejit.txt'):
        dst = os.path.join(out, 'overlay', 'game', rel); os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(os.path.join(src, rel), dst)
    # The Vita launcher and its build scripts, without build outputs and without the LiveArea/manual/trophy
    # images made from the game's art (sce_sys): the builder takes those from assets/.
    vita = os.path.join(src, 'Tools', 'Vita')
    for rel in walk(vita, {'bin', 'obj', '__pycache__', 'sce_sys'}):
        dst = os.path.join(out, 'overlay', 'game', 'Tools', 'Vita', rel); os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(os.path.join(vita, rel), dst)


def fna(fna_dir, out, tmp):
    """Our FNA changes against its pinned upstream commit (tools/sources.json)."""
    names = subprocess.run(['git', '-C', fna_dir, 'diff', '--name-only', 'HEAD'], capture_output=True,
                           check=True).stdout.decode('utf-8').split()
    n = 0
    for rel in names:
        if rel in ('CLAUDE.md', 'AGENTS.md'): continue
        orig = subprocess.run(['git', '-C', fna_dir, 'show', 'HEAD:' + rel], capture_output=True,
                              check=True).stdout.decode('utf-8-sig').replace('\r\n', '\n')
        p = os.path.join(fna_dir, rel)
        diff(None, None, rel, orig, read(p) if os.path.exists(p) else None, tmp,
             os.path.join(out, 'patches', 'fna'))
        n += 1
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--source', required=True); ap.add_argument('--clean', required=True)
    ap.add_argument('--fna', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    if not os.path.isfile(os.path.join(a.clean, 'StardewValley', 'Game1.cs')):
        sys.exit('no clean decompile in ' + a.clean)
    for d in ('patches', 'overlay'):
        shutil.rmtree(os.path.join(a.out, d), ignore_errors=True)
    with tempfile.TemporaryDirectory() as tmp:
        changed, added, deleted, conflicts = game(a.source, a.clean, a.out, tmp)
        print('game: %d patched, %d added, %d deleted' % (changed, added, deleted))
        for name, (c, n) in deps(a.source, a.clean, a.out, tmp).items():
            print('deps/%s: %d patched, %d added' % (name, c, n))
        tools(a.source, a.out)
        print('fna: %d patched' % fna(a.fna, a.out, tmp))
    if conflicts:
        print('Overlapping edits resolved in favor of ours (check that the build compiles):', *conflicts, sep='\n  ')


if __name__ == '__main__':
    main()
