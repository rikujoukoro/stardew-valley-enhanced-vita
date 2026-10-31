/* SDL3 functions whose results managed code would misread on the Vita, served to FNA's [DllImport]
 * in place of the originals (see sdl3.overrides and gen-native-table.sh).
 *
 * SDL returns device paths ("app0:/", "ux0:/data/..."), which .NET's Unix Path class treats as
 * relative: FNA's TitleLocation and storage paths would be prefixed with the current directory.
 * Managed code sees device paths rooted at "/" instead (vita_paths.c translates them back).
 */

#include <SDL3/SDL.h>
#include <string.h>
#include "vita_sdl_overrides.h"

/* Set by a launcher whose game files are not in app0: (Stardew lives in ux0:data). */
const char *vita_sdl_base_path = NULL;

const char *vita_SDL_GetBasePath(void)
{
	static char rooted[256];
	const char *path = vita_sdl_base_path != NULL ? vita_sdl_base_path : SDL_GetBasePath();
	if (path == NULL || path[0] == '/') {
		return path;
	}
	SDL_snprintf(rooted, sizeof(rooted), "/%s", path);
	return rooted;
}

char *vita_SDL_GetPrefPath(const char *org, const char *app)
{
	char *path = SDL_GetPrefPath(org, app);
	char *rooted;
	size_t length;
	if (path == NULL || path[0] == '/') {
		return path;
	}
	length = SDL_strlen(path);
	rooted = (char *)SDL_malloc(length + 2);
	if (rooted == NULL) {
		return path;
	}
	rooted[0] = '/';
	SDL_memcpy(rooted + 1, path, length + 1);
	SDL_free(path);
	return rooted;
}
