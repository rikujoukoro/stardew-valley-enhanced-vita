# Trophies

Stardew Valley's achievements as Vita trophies, in two layers:

1. **Toasts, for everyone** (`StardewValley.SDKs/VitaTrophies.cs`, `VitaSDKHelper.cs`). `Program.sdk` reports every
   achievement here. A new one shows a toast in the top-left corner: the grade's cup, "You have earned a trophy." and the name.
   Toasts are queued and shown one at a time. Unlocked trophies count for the whole console, not one save, and are kept in
   `ux0:data/StardewValley/Trophies.txt`. While a save loads (before its first day starts) the game reports the
   achievements it already has. Those are recorded without a toast. The Collections → Achievements tab shows each
   trophy's grade as a small cup and in the hover text. It also lists the ten Steam-only achievements on a row of
   their own.
2. **Real trophies, with the NoTrpDrm plugin** (`../main.c`, `vita_port_trophy_*`). The launcher's own trophy pack
   `../sce_sys/trophy/STDV00001_00/TROPHY.TRP` is built by `make-trophies.py`. It is not the official Stardew pack:
   redistributing that pack is a copyright problem, and unlocking official trophies from homebrew puts the PSN account
   at risk.

The cups are drawn by `VitaTrophies.CreateIcons` (a 64x16 texture, 4 KB) and `make-trophies.py` (the pack's PNGs).
Nothing of the game's art is in the VPK.

## Grades

The base set follows the official PS4/PS Vita list. It has 41 trophies: 26 bronze, 10 silver, 4 gold and 1 platinum
([VGTimes](https://vgtimes.ru/games/stardew-valley/achievements-and-trophies/ps-vita-ps4/),
[DualShockers](https://www.dualshockers.com/stardew-valley-playstation-4-trophies/)). Every grade in the base set
matches that list. The PS4 list later grew to 50 trophies: 31 bronze, 14 silver, 4 gold and 1 platinum
([PlayStationTrophies](https://www.playstationtrophies.org/game/stardew-valley/trophies/)). The extra 9 come from the
"1.6 Update" pack ([PSNProfiles guide](https://psnprofiles.com/guide/22094-stardew-valley-16-update-dlc-trophy-guide)).
That adds 5 bronze and 4 silver. Of those nine:

| Achievement | Grade | Certainty |
| --- | --- | --- |
| Well-Read, Infinite Power | silver | PSNProfiles |
| Blue Ribbon, Good Neighbors, A Distant Shore | bronze | PSNProfiles |
| Danger In The Deep | silver | **guess** |
| Two Thumbs Up, An Unforgettable Soup | bronze | **guess** |
| Perfection | platinum here (user's choice), gold in TROPHY.TRP | official grade not found |

The official 41 have no Perfection. The 9 DLC trophies need 4 silver: 2 are confirmed and 2 must come from Danger In
The Deep, Two Thumbs Up, Soup and Perfection. Perfection has its own row here, so only Danger In The Deep is silver.
That is a guess: check it against a full PSNProfiles or Exophase list when one can be read.

In TROPHY.TRP, Perfection is gold. A Vita platinum cannot be unlocked by the game. The system awards it (id 000) when
every trophy that links to it (`pid="000"`) is unlocked.

## The pack and NoTrpDrm

- **NoTrpDrm** ([source](https://github.com/Rinnegatamante/NoTrpDrm), [GameBrew](https://www.gamebrew.org/wiki/NoTrpDrm_Vita))
  is a `*main` plugin, so it loads into SceShell. It hooks two functions at firmware-specific offsets. One makes the
  NP communication signature check pass, and the other reports "not a dev pack" for the TRP signature check. It only
  supports firmware **3.60 (retail, PTEL, PDEL) and 3.65, 3.67 and 3.68 retail**. On other firmware the plugin
  refuses to start.
- **The TRP** format was checked byte for byte against Rinnegatamante's `not_a_hero-vita/livearea/TROPHY.TRP`. That
  is a homebrew pack with its own comm id, `NAHR00001_00`. The SFM files are plain XML. Each starts with a placeholder
  `<!--Sce-Np-Trophy-Signature: xxx…-->` line of 320 `x`. After the SFMs come `ICON0.PNG` (320x176) and one
  `TROPnnn.PNG` per trophy (240x240). The header is 0x40 bytes, big-endian: magic `DCA24D00`, version 2, file size,
  entry count and entry size 0x40. It ends with the SHA-1 of the whole file, computed with that field zeroed. The
  script reproduces the example's SHA-1.
- **Layout:** `sce_sys/trophy/<comm id>/TROPHY.TRP`. The comm id is the title id plus `_00`. `build-stardew.sh`
  copies `stardew/sce_sys` into the VPK as it is.
- **Calls** (`main.c`, after `not_a_hero-vita/loader/trophies.c`; the SDK has stubs but no header):
  `sceSysmoduleLoadModule(SCE_SYSMODULE_NP_TROPHY)`, `sceNpTrophyInit(NULL)`, then
  `sceNpTrophyCreateContext(&ctx, {"STDV00001", 0, 0}, signature, 0)`. The signature is zeros after the header bytes
  `b9 dd e1 3b 01 00`. Next comes the setup dialog (`sceNpTrophySetupDialogInit`, `…GetStatus` and `…Term`) and
  `sceNpTrophyGetTrophyUnlockState`. Each unlock then calls `sceNpTrophyCreateHandle`, `sceNpTrophyUnlockTrophy` and
  `sceNpTrophyDestroyHandle`. Without the plugin, `CreateContext` fails, the thread logs `trophies: off` and ends.

**The setup dialog needs the renderer.** vitaGL updates a common dialog only on `vglSwapBuffers(GL_TRUE)`.
`fna3d-vita/vita_gl_glue.c` passes `GL_TRUE` only while SDL text input is active. Until the glue also asks
`vita_port_common_dialog_active()` (see the side-session notes), the trophy thread waits before the dialog. Real
trophies then stay off and the toasts still work. The thread waits because a dialog that is never updated never
finishes, and it would block the IME.

## Device checklist

- Without NoTrpDrm: `vita-log.txt` has `trophies: off (0x…)`. A new achievement shows its toast once and makes no
  sound twice. Loading a save that already has achievements shows no toasts. `Trophies.txt` lists them afterwards.
- Toast queue: unlock two at once (for example `debug achieve 0`, then `debug achieve Achievement_TheBottom` with
  `SV_DEVCHEATS=1`). They appear one after the other. A toast earned at night (Singular Talent from the level-up
  screen) shows the next morning.
- Collections → Achievements: cups on the tiles, the grade in the hover text, and the row of 10 Steam-only trophies.
  They fit on one page.
- With NoTrpDrm (3.60 or 3.65–3.68) and the glue change: the first start shows the trophy setup dialog on the title
  screen. Then an unlock logs `trophies: unlock N: 0x00000000` and the system trophy popup appears. The trophy app
  lists "Stardew Valley Enhanced" with 50 trophies. Check whether the system accepts a platinum in a homebrew pack
  (`policy="small"`). If not, rebuild with `python make-trophies.py --no-platinum`.
- The IME (naming the farm) still opens while the setup dialog runs or after it.
- Memory: the `vita-log.txt` memory lines before and after `trophies: on`. The NpTrophy module and the 64 KB thread
  stack come out of MAIN.
