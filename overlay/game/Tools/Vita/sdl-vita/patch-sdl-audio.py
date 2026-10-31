#!/usr/bin/env python3
"""PS Vita port edits to SDL3's Vita audio driver (src/audio/vita/SDL_vitaaudio.c); run by build-sdl3.sh.

Usage: patch-sdl-audio.py <SDL source dir>

The speaker froze for whole seconds while a capture of FAudio's mix had no gaps and the mixer callbacks
were never late, so the loss is between SDL's device thread and the audio port. sceAudioOutOutput blocks
until the port can take the buffer; one that returns at once found the port's queue empty: the hardware
had already run dry (an audible gap). These edits count such outputs and time every output; the launcher
logs them (SDL_vita_audio_stats). The device is also always opened at 48 kHz on the main port (see
RATE_ANCHOR). An edit already present is left alone.
"""
import sys
from pathlib import Path

ANCHOR = """static bool VITAAUD_PlayDevice(SDL_AudioDevice *device, const Uint8 *buffer, int buffer_size)
{
    // sceAudioOutOutput returns amount of samples queued or < 0 on error
    return (sceAudioOutOutput(device->hidden->port, buffer) >= 0);
}"""

REPLACEMENT = """/* Vita port: outputs, outputs that found the port's queue empty (returned in under 1 ms: the hardware
 * had run dry), longest output wait (us), longest time between outputs (us). Read and reset by the
 * launcher (Tools/Vita/stardew/main.c). */
Uint32 SDL_vita_audio_stats[4];
static Uint64 vita_last_output_end;

static bool VITAAUD_PlayDevice(SDL_AudioDevice *device, const Uint8 *buffer, int buffer_size)
{
    // sceAudioOutOutput returns amount of samples queued or < 0 on error
    Uint64 start = SDL_GetTicksNS();
    int result = sceAudioOutOutput(device->hidden->port, buffer);
    Uint64 end = SDL_GetTicksNS();
    Uint32 waited = (Uint32) ((end - start) / 1000);
    SDL_vita_audio_stats[0] += 1;
    if (waited < 1000 && vita_last_output_end != 0) {
        SDL_vita_audio_stats[1] += 1;
    }
    if (waited > SDL_vita_audio_stats[2]) {
        SDL_vita_audio_stats[2] = waited;
    }
    if (vita_last_output_end != 0 && (Uint32) ((start - vita_last_output_end) / 1000) > SDL_vita_audio_stats[3]) {
        SDL_vita_audio_stats[3] = (Uint32) ((start - vita_last_output_end) / 1000);
    }
    vita_last_output_end = end;
    return (result >= 0);
}"""


# The output rate. The driver opened the port at the rate asked for, and anything under 48 kHz went to
# the BGM port (the main port takes 48 kHz only): FAudio asks for SDL's default 44.1 kHz, so the game
# played through the BGM port, which the system resamples to the hardware's 48 kHz itself, and the sound
# was heard as duller and rougher than the original. Mixing at 48 kHz in FAudio (SDL_AUDIO_FREQUENCY)
# resampled every voice and cost frame rate. Instead the device is always 48 kHz on the main port and
# FAudio keeps mixing at 44.1 kHz: SDL's audio stream resamples the one stereo mix with its windowed-sinc
# filter, on the audio thread (its own core). A file named audio-44k in the game folder brings back the
# old 44.1 kHz BGM port, to compare.
RATE_ANCHOR = """    // The sample count must be a multiple of 64.
    device->sample_frames = SCE_AUDIO_SAMPLE_ALIGN(device->sample_frames);
"""

RATE_REPLACEMENT = """    /* Vita port: always the main port at 48 kHz, which is what the hardware runs at; SDL resamples the
     * game's 44.1 kHz mix to it (see sdl-vita/patch-sdl-audio.py). ux0:data/StardewValley/audio-44k
     * keeps the rate asked for (44.1 kHz on the BGM port), to compare. */
    {
        SceIoStat stat;
        if (sceIoGetstat("ux0:data/StardewValley/audio-44k", &stat) < 0) {
            device->spec.freq = 48000;
        }
    }

    // The sample count must be a multiple of 64.
    device->sample_frames = SCE_AUDIO_SAMPLE_ALIGN(device->sample_frames);
"""

INCLUDE_ANCHOR = "#include <psp2/kernel/threadmgr.h>\n"
INCLUDE_REPLACEMENT = """#include <psp2/kernel/threadmgr.h>
#include <psp2/io/stat.h>

/* Vita port: the rate the output port was opened at (48000: main port, less: BGM port), for the launcher's log. */
int SDL_vita_audio_rate;
"""

PORT_ANCHOR = """    device->hidden->port = sceAudioOutOpenPort(port, device->sample_frames, device->spec.freq, format);
"""
PORT_REPLACEMENT = """    device->hidden->port = sceAudioOutOpenPort(port, device->sample_frames, device->spec.freq, format);
    SDL_vita_audio_rate = device->spec.freq;
"""


def apply(path, text, anchor, replacement, why):
    if replacement in text:
        print(f"SDL Vita audio: {why}: already present")
        return text
    if text.count(anchor) != 1:
        sys.exit(f"{path}: anchor for {why} not found exactly once")
    print(f"patched src/audio/vita/SDL_vitaaudio.c: {why}")
    return text.replace(anchor, replacement)


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    path = Path(sys.argv[1]) / "src/audio/vita/SDL_vitaaudio.c"
    text = path.read_text()
    text = apply(path, text, ANCHOR, REPLACEMENT, "output timing")
    text = apply(path, text, INCLUDE_ANCHOR, INCLUDE_REPLACEMENT, "sceIoGetstat")
    text = apply(path, text, RATE_ANCHOR, RATE_REPLACEMENT, "48 kHz main port")
    text = apply(path, text, PORT_ANCHOR, PORT_REPLACEMENT, "remember the port rate")
    path.write_text(text)


if __name__ == "__main__":
    main()
