#!/usr/bin/env python3
"""Applies the PS Vita port edits to vitaGL's sources (build-vitagl.sh's checkout).

Usage: patch-vitagl.py <vitagl-source-dir>

Each edit names the file, an anchor that must occur exactly once, and what replaces it. A missing or
ambiguous anchor stops the script; an edit already present is left alone.
"""
import sys
from pathlib import Path

EDITS = []


def edit(path, anchor, replacement, why):
    EDITS.append(dict(path=path, anchor=anchor, replacement=replacement, why=why))


# vitaGL puts every texture in CDRAM first and in main RAM once CDRAM is full. Stardew Valley's
# textures fill the CDRAM pool, so a render target created later (the UI buffer and the cached
# pictures are recreated on every camera change) landed in main RAM wherever the free space
# happened to be. The UI buffer is entered and left about eight times a frame and each scene loads
# and stores it: in main RAM the render thread went from 20 to 52 ms a frame at the same work.
# Now CDRAM keeps vgl_vram_reserve bytes free for allocations made with vgl_vram_for_render_target
# set (FNA3D sets it while it creates a render target); other allocations take CDRAM only above
# the reserve, and fall back to it only when every other pool is full.

GPU = "source/utils/gpu_utils.c"

edit(GPU,
     why="CDRAM reserve for render targets",
     anchor="""static inline __attribute__((always_inline)) void *gpu_alloc_mapped_aligned_for_gpu_inner(size_t alignment, size_t size) {
	void *res;
	vgl_alloc_attempt(alignment, size, VGL_MEM_VRAM)
	vgl_alloc_attempt(alignment, size, VGL_MEM_RAM)
	vgl_alloc_attempt(alignment, size, VGL_MEM_PHYCONT)
	vgl_alloc_attempt(alignment, size, VGL_MEM_BUDGET)
""",
     replacement="""// Vita port (Stardew Valley): CDRAM kept for render targets, see patch-vitagl.py.
uint8_t vgl_vram_for_render_target = GL_FALSE;
uint32_t vgl_vram_reserve = 0;

static inline __attribute__((always_inline)) void *gpu_alloc_mapped_aligned_for_gpu_inner(size_t alignment, size_t size) {
	void *res;
	uint8_t vram_first = vgl_vram_for_render_target || vgl_vram_reserve == 0 || vgl_mem_get_free_space(VGL_MEM_VRAM) >= size + vgl_vram_reserve;
	if (vram_first) {
		vgl_alloc_attempt(alignment, size, VGL_MEM_VRAM)
	}
	vgl_alloc_attempt(alignment, size, VGL_MEM_RAM)
	vgl_alloc_attempt(alignment, size, VGL_MEM_PHYCONT)
	vgl_alloc_attempt(alignment, size, VGL_MEM_BUDGET)
	if (!vram_first) {
		vgl_alloc_attempt(alignment, size, VGL_MEM_VRAM)
	}
""")


def apply(root):
    applied = skipped = 0
    for e in EDITS:
        path = root / e["path"]
        text = path.read_text()
        if e["replacement"] in text:
            skipped += 1
            continue
        count = text.count(e["anchor"])
        if count != 1:
            sys.exit(f"{e['path']}: anchor found {count} times ({e['why']})")
        path.write_text(text.replace(e["anchor"], e["replacement"]))
        applied += 1
        print(f"patched {e['path']}: {e['why']}")
    print(f"{applied} edits applied, {skipped} already present")


if __name__ == "__main__":
    apply(Path(sys.argv[1]))
