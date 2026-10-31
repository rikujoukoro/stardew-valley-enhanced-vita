# Texture prebake pilot

Offline texture tool. Original loose XNBs and the install tree stay untouched.

## Active two-texture trial (2026-10-27)

The user requested activation after the canopy correction. The corrected stb spring/summer
outputs are now staged in `vita-deploy/Content.pak`. All 3,548 other entries are byte-identical.
`patch-content-pack.py input.pak correction-directory output.pak` verifies the original source
hashes, encoder and output manifests, pack bounds, and the exact two-entry change set. It never
overwrites its input. The original pack and card files are backed up under
`scratchpad/444-target-before-20261027/`. Pack SHA256 after replacement:
`b3c3b9c16175cee19ec6ebe7e2bc7a9e5ff1d68e85439179eaa59e2730ab61d6`.
Vita rendering, loading time, memory and FPS still require device testing. The following pilot
notes describe historical outputs; only the corrected stb outputs are selected for this trial.

## Completed pilots

- Source: `vita-install/StardewValley/Content/Maps/spring_outdoorsTileSheet.xnb` (XNB `w`, version 5, flags `0x81`, 149,096 bytes).
- Source SHA256: `170b3c3d4adb8f0b2954b86caca9611a115d8232ac795fe0752c62381bf795b9`.
- FNA's own LZX decoder opened the XNB. The contained texture is `SurfaceFormat.Color`, 400×1264, one mip, 2,022,400 RGBA bytes. Both dimensions are divisible by four.
- Pillow 12.3.0's DDS `bcn` encoder produced BC3/DXT5. The payload is 505,600 bytes, 75% smaller than the raw texture bytes. The source pixel bytes were passed through unchanged, preserving premultiplied-alpha values.
- Spring output: `scratchpad/texture-prebake/spring-bc3/spring_outdoorsTileSheet-bc3.xnb`; its SHA256 is recorded in the manifest. The DDS was decoded back to a PNG preview and visually inspected. The emitted XNB was parsed again by the FNA-LZX helper: format 6 (Dxt5), 400×1264, one mip, 505,600 payload bytes.
- Summer output: `scratchpad/texture-prebake/summer_outdoorsTileSheet-bc3/summer_outdoorsTileSheet-bc3.xnb`; source SHA256 is pinned in the allowlist and output SHA256/measurements are in its manifest. The source PNG and three nearest-neighbor source/BC3 crop comparisons are saved alongside it. Pixel error against the BC3-decoded DDS: alpha MAE 0.4346 (max 51); premultiplied RGB MAE 7.4202 (max 255). The tree canopy crop shows visible block-level color shifts; root review should decide if the texture is acceptable for a device trial.
- The helper rejects nonzero shared-resource counts and any trailing decoded XNB bytes after the single-mip payload. The output body preserves any tail bytes when rebuilding.
- This is not active game content. No game, FNA, native, or install files were modified. No Vita test has been performed.

## Reproduce

From the repository root, run `SV_PC_source/Tools/Vita/TexturePrebake/build-helper.ps1`, then build the offline encoder:

`wsl.exe -u root -- bash "/mnt/c/Users/mamik/Desktop/stardew vita 1.6/SV_PC_source/Tools/Vita/TexturePrebake/build-encoder.sh"`

Then run:

`C:\Users\mamik\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe SV_PC_source/Tools/Vita/TexturePrebake/prebake.py spring_outdoorsTileSheet.xnb`

The script has an exact two-file allowlist and pins each input SHA256. The corrected encoder writes to `scratchpad/texture-prebake/encoder-correction-20261027/<asset>-bc3-stb/`. The old Pillow pilot folders are preserved.

## Corrected BC3 encoder (2026-10-27)

The Pillow RGB encoder chooses endpoint colors by numeric RGB565 minimum/maximum and selects indices from a heuristic sum of channel distances, rather than nearest decoded palette color. Solid/channel/alpha fixtures showed no channel-order, row-stride, or double-premultiplication problem. The mixed-color canopy exposed a large palette error: source RGBA `[12,133,94,253]` at (68,31) became `[255,231,0,253]` with Pillow.

`encode-bc3.c` now uses upstream `stb_dxt.h` v1.12 `STB_DXT_HIGHQUAL`, without dithering or RGBA/alpha/color-space conversion. Source: https://github.com/nothings/stb/blob/master/stb_dxt.h . The header retains its full public-domain/MIT dual license; SHA256 is pinned in the build script and manifests (`807667ef98e0fd749cdb65cca0c2d980bc148109d2fed6f1873c81ae0f449933`). WSL gcc builds a host utility in scratchpad; it is not a Vita runtime dependency. It reads four RGBA rows at a time.

Summer RGB MAE improved 7.4202 → 4.4659, alpha MAE 0.4346 → 0.0763. The reviewed leaf is now `[65,109,52,253]`: its former bright yellow corruption is removed, while ordinary BC3 palette loss remains. Spring RGB MAE is 4.3682, alpha MAE 0.0734. Both rebuilt XNB files reparse as Dxt5/400×1264/one mip/505600 bytes through the FNA helper. Neither is lossless; neither has been tested on Vita.

`test-encoder.py` checks solid colors, channels, row stride, constant alpha, alpha gradients and the canopy regression. An independent BC3 decoder is compared with Pillow's decoder (≤1 interpolation rounding difference). `diagnose.py` records the old Pillow failure, and `compare-encoders.py` records fixed-source quality metrics. Test evidence and new previews are in the correction folder. The prior `prebake.py` is backed up there as `prebake-before.py`.

## Boundaries

These trials cover two static map tilesheets. The fall and winter tilesheets remain candidates; each needs the same source/hash check and review. Farmer, ControllerMaps, paint masks, readback-mutated textures, and render targets are excluded. Wider integration requires Vita visual quality and loading validation of this two-texture trial.
