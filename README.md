# Stardew Valley Enhanced for PS Vita (unofficial) — build it yourself

**This repository does not contain Stardew Valley.** It turns your own legal copy (Steam, compatibility branch,
version 1.6.15) into a PS Vita build on your PC: the builder decompiles your copy, applies the port's changes and
builds it. No game code, art or audio is distributed here. Not affiliated with ConcernedApe, Sickhead Games or Sony.
Please buy the game.

## What you get

- Stardew Valley 1.6.15 on the PS Vita: 960x544, controller layout of the official Vita release, 350 MB memory budget
- Mods menu: tractor, info overlays, chest helper, three extra ring slots, passable crops
- 12 languages, trophies (with NoTrpDrm), LAN co-op groundwork

## Requirements

- **PC:** Windows 10 (up to date) or 11, about 5 GB free. **Experimental:** Linux (x86_64, glibc 2.29 or newer:
  Ubuntu 20.04+, Debian 11+, Fedora, Arch, SteamOS...) and macOS through Docker (below): both were only tested in a
  Linux test setup (a Debian 11 system and the Docker image on x86_64), never on a real Linux PC or a Mac, so please
  report how they go.
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (the SDK, not only the Runtime; a newer SDK works too).
- This project's release download for your system: **StardewVitaBuilder-windows.zip** (this folder with
  `vita-toolchain-windows.zip` already inside), or this folder plus `vita-toolchain-linux.zip`. The toolchain zip
  holds Mono for the Vita, SDL3, vitaGL, FNA3D and FAudio, no game files. No WSL or VitaSDK install needed: the
  builder downloads the official VitaSDK for your system by itself (~45-70 MB, once).
- **Vita:** HENkaku/Ensō, VitaShell, `kubridge.skprx` (v0.3.1_hotfix) under `*KERNEL` in `ur0:tai/config.txt`,
  `libshacccg.suprx` in `ur0:data/`. Optional: NoTrpDrm for real trophies.

## 1. Get the right game version

The builder needs the game's **Windows** files, Steam version 1.6.15 from the **compatibility** branch. It checks
their SHA-256 (`tools/known-games.json`) and stops on any other version. GOG is not supported yet.

- **Windows:** Steam → Library → Stardew Valley → Properties → Betas → choose **compatibility**, let it update.
- **Linux** (experimental): the same, and also Properties → Compatibility → *Force the use of a specific Steam Play
  compatibility tool* (any Proton), so Steam downloads the Windows files. You don't have to play it that way.
- **macOS** (experimental): Steam for Mac downloads the Mac files, so get the Windows ones with
  [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) (your own Steam login):

  ```
  steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir ~/StardewWindows +login <your Steam name> +app_update 413150 -beta compatibility validate +quit
  ```

## 2. Build

**Windows:** unzip StardewVitaBuilder-windows.zip somewhere plain and short like `C:\Games` (not in OneDrive:
Windows often keeps Desktop and Documents there), open the `StardewVitaBuilder` folder and double-click
**Build.bat**. When it's done, the folder to copy opens by itself.

**Linux** (experimental): put `vita-toolchain-linux.zip` in this folder and run `bash build.sh` in a terminal.

**macOS** (experimental, never tried on a Mac): install Docker Desktop (or OrbStack/Colima; on Apple Silicon keep
Docker Desktop's *Use Rosetta for x86_64/amd64 emulation* on), then in Terminal, in this folder:

```
bash tools/docker-build.sh --game ~/StardewWindows --toolchain ~/Downloads/vita-toolchain-linux.zip
```

The builder finds your Steam install and the toolchain zip next to it by itself (or add
`--game "<folder with Stardew Valley.exe>"` and `--toolchain <zip>`, also to Build.bat from a command prompt), then:
verifies the game, downloads and builds FNA, decompiles the game with a pinned ILSpy, applies `patches/` and
`overlay/`, builds it, stages `work/out/ux0/data/StardewValley` (your Content with streaming wave banks,
`Content.pak`) and the VPK's art (icon, LiveArea, manual and trophy pack, drawn from your Content like the title
screen), then AOT-compiles the game for the Vita, links the eboot and packs `work/out/ux0/stardew.vpk`.
About 4 minutes the first time, 2-3 minutes after that. On Windows, if the folder's path is long or has letters like
ç, ğ, ü, the Vita tools work in `C:\ProgramData\StardewVitaBuilder` instead (~1 GB, safe to delete afterwards; or
pass `--native-dir C:\svb`). Everything else stays in `work/`; delete it to start over.

## 3. (Optional) Build the VPK in WSL instead

`--no-vpk` stops after step 2's staging. The same VPK can then be made in WSL2 (Ubuntu) with VitaSDK in
`/usr/local/vitasdk`, `cmake`, `ninja-build`, `python3`, `python3-pil` and `vita-toolchain.tar.xz` extracted into `/`
(`sudo tar -C / -xJf vita-toolchain.tar.xz`):

```
sudo bash vpk/build-vpk.sh /mnt/c/<path>/vita-builder/work
```

## 4. Install

`work/out/ux0` is laid out like the memory card: `data/StardewValley/` (~600 MB) and `stardew.vpk`.

1. In VitaShell press SELECT (USB) or connect over FTP, and copy everything inside `work/out/ux0` to `ux0:`
   (let it merge with the existing `data` folder).
2. In VitaShell open `ux0:stardew.vpk` and install it; then you can delete the .vpk.

Saves live in `ux0:data/StardewValley/Saves`; the copy and reinstalling the VPK keep them.

## For maintainers

- Release bundle: `StardewVitaBuilder-windows.zip` is this folder (without `work/`, `builder/bin`, `builder/obj`)
  as `StardewVitaBuilder/` with `vita-toolchain-windows.zip` inside, next to Build.bat.
- `tools/make-patches.py` regenerates `patches/` and `overlay/` from the development tree (see its header).
- `vpk/pack-toolchain-zip.sh windows|linux <zip>` packs the toolchain zips (needs the AOT compiler for that system:
  `vpk/build-mono-crosswin.sh`, built in WSL with mingw-w64, or `vpk/build-mono-crosslinux.sh`, built in a Debian 11
  chroot for an old glibc); `vpk/pack-toolchain.sh` packs the WSL archive.
- `assets/xmlserializers.bin`: the game's pregenerated XML serializers for builds outside Windows (SerializerGen
  needs .NET Framework), encrypted with a key from the game's own `Stardew Valley.exe`. Whenever `patches/` or
  `overlay/` change, a Windows build says so; run it once with `--save-serializers` to write a new one.
- `tools/controls-art/` draws `assets/vita-controls.png` and `assets/manual-002.png`.
- Licenses: `THIRD_PARTY.md`.
