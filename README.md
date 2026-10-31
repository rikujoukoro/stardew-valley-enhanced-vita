# Stardew Valley Enhanced

An unofficial PS Vita port of Stardew Valley 1.6, built on your own PC from your own copy of the game.

> **GOG is not supported yet.** I don't own Stardew Valley on GOG, so I can't make or test a GOG version. If you have
> the game on GOG and want to help add it, please get in touch (open an issue on this repository).

**This repository does not contain Stardew Valley.** The builder takes your own legal copy (Steam) and turns it into
a PS Vita build on your PC: it decompiles your copy, applies the port's changes and builds it. No game code, art or
audio is distributed here. Not affiliated with ConcernedApe, Sickhead Games or Sony. Please buy the game.

## What you get

- Stardew Valley 1.6.15 on the PS Vita: 960x544, controller layout of the official Vita release, 350 MB memory budget
- Mods menu: tractor, info overlays, chest helper, three extra ring slots, passable crops
- 12 languages, trophies (with NoTrpDrm)
- Co-op over LAN/IP (new, please report bugs): join a PC host by its IP from the title screen's co-op menu, or host
  on the Vita (its IP shows in the chat; type `/ip` to see it again). Steam invites don't work, only IP
- Your PC saves work on the Vita and back (see [Moving saves](#moving-saves-between-pc-and-vita))

## What you need

**On the Vita:** HENkaku/Ensō, VitaShell, `kubridge.skprx` (v0.3.1_hotfix) under `*KERNEL` in
`ur0:tai/config.txt`, and `libshacccg.suprx` in `ur0:data/`. Optional: NoTrpDrm for real trophies.

**On the PC:** Stardew Valley on Steam, about 5 GB of free space, an internet connection for the first build, and:

| System | What to install | Download from Releases |
| --- | --- | --- |
| **Windows 10 / 11** | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the **SDK**, not only the Runtime) | `StardewVitaBuilder-windows.zip` |
| Linux (experimental) | .NET 8 SDK | `StardewVitaBuilder-linux-macos.zip` |
| macOS (experimental) | Docker Desktop (or OrbStack/Colima) and SteamCMD | `StardewVitaBuilder-linux-macos.zip` |

Nothing else: the builder downloads the official VitaSDK by itself the first time (~45-70 MB).

## Installing on Windows

▶ Video: *coming soon* <!-- YouTube link for the Windows install video goes here -->

1. **Install the .NET 8 SDK** from https://dotnet.microsoft.com/download/dotnet/8.0 (Windows, x64 installer of the
   SDK). A newer SDK works too.
2. **Switch Stardew Valley to the compatibility branch:** Steam → Library → right-click Stardew Valley →
   Properties → Betas → choose **compatibility**, and let it update. The builder checks the game files and stops on
   any other version.
3. **Download `StardewVitaBuilder-windows.zip`** from this repository's Releases and unzip it somewhere short and
   plain, like `C:\Games`. Not on a OneDrive Desktop or Documents folder.
4. **Double-click `Build.bat`** in the `StardewVitaBuilder` folder. If Windows shows "Windows protected your PC",
   choose *More info* → *Run anyway*. It finds your Steam game by itself and takes about 4 minutes the first time
   (2-3 minutes after that). If something goes wrong, the window stays open with an `ERROR:` line saying what.
5. When it's done, the folder to copy opens by itself (`work\out\ux0`). It is laid out like the Vita's memory card:
   `data\StardewValley` (~600 MB, with your PC saves in `Saves`) and `stardew.vpk`.
6. **Copy it to the Vita:** in VitaShell press SELECT for USB (or use FTP) and copy everything inside `ux0` to the
   Vita's `ux0:`. Let Windows merge the `data` folder with the one already there. If Windows asks to replace a
   farm's save files, that farm is on both: say yes only if the PC has your latest progress for it.
7. **Install:** in VitaShell open `ux0:stardew.vpk`, install it, and then you can delete the .vpk. Start the game
   from its bubble.

Building again later (for example after an update of this repository) is the same: double-click `Build.bat` and copy
`ux0` again. Saves that are only on the Vita are kept.

## Installing on Linux (experimental)

Tested only in a Linux test setup (Debian 11), never on a real Linux PC: please report how it goes.

