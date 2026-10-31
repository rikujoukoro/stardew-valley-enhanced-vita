# Mono on the PS Vita (milestone 3)

Mono 6.14.1 (WineHQ's continuation of 6.12) cross-built with VitaSDK as a static library, embedded
by a launcher. `build-mono-host.sh` builds the same version for the WSL host (class libraries, `mcs`,
later the cross AOT compiler); `build-mono-vita.sh` builds the Vita runtime.

## Design

- **No signals.** The Vita has no asynchronous signals: the runtime is built with
  `--enable-cooperative-suspend` (GC suspension never signals a thread), single-argument signal
  handlers like watchOS (`MONO_ARCH_USE_SIGACTION` off), and a stubbed signal suspend backend.
  JIT code uses explicit null checks (`MONO_DEBUG=explicit-null-checks`, set by the launcher).
- **No mmap.** Mono's malloc-based `mono_valloc` and read-based file maps are used, so every GC and
  runtime allocation comes from the newlib heap (`_newlib_heap_size_user`, one number to budget).
  SGen therefore never returns empty major blocks (as on Windows/WASM: they cannot be freed from the
  middle of a run) and sweeps serially (no sweep worker thread).
- **Executable memory** for JIT code and trampolines needs **kubridge** (bythos14 v0.3.1_hotfix,
  `ur0:tai/kubridge.skprx` under `*KERNEL`). Mono writes code from any thread; VM-domain blocks
  (`sceKernelAllocMemBlockForVM`) only let the one thread that opened the domain write, so second
  threads fault on the device. kubridge arenas are `kuKernelMemReserve` ranges committed RWX once,
  whole, and decommitted once before exit (`vita_exec_shutdown`, which the host must call); caches
  are synced with `kuKernelFlushCaches`. The device runs that ruled out the alternatives (direct RWX
  block type, multi-commit ranges, base-backed views, exiting with committed memory) are listed in
  `vita-shim/vita_shim.c`; `exectest/` reproduces them. Without kubridge the shim falls back to a
  VM domain, which is enough only while code is written on the main thread.
- **Paths.** Managed code sees device paths rooted at `/` (`/ux0:/data/x`, `getcwd` = `/app0:/`) so
  .NET's Unix `Path` keeps them intact; `vita-shim/vita_paths.c` wraps the libc path functions
  (`-Wl,--wrap`, see `vita-mono.cmake`) and translates back to `ux0:/data/x`.
- **P/Invoke** without a dynamic loader: `System.Native` (CoreFX PAL, file IO etc.) is compiled from
  Mono's tree and served from a generated name -> address table through a Mono dl fallback
  (`vita-native/`). SDL3, FNA3D and FAudio will be registered the same way.
- **Clocks.** pthread-embedded measures timed waits against `_ftime`; Mono's condition waits use
  `gettimeofday` deadlines on the Vita (`BROKEN_CLOCK_SOURCE`), else every timed wait returns at once.
- **CPU features** are fixed (Cortex-A9: ARMv7, Thumb-2, VFPv3-D32, NEON): no auxv or /proc.
- Left out: sockets, perf counters, attach, debugger agents, COM, BTLS, the SGen concurrent
  collector. FileShare emulation is off (newlib's stat has no inode numbers; one process).

All Mono source edits are in `patch-mono.py` (anchor-checked, idempotent); newlib gaps are in
`vita-shim/`. Findings credited to Duro02/vita-mono-port (MIT): VM-domain executable memory, the
no-op icache flush, newlib's broken `getrusage`/clock behaviour.

## Status

`monotest` (hello.exe: generics, LINQ, exceptions, null/div/bounds checks, reflection, threads,
Monitor, thread pool, nursery and full GCs, finalizers, file and directory IO, XML) passes every
check **on the real Vita** in both modes, exits cleanly and leaves the system stable (2026-10-22,
HENkaku, kubridge v0.3.1_hotfix):

| mode | runtime start | Main | Fib(24) | 2M float loop | generic dictionary (10k) | code memory | newlib heap at exit |
|---|---|---|---|---|---|---|---|
| interpreter (`monotest.vpk`, MONO00001) | 0.99 s | 7.3 s | 115 ms | 1288 ms | 807 ms | 4.2 MB | 31.5 MB |
| JIT (`monotest-jit.vpk`, MONO00002) | 0.98 s | 6.0 s | 8 ms | 139 ms | 182 ms | 4.8 MB | 27.9 MB |

JIT is about 10x faster than the interpreter on compute. Code memory includes the launcher's 4 MB
executable-memory experiment. eboot (runtime with interpreter and JIT): 2.1 MB. VPK with mscorlib,
System, System.Core, System.Xml, System.Configuration, Mono.Security: 6.3 MB.

Vita3K differs from the device: it enforces neither execute permission nor per-thread VM-domain
access, does not implement the RWX block type, and keeps stale JIT translations when code memory is
reused after `kuKernelFlushCaches`.
