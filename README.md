# Stardew Valley Enhanced

An unofficial PS Vita port of Stardew Valley 1.6, built on your own PC from your own copy of the game.

> **Steam and GOG.** The GOG version works too (its 1.6.15 compatibility build). The builder turns it into the
> same Vita build as Steam's, byte for byte, so it hasn't been tested on a Vita separately. Thanks to the player
> who shared their GOG files for testing.

**This repository does not contain Stardew Valley.** The builder takes your own legal copy (Steam or GOG) and turns it into
a PS Vita build on your PC: it decompiles your copy, applies the port's changes and builds it. No game code, art or
audio is distributed here. Not affiliated with ConcernedApe, Sickhead Games or Sony. Please buy the game.

## What you get

- Stardew Valley 1.6.15 on the PS Vita: 960x544, controller layout of the official Vita release, 350 MB memory budget
- Mods menu: tractor, info overlays, chest helper, three extra ring slots, passable crops
- 12 languages, trophies (with NoTrpDrm)
- Co-op over LAN/IP (new, please report bugs): join a PC host by its IP from the title screen's co-op menu, or host
  on the Vita (its IP shows in the chat; type `/ip` to see it again). Steam invites don't work, only IP
- Your PC saves work on the Vita and back, and saves from the official Vita release can be brought over (see
  [Moving saves](#moving-saves-between-pc-and-vita))

## What you need

**On the Vita:** HENkaku/Ensō, VitaShell, `kubridge.skprx` (v0.3.1_hotfix) under `*KERNEL` in
`ur0:tai/config.txt`, and `libshacccg.suprx` in `ur0:data/` (or `ur0:data/external/`). Optional: NoTrpDrm for real
trophies. You don't have to find these yourself: the builder puts kubridge, NoTrpDrm and ShaRKF00D (which makes
`libshacccg.suprx` on your own Vita; it is Sony's file and can't be shared) next to the game, with a
`VITA-PLUGINS.txt` that explains each step (see [First time on this Vita](#first-time-on-this-vita)).

**On the PC:** Stardew Valley on Steam or GOG, about 5 GB of free space, an internet connection for the first build, and:

| System | What to install | Download from Releases |
| --- | --- | --- |
| **Windows 10 / 11** | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the **SDK**, not only the Runtime) | `StardewVitaBuilder-windows.zip` |
| Linux (experimental) | .NET 8 SDK | `StardewVitaBuilder-linux-macos.zip` |
| macOS (experimental) | Docker Desktop and DepotDownloader (both with Homebrew) | `StardewVitaBuilder-linux-macos.zip` |

Nothing else: the builder downloads the official VitaSDK by itself the first time (~45-70 MB), and the Vita plugins
from their authors' releases.

## Installing on Windows

▶ Video: *coming soon* <!-- YouTube link for the Windows install video goes here -->

1. **Install the .NET 8 SDK** from https://dotnet.microsoft.com/download/dotnet/8.0 (Windows, x64 installer of the
   SDK). A newer SDK works too.
2. **Switch Stardew Valley to the compatibility version:**
   - Steam: Library → right-click Stardew Valley → Properties → Betas → choose **compatibility**, and let it update.
   - GOG: install the game's **compatibility** version (1.6.15), see [Installing from GOG](#installing-from-gog).

   The builder checks the game files and stops on any other version.
3. **Download `StardewVitaBuilder-windows.zip`** from this repository's Releases and unzip it somewhere short and
   plain, like `C:\Games`. Not on a OneDrive Desktop or Documents folder.
4. **Double-click `Build.bat`** in the `StardewVitaBuilder` folder. If Windows shows "Windows protected your PC",
   choose *More info* → *Run anyway*. It finds your Steam or GOG Galaxy game by itself and takes about 4 minutes the
   first time (2-3 minutes after that). If something goes wrong, the window stays open with an `ERROR:` line saying
   what. If it doesn't find the game (for example, a GOG game installed without Galaxy), copy your game folder (the
   one with `Stardew Valley.exe`) into the `StardewVitaBuilder` folder, name it `Stardew Valley`, and run
   `Build.bat` again.
5. When it's done, `work\out` opens by itself. `ux0` in it is laid out like the Vita's memory card:
   `data\StardewValley` (~600 MB, with your PC saves in `Saves`), `stardew.vpk`, and the plugin files
   (`tai\NoTrpDrm.suprx`, `ShaRKF00D.vpk`). Next to it are `ur0\tai\kubridge.skprx` and `VITA-PLUGINS.txt`.
6. **Copy it to the Vita:** in VitaShell press SELECT for USB (or use FTP) and copy everything inside `ux0` to the
   Vita's `ux0:`. Let Windows merge the `data` folder with the one already there. If Windows asks to replace a
   farm's save files, that farm is on both: say yes only if the PC has your latest progress for it.
