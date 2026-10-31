/* No AOT images linked into the eboot: every method is JIT-compiled. build-stardew.sh uses the list
 * aot-stardew.sh generates instead when AOT_DIR is set. */
#include <stddef.h>

void **const vita_aot_modules[] = { NULL };
