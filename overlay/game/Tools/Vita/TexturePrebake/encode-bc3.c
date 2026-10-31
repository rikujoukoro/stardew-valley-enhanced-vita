/* Offline host utility only. stb_dxt.h retains its upstream dual MIT/public-domain license. */
#include <string.h>
#define STB_DXT_IMPLEMENTATION
#include "stb_dxt.h"
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <limits.h>

int main(int argc, char **argv)
{
    if (argc != 5) { fprintf(stderr, "Usage: encode-bc3 width height input.rgba output.bc3\n"); return 2; }
    char *end;
    long w = strtol(argv[1], &end, 10);
    if (*end || w < 4 || w > 16384 || w % 4) return 2;
    long h = strtol(argv[2], &end, 10);
    if (*end || h < 4 || h > 16384 || h % 4) return 2;
    FILE *in = fopen(argv[3], "rb");
    if (!in) { perror("input"); return 1; }
    size_t row_bytes = (size_t)w * 4, stripe_bytes = row_bytes * 4;
    unsigned char *stripe = malloc(stripe_bytes);
    if (!stripe) { fclose(in); return 1; }
    FILE *out = fopen(argv[4], "wb");
    if (!out) { perror("output"); free(stripe); fclose(in); return 1; }
    int result = 0;
    for (long y = 0; y < h && !result; y += 4) {
        if (fread(stripe, 1, stripe_bytes, in) != stripe_bytes) { result = 1; break; }
        for (long x = 0; x < w; x += 4) {
            unsigned char block[64], coded[16];
            for (int r = 0; r < 4; ++r) memcpy(block + r * 16, stripe + r * row_bytes + x * 4, 16);
            /* Source already is premultiplied RGBA: do not multiply or unpremultiply. */
            stb_compress_dxt_block(coded, block, 1, STB_DXT_HIGHQUAL);
            if (fwrite(coded, 1, 16, out) != 16) { result = 1; break; }
        }
    }
    if (fgetc(in) != EOF) result = 1;
    if (ferror(in)) result = 1;
    free(stripe); fclose(in); if (fclose(out)) result = 1;
    if (result) fprintf(stderr, "BC3 conversion failed (input length or I/O).\n");
    return result;
}