7. **Install:** in VitaShell open `ux0:stardew.vpk`, install it, and then you can delete the .vpk.
8. **First time on this Vita?** Set up kubridge and libshacccg (and NoTrpDrm if you like) as `VITA-PLUGINS.txt`
   says, then restart the Vita. Start the game from its bubble.

Building again later (for example after an update of this repository) is the same: double-click `Build.bat` and copy
`ux0` again. Saves that are only on the Vita are kept.

## Installing from GOG

The GOG version needs its **1.6.15 compatibility** build (GOG's .NET Framework/XNA version of the game, the same one
Steam calls the *compatibility* branch). The builder recognizes it by its file hashes and stops on any other GOG
build. Everything after step 2 is the same as on Steam: the GOG files give the same Vita build, byte for byte.

1. **Install the .NET 8 SDK**, as in step 1 above.
2. **Get the compatibility build:** in GOG Galaxy open Stardew Valley, click the settings icon next to *Play* →
   *Manage installation* → *Configure*, choose the **compatibility** version (1.6.15) instead of the default one, and
   let it install. (The menu names can differ between Galaxy versions; if your Galaxy doesn't offer it, GOG's
   website may have an offline installer for that version in your game library.)
3. **Download `StardewVitaBuilder-windows.zip`** and unzip it, as in step 3 above.
4. **Double-click `Build.bat`.** It finds a game installed with GOG Galaxy by itself (it reads the folder Galaxy
   registered). If it doesn't (an offline installer, a moved folder, a copy from another PC), copy the game's
   folder, the one with `Stardew Valley.exe` and `Content`, into the `StardewVitaBuilder` folder, name it
   `Stardew Valley`, and run `Build.bat` again. You can also point at it yourself:
   `Build.bat --game "D:\GOG Games\Stardew Valley"`.
5. The window should say `Verified: GOG 1.6.15 compatibility`. Then continue with steps 5-8 above.

If it says your files don't match: the folder has GOG's default (.NET 6) version, or a mix of both. A
compatibility folder has a `Stardew Valley.exe` of about 6 MB. Files left over from the other version
(`Stardew Valley.dll`, `*.runtimeconfig.json`, many `System.*.dll`) don't stop the build: the builder only reads the
files it checks and `Content`.

GOG saves are the same as Steam's (same folder, same format), so [Moving saves](#moving-saves-between-pc-and-vita)
works the same way. Co-op on the Vita is over IP only, so GOG Galaxy invites don't work either.

## First time on this Vita

The game needs two things a fresh HENkaku Vita may not have; NoTrpDrm is optional. The builder downloads them from
their authors' own releases (checked by SHA-256) and puts them in `work\out`, laid out like the Vita's memory:
`ux0\...` goes to `ux0:`, `ur0\...` to `ur0:`. `VITA-PLUGINS.txt` there has the same steps.

First, which `config.txt`: if `ux0:tai/config.txt` exists, the Vita reads that one and ignores
`ur0:tai/config.txt`. Edit the one your Vita uses (in VitaShell: select it, press Triangle, *Open in text editor*).

1. **kubridge** (needed, v0.3.1_hotfix; older ones crash the game at the start): copy
   `ur0\tai\kubridge.skprx` to `ur0:tai/`, replacing an older one. VitaShell's USB mode only shows `ux0:`, so copy
   it to `ux0:` first and move it to `ur0:tai/` in VitaShell, or use FTP. In `config.txt`, under `*KERNEL`, keep one
   line `ur0:tai/kubridge.skprx` and no other kubridge line.
2. **libshacccg.suprx** (needed for the graphics): you already have it if `ur0:data/libshacccg.suprx` or
   `ur0:data/external/libshacccg.suprx` exists. Otherwise install *PSM Runtime* with its 2.01 update from the PS
   Store, install `ux0:ShaRKF00D.vpk` with VitaShell and start it: it closes by itself when done and leaves
   `ur0:data/libshacccg.suprx`.
