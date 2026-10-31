/* Vita GL glue for FNA3D's OpenGL driver (local port glue, not an FNA3D contribution).
 *
 * Force-included (-include) into every FNA3D/MojoShader translation unit when building for the Vita.
 * Our SDL3 build has no GL video backend, so the SDL_GL_* calls in FNA3D_Driver_OpenGL.c are renamed
 * to VitaGLGlue_* functions (vita_gl_glue.c) that drive vitaGL directly. The renaming also applies to
 * SDL's own prototypes, so the glue's definitions are checked against SDL's exact signatures.
 *
 * Must not include any header itself: it is seen before SDL.h.
 */
#ifndef VITA_GL_GLUE_H
#define VITA_GL_GLUE_H

#ifdef __vita__
#define SDL_GL_LoadLibrary VitaGLGlue_LoadLibrary
#define SDL_GL_SetAttribute VitaGLGlue_SetAttribute
#define SDL_GL_GetAttribute VitaGLGlue_GetAttribute
#define SDL_GL_CreateContext VitaGLGlue_CreateContext
#define SDL_GL_DestroyContext VitaGLGlue_DestroyContext
#define SDL_GL_GetProcAddress VitaGLGlue_GetProcAddress
#define SDL_GL_SwapWindow VitaGLGlue_SwapWindow
#define SDL_GL_SetSwapInterval VitaGLGlue_SetSwapInterval
#endif

#endif /* VITA_GL_GLUE_H */
