/* configure has no Mono.Native platform type for the Vita. Managed code only uses it to choose
 * between POSIX flavours; the Vita's newlib behaves closest to the Linux one. */
#include "mono-native-platform.h"

MonoNativePlatformType mono_native_platform_type = MONO_NATIVE_PLATFORM_TYPE_LINUX;
