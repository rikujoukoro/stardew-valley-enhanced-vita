# Vita toolchain (WSL Ubuntu)

Run inside WSL as root (`wsl -d Ubuntu -u root`), from this folder.

| Script | What it does |
|---|---|
| `install-vitasdk.sh` | Installs VitaSDK into `/usr/local/vitasdk` (toolchain + vdpm package list; `cpython` and `openssl` are no longer in the channel) |
| `build-sdl3.sh [tag]` | Builds SDL3 (default `release-3.4.16`) for the Vita into `/root/vita/prefix` |
| `build-vitagl.sh [commit]` | Builds vitaGL (default `76f43e6`) with the shader and vertex layout caches into `/root/vita/prefix`; the launcher links it before the SDK's vdpm copy |
| `build-sdl3test.sh [out.vpk]` | Builds `sdl3test/` into a VPK and optionally copies it to a Windows path |
| `build-vgltest.sh [outdir]` | Builds `vgltest/` (vitaGL alone: GLSL 1.20 shader + textured quad) into `vgltest.vpk` |
| `build-fna3d.sh` | Builds FNA3D + MojoShader as static libraries on vitaGL into `/root/vita/prefix` |
| `build-fna3dtest.sh [outdir]` | Builds `fna3dtest/` (FNA3D + SpriteEffect, 1024 sprites/frame) into `fna3dtest.vpk` |
| `build-faudio.sh` | Builds FAudio (FNA's XAudio2/XACT audio) as a static library on SDL3's Vita audio into `/root/vita/prefix` |
| `build-mono-host.sh` | Builds Mono 6.14.1 for the WSL host into `/root/vita/mono-host` (class libraries, `mcs`) |
| `build-mono-vita.sh [stage]` | Cross-builds the Mono runtime, shim and System.Native for the Vita (see `mono-vita/README.md`) |
| `build-mono-cross.sh [stage]` | Builds the cross AOT compiler (x86_64 host, Vita target) into `/root/vita/mono-cross` (see AOT below) |
| `aot-stardew.sh <game folder> [assembly ...]` | AOT-compiles assemblies into objects in `/root/vita/build-aot` for `AOT_DIR=... build-stardew.sh` |
| `build-monotest.sh [outdir]` | Builds `monotest/` (embedded Mono running hello.exe) as `monotest.vpk` (interpreter) and `monotest-jit.vpk` |
| `build-exectest.sh [outdir]` | Builds `exectest/`, the kubridge executable-memory experiment (no Mono) |
| `build-fnatest.sh [outdir]` | Builds `fnatest/` (an FNA Game in C# on the Vita Mono runtime) into `fnatest.vpk` |
| `build-stardew.sh [outdir]` | Builds `stardew/`, the game launcher (runtime, class libraries, FNA.dll; no game files) into `stardew.vpk` |
| `stage-stardew.ps1` (Windows) | Builds the `ux0:data/StardewValley` folder from the player's own files: our game build and their Content, with streaming wave banks |
| `gen-native-table.sh` | Generates a `[DllImport]` table (name -> address) from static libraries, with optional overrides |

Running Mono on the device needs `kubridge.skprx` (bythos14 v0.3.1_hotfix) in `ur0:tai/` under
`*KERNEL`, besides `libshacccg.suprx` for graphics.

## Stardew Valley on the Vita (milestone 5)

Nothing of the game is in the VPK. `stardew.vpk` (STDV00001) holds the launcher with the runtime,
Mono's class libraries and FNA.dll; the game runs from `ux0:data/StardewValley`, built from the
player's own copy:

1. Windows: `dotnet build Stardew-Valley.csproj -c Release`, then `Tools\Vita\stage-stardew.ps1`. It
   writes `vita-install\StardewValley` next to the repository: the game assemblies plus the PC game's
   Content (~541 MB), with both wave banks converted to streaming banks.
2. WSL: `bash build-stardew.sh <outdir>`.
3. Copy `vita-install\StardewValley` to `ux0:data/` and install `stardew.vpk` (kubridge and
   libshacccg.suprx as above).

The launcher changes directory to the game folder and serves it as SDL's base path (FNA's
TitleLocation), sets the clocks to 444/222/222/166 MHz (500 MHz ARM needs a kernel plugin such as
PSVshell), keeps saves, `startup_preferences` and `ErrorLogs/` in the same folder (XDG dirs), turns
off Mono's XmlSerializer code generation (it would run mcs), and passes the assembly search path with
`mono_set_assemblies_path_direct` (`mono_set_assemblies_path` splits at ':'). Logs:
`vita-log.txt` (runtime, SDL/vitaGL messages, memory every 10 s), `ErrorLogs/game-latest.txt` (game
log) and `ErrorLogs/console.txt` (FNA). Quit through the game's Exit buttons: Run() then returns
and the launcher releases the JIT memory; the game no longer kills its own process on the Vita.

Game changes for the Vita (`VitaPlatform.IsVita`, from SDL's platform name):

- 960x544 fullscreen back buffer; every window mode stays fullscreen. World and UI zoom carry a 0.75
  modifier (as split-screen uses it): the 1280x720 layout the UI needs, at exactly 3 screen pixels
  per art pixel. On top of that the zoom level is fixed at 105% and the UI scale at 90% (title screen
  included), with the toolbar pinned (`VitaPlatform.BaseZoomLevel`/`BaseUIScale`,
  `Options.platformClampValues`); the options page doesn't offer them, nor window mode/resolution.
- The intro's bus ride sizes its road from `Game1.viewport` (the buffer minigames are drawn into
  when zoomed) instead of the back buffer, which left the right quarter of the screen unpainted.
- Gamepad only: gamepad mode is forced on, and the mouse is virtual (SDL's mouse ignores warps there,
  which read as mouse motion). No split-screen (its setup reflected over every type: ~160 s).
- Controls follow the official Vita release (1.2, by Sickhead Games): Cross check, Circle cancel/menu,
  Square use tool, Triangle crafting, L/R swap tool, Select journal, Start menu, and a tap on the left
  or right half of the rear touchpad shifts the toolbar (LT/RT, the Vita has no L2/R2). The launcher
  serves the rear-panel taps as `[DllImport("VitaPort")]`.
- Text boxes open the system keyboard on Cross (SDL text input = `sceImeDialog`); the glue swaps with
  `vglSwapBuffers(GL_TRUE)` while it is open, otherwise the undrawn dialog swallows the buttons.
- The game's background tasks (load, save, new day) run on the main thread: saving on a thread-pool
  thread ended in a fatal assertion in Mono's cooperative GC suspend.
- XML serializers are pregenerated (`Tools/SerializerGen`, C# from .NET Framework's generator, in
  `StardewValley.SaveSerialization/Generated/`). Built at run time they took ~150 s on the Vita.
  `Tools/SaveRoundTrip/run-roundtrip.ps1` checks that saves stay byte-identical to the original exe
  (.NET and Mono). Regenerate with `Tools/SerializerGen/regenerate.ps1` after changing saved types.
- `VitaPlatform.Trace` writes startup steps and, every 10 s, fps with the CPU time of Update and Draw
  (and of their main parts) to `ErrorLogs/console.txt`; `vita-log.txt` adds JIT/GC/exception counts
  and the GPU swap wait. Parts timed per location use `BeginHotSection`/`EndHotSection` (fixed
  slots, no dictionary): "here" is the current location's update, "all locations" every location's
  off-screen update and "all locations: npcs" the NPCs in it.
- Two behavior-preserving shortcuts for the per-tick update: collision checks read bushes' bounding
  boxes from a per-location cache (`GameLocation.GetLargeTerrainFeatureBounds`, rebuilt when the
  collection or any large terrain feature's tile changes), and `Object.updateWhenCurrentLocation`
  returns at once for objects it has nothing to do for (no held object, light, animation, shake,
  harvest flag or id-specific work).

Switches in `ux0:data/StardewValley` (no new build needed):

- `profile-calls` (folder or file): sampling profiler. The game's own assemblies are instrumented
  (a shadow call stack of the main thread), a native thread on another core samples it every
  millisecond, and `vita-log.txt` lists the 25 most expensive methods by self and inclusive time
  every 10 s. The game runs several times slower meanwhile; the proportions are what count.
- `profile-samples` (folder or file): stack sampling without instrumentation
  (`mono-vita/vita-sampler.c`, built by `build-mono-vita.sh sampler`). Every 10 ms a native thread
  stops the main thread at its next GC safepoint (coop suspend, as the GC does), walks its managed
  stack with Mono's unwinder and resumes it; `vita-log.txt` gets `[samples]` lines every 10 s with
  the top methods by self and inclusive share, the callers of wrappers on top, and the heaviest
  call edges (`edge  callee  <-  caller`, the trunk above 30% left out). The game keeps nearly full speed. Native time
  (vitaGL, SDL, FAudio) shows under the P/Invoke wrapper that called it; GC pauses as time waited
  for the suspend lock.
- `profile-sections`: the fine-grained timing (per location, per NPC) in `console.txt`'s
  `per frame:` lines. Off by default: its clock reads cost 4-6% of the main thread.
- `jit-log`: every method compiled after the first 60 s gets a `jit` line in `vita-log.txt`
  (buffered, written every 10 s). `make-prejit-list.sh <vita-log.txt> <seconds>` turns a session
  into `vita-prejit.txt`.
- `Content.pak` (built by `make-content-pack.py <Content dir> <Content.pak>`, ~60 MB): every .xnb, all
  languages, in one file, read by `VitaContentPack`. Opening a file on the memory card cost
  ~100 ms, so small assets took 100-300 ms each (map tile sheets on walking into a location, the game
  menu's textures). Rebuild it when Content changes; without it the loose files are read.
- `vita-controls.png` (from `stardew/vita-controls.png`, 515x290): the official Vita release's controls
  diagram, drawn on loading screens (`Game1.DrawVitaLoadingContents`). Without the file the loading
  screen shows only the "Loading" text.
- `vita-prejit.txt` (from `stardew/vita-prejit.txt`): methods `VitaPlatform.PrepareHotMethods`
  compiles behind the save's loading screen, once a session. The game's per-frame code stays JIT
  (AOT ran it about twice as slow), so the first minute of play compiled ~1,600 methods in 100-700 ms
  stalls; now that happens while loading. It compiles through `MethodHandle.GetFunctionPointer()`:
  Mono's `RuntimeHelpers.PrepareMethod` is empty and compiled nothing.
- `vita-close-zoom`: written by the options page's close-up camera checkbox (133% world zoom).
- `mono-options.txt`: words passed to `mono_jit_parse_options`, e.g. `-O=all`.
- `no-map-buffers`: SpriteBatch goes back to Discard + `glBufferSubData` (see below).

FNA has two local changes:

- `fna-vita/fna-stable-sprite-sort.patch`: sorted SpriteBatch modes keep submission order for equal
  depths. `Array.Sort` is unstable and Mono breaks ties differently, which hid text boxes and labels
  under menu backgrounds drawn at the same depth.
- `fna-vita/fna-gamepad-state-cache.patch`: `GamePad.GetState` answers repeat queries from a cache
  until the next event poll. Each query read ~25 values through separate P/Invokes and the game asks
  several times a frame: 13% of the main thread on the farm (`profile-samples`).
- `fna-vita/fna-spritebatch-tiles.patch` (applies on top of the spritebatch-perf/norotation/stable-sort patches):
  `SpriteBatch.DrawTileEXT` for xTile map tiles (called by `LowRamDisplayDevice`), an unrotated/unflipped fast
  path in `GenerateVertexInfo`, one `Nullable<Rectangle>.Value` read per `Draw`, the texture hash only in
  Texture sort mode, and a radix sort that counts all four byte histograms in the key pass and skips
  already-ascending batches. Output vertices are the same as before.

Integer division: the Vita's Cortex-A9 has no divide instruction, so Mono calls a helper for every
32-bit `/` and `%`. `mono-vita/patch-mono.py` has the JIT (and the AOT cross compiler) check the
operands inline and call the helpers without an icall wrapper (5% of the main thread, mostly
Dictionary lookups).

vitaGL's RAM pool is 64 MB for the game (`FNA3D_VITAGL_RAM_POOL_MB`, set with `SDL_SetHint`: SDL keeps
its own environment and ignores newlib's `setenv`). vitaGL sleeps one second per failed allocation
(0-1 fps). The launcher also sets `FNA3D_OPENGL_ALLOW_MAP_UNSYNCHRONIZED`: SpriteBatch appends to its
vertex buffer with NoOverwrite, and through `glBufferSubData` vitaGL copied the whole 196 KB buffer
into a new allocation each time the GPU might still read it. That filled the RAM pool, and when an
allocation failed vitaGL dropped the update: the end of a batch vanished (character creation's text
boxes) or drew stale vertices (garbled bus scene). `glMapBufferRange` writes the buffer in place.
With the hint SpriteBatch appends (NoOverwrite) and draws with a base vertex. FNA3D enables base
vertex only if `glDrawElementsInstancedBaseVertex` exists too, which vitaGL lacks; its fallback adds
the offset to the attribute pointers, which vitaGL ignores for an interleaved buffer, so draws read
the buffer's first vertices (black, flickering frames). The glue provides the instanced entry point
so FNA3D uses vitaGL's `glDrawRangeElementsBaseVertex`, and turns off vitaGL's per-frame scratch
memory for dynamic buffers (`vglSetupScratchMemory`), since SpriteBatch's buffer now lives across
frames. A folder or file named `no-map-buffers` in `ux0:data/StardewValley` turns the hint off again.

vitaGL's CDRAM pool: vitaGL initializes GXM (16 MB parameter buffer in CDRAM) before it creates its
pools, so a pool sized to the CDRAM free before init failed and vitaGL ran without one (the "only
16 MB of CDRAM used" seen in milestones 2 and 4). The default is now the free CDRAM minus 16 MB.

Render targets: FNA3D's OpenGL driver draws to every render target through one framebuffer,
attaching the target's texture when it changes, and vitaGL throws a framebuffer's GXM render target
away whenever an attachment of another size is attached (a new `sceGxmCreateRenderTarget` at the next
scene, the old one freed frames later). Stardew switches between its world buffer, UI buffer and
light map several times a frame; once the UI scale differed from the zoom the two buffers differed
in size, and fps fell to 8-9 with one-second allocation stalls. `fna-vita/patch-fna3d.py` gives each
depthless 2D render target texture a framebuffer of its own, and the glue lets render targets take
4 scenes per frame (`FNA3D_VITAGL_FBO_SCENES`, 1-8; with 1 a second scene on the same target waits
for the GPU).

## AOT: precompiled code in the eboot (milestone 6, in progress)

The JIT spent ~40 s per session compiling (~13,600 methods, 12 MB of code): long loads and stalls the
first time any code runs (a menu, a gift box). AOT moves that to build time; steady-state code quality
is about the JIT's, so fps gains come from elsewhere.

- **Cross compiler** (`build-mono-cross.sh`): Mono 6.14.1 for the WSL host, configured
  `--target=arm-vita-eabi`, from its own copy of the patched source. configure's Vita cross target
  (`patch-mono.py`) sets the runtime's ABI: hard-float VFP, no unaligned access, and `TARGET_VITA`.
  It is built with cooperative suspend (images then carry GC safepoints, which the runtime demands)
  and the same `--enable-minimal` set.
- **Struct offsets** (`mono-vita/gen-cross-offsets.sh`): AOT code reads runtime structures at fixed
  offsets, so the cross compiler needs the Vita runtime's layout. They come from the Vita compiler, as
  Linux generates asm-offsets.h: `object-offsets.h` is compiled with the runtime's own flags and
  config.h, each entry turns into an asm marker carrying its value, and the markers are read back from
  the assembly into the `--with-cross-offsets` header. Nothing runs on the device.
- **Images** (`aot-stardew.sh`): `--aot=static,asmonly,nodebug,mtriple=armv7-vita-eabi` per assembly,
  with `MONO_DEBUG=explicit-null-checks` as on the device; the assembly is linked as an object, and
  `vita_aot_modules.c` lists the `mono_aot_module_<name>_info` symbols. The launcher registers them
  before `mono_jit_init` (`register_aot_modules`). Mono uses an image only for the assembly it was
  compiled from (same GUID); anything else is JIT-compiled as before, so a newer game exe still runs.
  Relocations are only `R_ARM_ABS32`, `R_ARM_CALL` and `R_ARM_REL32`, which vita-elf-create handles.
- **Size.** Everything compiled is ~50 MB loaded (Stardew Valley 23.8 MB, mscorlib 8.9, System.Xml
  7.8, System 5.9, System.Core 2.6, FNA 1.6), too much next to the 230 MB newlib heap. mscorlib,
  System.Core, FNA, xTile, StardewValley.GameData and BmFont are compiled whole (~13 MB); the rest is
  to be profile-guided.
- **Profile-guided AOT.** A file or folder named `aot-profile` in `ux0:data/StardewValley` turns on
  Mono's AOT profiler (Vita copy without sockets: `mono-vita/patch-aot-profiler.py`, built by
  `build-mono-vita.sh aotprof`), which records every JIT-compiled method in `stardew.aotprofile`,
  rewritten at most once a minute (a short stall) and when Main returns. Methods are matched by name
  and signature, so a profile outlives game rebuilds. `AOT_PROFILE=<file>
  PROFILE_ONLY="System:System.Xml:Stardew Valley" aot-stardew.sh ...` compiles only those.
- `aot-log` (switch): Mono's AOT log at debug level (which images load, every method lookup).

## FNA on Mono (milestone 4)

FNA's `[DllImport("SDL3")]` and `[DllImport("FNA3D")]` resolve against tables generated from the
static libraries (`gen-native-table.sh`; 1878 SDL3 and 147 FNA3D/MojoShader functions), registered
by the launcher with `vita_native_register_library`. `fna-vita/` holds the Vita replacements served
under SDL names (`sdl3.overrides`): `SDL_GetBasePath`/`SDL_GetPrefPath` return rooted paths
(`/app0:/`), so FNA's TitleLocation and storage paths survive .NET's Unix `Path`. FNA itself is
unchanged (the net4.0 build, shipped without its Linux `FNA.dll.config`).

arm-vita-eabi defaults to **1-byte enums** and SDL skips its enum-size check on the Vita, but SDL3-CS
and FNA marshal every enum, in structs too, as a 4-byte int: SDL3, FNA3D and MojoShader (and any C
using their structs) are built with `-fno-short-enums`. Without it, the first SpriteBatch failed
parsing MojoShader's effect structs.

Real device (2026-10-22): `fnatest` (FNA Game, SpriteBatch, 1024 sprites per frame, gamepad) runs at
**60 fps** (vsync) after a 54 fps first five seconds, with no garbage per frame, reads content
through TitleContainer, exits cleanly on Start. Runtime start ~1 s. MAIN used outside the newlib heap
~44 MB (vitaGL's 32 MB pool, 4 MB JIT code, SDL); newlib heap 36.7 MB at exit.

Audio (2026-10-22): `[DllImport("FAudio")]` resolves against a third table (296 FAudio/FACT/F3DAudio
functions). On the device a looping SoundEffectInstance with a pitch sweep plays alongside one-shot
SoundEffects (46 chimes in 18 s), clean, through SDL3's Vita audio driver. fps stays at 59.5-59.9
with FAudio's scalar mixer (its SIMD paths are AArch64-only); MAIN use did not measurably change.

`sdl3test` opens a window with SDL's Vita renderer, reacts to touch and buttons, and writes the
renderer name and the kernel's free-memory figures to `ux0:data/sdl3test/log.txt`; it quits after
20 s or on Start.

First run in Vita3K (2026-10-21): renderer `VITA gxm`, 1 gamepad, touch events delivered; free memory
before SDL_Init: user 131072 KB, CDRAM 114688 KB, phycont 26624 KB (after window+renderer: user
119868 KB, CDRAM 108544 KB). These are Vita3K's numbers; the real-device budget still has to be
measured with the same VPK.

## Memory budget (sdl3test v2: malloc probe + free memory)

`build-sdl3test.sh` now builds two VPKs: `sdl3test.vpk` (SDLT00001, default) and
`sdl3test-extmem.vpk` (SDLT00002, `ATTRIBUTE2=12` in param.sfo).

| | free user | malloc-able | total | source |
|---|---|---|---|---|
| default, real Vita | 120 MB | 127 MB | ~247 MB | device |
| ATTRIBUTE2=12, real Vita | 229 MB | 127 MB | **~356 MB** | device |
| default, Vita3K | 128 MB | 127 MB | ~256 MB | Vita3K |
| ATTRIBUTE2=12, Vita3K | 237 MB | 127 MB | ~365 MB | Vita3K |

CDRAM 112 MB and phycont 26 MB in every case. newlib reserves a fixed 128 MB heap at startup that
does not grow with the extended budget, so the extra ~110 MB is not reachable through malloc by
default: Mono will need a larger newlib heap or to allocate from the kernel directly.

Real device (HENkaku Vita, 2026-10-21): the extended budget is **~356 MB of MAIN memory** (the
project's "350 MB" limit), plus 112 MB CDRAM and 26 MB phycont. SDL3's window + GXM renderer alone
took 13 MB MAIN and 22 MB CDRAM (Vita3K: 11 MB / 6 MB). Both variants ran the full 20 s at ~770 fps
(no vsync). Plan the game for the extended mode, with textures in CDRAM where possible.

## Graphics: FNA3D on vitaGL (milestone 2)

vitaGL needs `libshacccg.suprx` in `ur0:data/` (dump it on the device with ShaRKF00D; never
distributed with the project). Vita3K hangs in vitaGL's first `glClear`, so drawing is tested on
the device; Vita3K is still useful for everything before the first frame.

FNA3D runs its desktop OpenGL 2.1 driver with MojoShader's `glsl120` profile. The glue in
`fna3d-vita/` is local port code, never sent upstream (FNA3D does not accept AI-written code):

- `vita_gl_glue.h` is force-included into FNA3D/MojoShader and renames the `SDL_GL_*` calls (our SDL3
  has no GL backend) to `vita_gl_glue.c`, which drives vitaGL directly.
- `glGetString` reports GL 2.1 / GLSL 1.20 (vitaGL says ES 2.0 / 1.00 ES, which makes MojoShader
  refuse `glsl120`); the seven entry points vitaGL lacks get replacements (`glGetTexImage` via a
  temporary framebuffer, `glGetBufferSubData` via `glMapBuffer`, the rest warn once).
- `vita_glsl_rewrite.c` turns MojoShader's legacy `gl_Color`/`gl_FrontColor`/`gl_TexCoord[n]` into
  explicit varyings; vitaGL's translator does not know them and its `glLinkProgram` then crashes on
  the uncompiled shader.
- vitaGL's pools are sized explicitly (`FNA3D_VITAGL_RAM_POOL_MB`, default 32;
  `FNA3D_VITAGL_CDRAM_POOL_MB`, `FNA3D_VITAGL_PHYCONT_POOL_MB`); left alone vitaGL takes ~211 MB MAIN.
  `FNA3D_VITAGL_SHADER_LOG=<path>` logs every shader source and compile/link result.
- `build-fna3d.sh` copies FNA3D into `/root/vita/src` and drops the SDL_GPU driver, MojoShader's
  SDL_GPU backend and unused shader profiles; the FNA checkout stays untouched.

Real device (2026-10-22): SpriteEffect, 1024 sprites per frame from a dynamic vertex buffer,
**55 fps over 20 s** (60 after the first-frame shader compile); `GetTextureData2D` exact. FNA3D +
vitaGL took ~38 MB MAIN. With the CDRAM pool set to "all free CDRAM" only ~16 MB of CDRAM was used
(the pool allocation failed; fixed in milestone 5, see above). libFNA3D.a 198 KB, libmojoshader.a 187 KB.
