#ifndef VITA_SDL_OVERRIDES_H
#define VITA_SDL_OVERRIDES_H

/* Directory SDL_GetBasePath reports to managed code (FNA's TitleLocation), as a device path ending
 * in '/', e.g. "ux0:data/StardewValley/". NULL (the default) keeps SDL's own, app0:/. */
extern const char *vita_sdl_base_path;

const char *vita_SDL_GetBasePath(void);
char *vita_SDL_GetPrefPath(const char *org, const char *app);

#endif
