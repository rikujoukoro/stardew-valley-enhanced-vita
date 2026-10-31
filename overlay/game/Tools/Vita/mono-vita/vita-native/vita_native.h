/* Static "native libraries" for P/Invoke on the Vita.
 *
 * The Vita has no dynamic loader, so every library a [DllImport] names (System.Native, and later
 * SDL3, FNA3D, FAudio) is linked into the executable and described by a name -> address table.
 * vita_native_install() registers a Mono dl fallback that serves DllImport lookups from these tables.
 */
#ifndef VITA_NATIVE_H
#define VITA_NATIVE_H

typedef struct VitaNativeSymbol {
	const char *name;
	void *address;
} VitaNativeSymbol;

/* Generated from the System.Native objects at build time; ends with { 0, 0 }. */
extern const VitaNativeSymbol vita_system_native_symbols[];

/* Makes `table` answer for the library called `name` ("System.Native", "SDL3", ...): any
 * lib prefix and .so/.dll/.dylib suffix Mono tries is ignored. Call before the first P/Invoke. */
int vita_native_register_library(const char *name, const VitaNativeSymbol *table);

/* Registers the Mono dl fallback and System.Native. Call once, before mono_jit_init*. */
void vita_native_install(void);

#endif /* VITA_NATIVE_H */