3. **NoTrpDrm** (optional, real trophies): `ux0\tai\NoTrpDrm.suprx` arrives at `ux0:tai/` with the rest of `ux0`.
   In `config.txt`, under `*main` (add that line if it isn't there), add `ux0:tai/NoTrpDrm.suprx`.

Restart the Vita after changing `config.txt`.

## Installing on Linux (experimental)

Tested only in a Linux test setup (Debian 11), never on a real Linux PC: please report how it goes.

1. Install the .NET 8 SDK (your distribution's package or https://dotnet.microsoft.com/download/dotnet/8.0). Needs
   an x86_64 Linux with glibc 2.29 or newer (Ubuntu 20.04+, Debian 11+, Fedora, Arch, SteamOS...).
2. In Steam: Stardew Valley → Properties → Betas → **compatibility**, and also Properties → Compatibility → *Force
   the use of a specific Steam Play compatibility tool* (any Proton), so Steam downloads the game's **Windows** files,
   which the builder needs. You don't have to play it that way. GOG: pass the game's folder with `--game`.
3. Unzip `StardewVitaBuilder-linux-macos.zip` and run `bash build.sh` in the `StardewVitaBuilder` folder. It finds
   Steam (also the Flatpak and Snap versions) by itself; otherwise add `--game "<folder with Stardew Valley.exe>"`.
4. Copy everything inside `work/out/ux0` to the Vita's `ux0:` and install `ux0:stardew.vpk`, as on Windows.

## Installing on macOS (experimental)

It runs the Linux build in Docker. A player built and played it this way (thanks for the steps!); please report
how it goes on your Mac.

1. **Install [Homebrew](https://brew.sh)** if you don't have it, then DepotDownloader and Docker Desktop:

   ```
   brew install depotdownloader
   brew install docker-desktop
   ```

   Open Docker Desktop once and let it start. On Apple Silicon keep its *Use Rosetta for x86_64/amd64 emulation*
   setting on.
2. **Download the game's Windows files.** Steam for Mac downloads the Mac files, but the builder needs the
   **Windows** ones of the compatibility branch. DepotDownloader gets them with your own Steam account:

   ```
   depotdownloader -app 413150 -depot 413151 -manifest 4848991934266309406 -beta compatibility -username <your Steam name> -dir ~/StardewWindows
   ```

   It asks for your password (and a Steam Guard code if you use one). You can also pass `-password <password>`,
   but then it stays in your shell history.
3. **Build:** unzip `StardewVitaBuilder-linux-macos.zip`, open Terminal in the `StardewVitaBuilder` folder and run:

   ```
   bash tools/docker-build.sh --game ~/StardewWindows
   ```

4. Copy everything inside `work/out/ux0` to the Vita's `ux0:` and install `ux0:stardew.vpk`, as on Windows. The
   plugins and `VITA-PLUGINS.txt` are in `work/out` too (see [First time on this Vita](#first-time-on-this-vita)).

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

**From the official PS Vita release** (found by a player, [issue #2](https://github.com/rikujoukoro/stardew-valley-enhanced-vita/issues/2)):
the official release keeps its saves encrypted under `ux0:user/00/savedata/<its title ID>`, so copied as they are
they don't show up in the game. VitaShell can open them decrypted, and inside they are ordinary farm folders:

1. In VitaShell, go to `ux0:user/00/savedata/`. Find the official Stardew Valley's folder: the player's European copy
   was `PCSB01226`; other regions have another ID (each farm inside is a folder like `Farmname_123456789`).
2. Highlight that folder, press △ and choose **Open decrypted**.
3. Copy the farm's folder from there (the whole folder) into `ux0:data/StardewValley/Saves` (create `Saves` if it
   isn't there yet). The official release's own save stays where it was.
4. Start the game and load the farm. The first load of such a save can take a long while; let it finish.

## Questions and problems

Open an issue on this repository with the `ERROR:` line from the build window (or, for problems in the game,
`ux0:data/StardewValley/vita-log.txt`).

**The game closes right at the start** and `vita-log.txt` ends at a `MONO_GC_PARAMS=` line: your kubridge is too old
(older ones lack the memory functions the game uses). Put kubridge
[v0.3.1_hotfix](https://github.com/bythos14/kubridge/releases) in `ur0:tai/`, keep a single kubridge line under
`*KERNEL`, and restart the Vita. If there is a `ux0:tai/config.txt`, the Vita reads that one instead of
`ur0:tai/config.txt`: check it too.

## Contributing and license

Contributions are welcome as Pull Requests to this repository. The port's own code (the builder, the patches and
files in `overlay/`) is © rikujoukoro, all rights reserved: please don't publish modified copies or your own
releases of it without permission. Ask first, and improvements can go into a release here with credit. Third-party
parts keep their own licenses (`THIRD_PARTY.md`).

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
- `assets/xmlserializers.bin` (Steam) and `assets/xmlserializers-gog.bin` (GOG): the game's pregenerated XML
  serializers for builds outside Windows (SerializerGen needs .NET Framework), each encrypted with a key from that
  build's own `Stardew Valley.exe`. Whenever `patches/` or `overlay/` change, a Windows build says so; run it once
  per store with `--save-serializers` to write new ones.
- Known game builds: `tools/known-games.json`. The GOG build is the Steam one without its Steam code, so after
  decompiling, `patches/gog/` turns its source into Steam's before the port's own patches. The two builds then
  produce identical output.
- `tools/controls-art/` draws `assets/vita-controls.png` and `assets/manual-002.png`.
- Licenses: `THIRD_PARTY.md`.
