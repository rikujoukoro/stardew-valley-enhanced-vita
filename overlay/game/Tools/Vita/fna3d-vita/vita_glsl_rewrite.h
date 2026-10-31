/* GLSL rewrite applied to MojoShader output before vitaGL compiles it (local port glue). */
#ifndef VITA_GLSL_REWRITE_H
#define VITA_GLSL_REWRITE_H

/* MojoShader's glsl120 profile passes data between stages through the legacy built-in varyings
 * gl_FrontColor/gl_Color, gl_FrontSecondaryColor/gl_SecondaryColor and gl_TexCoord[n]. vitaGL's
 * GLES-based translator has no such built-ins, so they become explicit varyings with matching names
 * in both stages (vgl_Color0, vgl_Color1, vgl_TexCoordN), declared right after the #version line.
 *
 * Returns a malloc'd, NUL-terminated rewritten source, or NULL when the source uses none of them.
 */
char *VitaGLSL_RewriteLegacyVaryings(const char *source);

#endif /* VITA_GLSL_REWRITE_H */