1. Install the .NET 8 SDK (your distribution's package or https://dotnet.microsoft.com/download/dotnet/8.0). Needs
   an x86_64 Linux with glibc 2.29 or newer (Ubuntu 20.04+, Debian 11+, Fedora, Arch, SteamOS...).
2. In Steam: Stardew Valley → Properties → Betas → **compatibility**, and also Properties → Compatibility → *Force
   the use of a specific Steam Play compatibility tool* (any Proton), so Steam downloads the game's **Windows** files,
   which the builder needs. You don't have to play it that way.
3. Unzip `StardewVitaBuilder-linux-macos.zip` and run `bash build.sh` in the `StardewVitaBuilder` folder. It finds
   Steam (also the Flatpak and Snap versions) by itself; otherwise add `--game "<folder with Stardew Valley.exe>"`.
4. Copy everything inside `work/out/ux0` to the Vita's `ux0:` and install `ux0:stardew.vpk`, as on Windows.

## Installing on macOS (experimental)

Never tried on a Mac: it runs the Linux build in Docker. Please report how it goes.

1. Install Docker Desktop (or OrbStack/Colima). On Apple Silicon keep Docker Desktop's *Use Rosetta for x86_64/amd64
   emulation* on.
2. Steam for Mac downloads the Mac files, but the builder needs the **Windows** ones. Get them with
   [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) and your own Steam login:

   ```
   steamcmd +@sSteamCmdForcePlatformType windows +force_install_dir ~/StardewWindows +login <your Steam name> +app_update 413150 -beta compatibility validate +quit
   ```

3. Unzip `StardewVitaBuilder-linux-macos.zip`, open Terminal in the `StardewVitaBuilder` folder and run:

   ```
   bash tools/docker-build.sh --game ~/StardewWindows
   ```

4. Copy everything inside `work/out/ux0` to the Vita's `ux0:` and install `ux0:stardew.vpk`, as on Windows.

## Moving saves between PC and Vita

▶ Video: *coming soon* <!-- YouTube link for the save transfer video goes here -->

Saves are plain folders, one per farm (for example `Farmname_123456789`, with two files inside). Saves from Stardew
Valley 1.6 on PC load on the Vita, and Vita saves load on PC. **Keep a copy of a save before moving it.**

| Where | Saves folder |
| --- | --- |
| Windows | `%APPDATA%\StardewValley\Saves` (paste this into the Explorer address bar) |
| Linux, macOS | `~/.config/StardewValley/Saves` |
| PS Vita | `ux0:data/StardewValley/Saves` |

**PC → Vita, automatically:** the builder copies every farm from the PC's Saves folder into its output
(`work/out/ux0/data/StardewValley/Saves`), so they reach the Vita with the game. Add `--no-saves` to leave them out
(from a command prompt: `Build.bat --no-saves`), or `--saves "<folder>"` to take them from another folder.

**PC → Vita, by hand** (for example a farm you played further on the PC later): close the game on the PC, connect
the Vita with VitaShell (SELECT for USB, or FTP), and copy the farm's folder (the whole folder, not only the files
inside) into `ux0:data/StardewValley/Saves` (create `Saves` if it isn't there yet). Start the game on the Vita and
load the farm.

**Vita → PC:** the same the other way: copy the farm's folder from `ux0:data/StardewValley/Saves` into the PC's
Saves folder. With Steam Cloud on, start the game once afterwards and pick the local save if Steam asks.

## Questions and problems

Open an issue on this repository with the `ERROR:` line from the build window (or, for problems in the game,
`ux0:data/StardewValley/vita-log.txt`).

## For maintainers

- Release files: `StardewVitaBuilder-windows.zip` is this folder (without `.git/`, `work/`, `builder/bin`,
  `builder/obj`) as `StardewVitaBuilder/` with `vita-toolchain-windows.zip` inside, next to Build.bat;
  `StardewVitaBuilder-linux-macos.zip` the same with `vita-toolchain-linux.zip`.
- The builder by hand: `dotnet run --project builder -c Release -- [--game <folder>] [--toolchain <zip>] [--work
  <folder>] [--native-dir <short ASCII folder>] [--saves <folder> | --no-saves] [--no-vpk]`. On Windows, if the folder's path is long or has letters
  like ç, ğ, ü, the Vita tools work in `C:\ProgramData\StardewVitaBuilder` (~1 GB, safe to delete afterwards).
  `--no-vpk` stops after staging; `vpk/build-vpk.sh` then makes the same VPK in WSL with the
  `vita-toolchain.tar.xz` archive.
- `tools/make-patches.py` regenerates `patches/` and `overlay/` from the development tree (see its header).
- `vpk/pack-toolchain-zip.sh windows|linux <zip>` packs the toolchain zips (needs the AOT compiler for that system:
  `vpk/build-mono-crosswin.sh`, built in WSL with mingw-w64, or `vpk/build-mono-crosslinux.sh`, built in a Debian 11
  chroot for an old glibc); `vpk/pack-toolchain.sh` packs the WSL archive.
- `assets/xmlserializers.bin`: the game's pregenerated XML serializers for builds outside Windows (SerializerGen
  needs .NET Framework), encrypted with a key from the game's own `Stardew Valley.exe`. Whenever `patches/` or
  `overlay/` change, a Windows build says so; run it once with `--save-serializers` to write a new one.
- `tools/controls-art/` draws `assets/vita-controls.png` and `assets/manual-002.png`.
- Licenses: `THIRD_PARTY.md`.
