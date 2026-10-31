// vitaGL smoke test (milestone 2, step 1): the GL path FNA3D will use, without FNA3D.
//
// Initializes vitaGL, logs GL strings and free memory, compiles a GLSL 1.20 shader pair (the dialect
// MojoShader emits for FNA3D's OpenGL driver) through vitaGL's runtime shader compiler, and draws a
// textured, slowly scrolling quad with a procedural 256x256 checker texture. Writes everything to
// ux0:data/vgltest/log.txt and quits after 20 seconds or on Start.
//
// vitaGL's runtime shader compiler needs libshacccg.suprx in ur0:data/ (or ur0:data/external/);
// without it shader compilation fails and the log says so.

#include <vitaGL.h>
#include <psp2/ctrl.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

#define LOG_PATH "ux0:data/vgltest/log.txt"

static void log_line(const char *fmt, ...)
{
	char line[512];
	va_list args;
	va_start(args, fmt);
	int length = vsnprintf(line, sizeof(line) - 1, fmt, args);
	va_end(args);
	if (length < 0) {
		return;
	}
	if (length > (int)sizeof(line) - 2) {
		length = (int)sizeof(line) - 2;
	}
	line[length++] = '\n';
	printf("%.*s", length, line);
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, line, (SceSize)length);
		sceIoClose(fd);
	}
}

static void log_free_memory(const char *when)
{
	SceKernelFreeMemorySizeInfo info;
	info.size = sizeof(info);
	if (sceKernelGetFreeMemorySize(&info) == 0) {
		log_line("[%s] free memory: user %d KB, cdram %d KB, phycont %d KB", when,
			info.size_user / 1024, info.size_cdram / 1024, info.size_phycont / 1024);
	}
}

static int file_exists(const char *path)
{
	SceIoStat stat;
	return sceIoGetstat(path, &stat) >= 0;
}

static GLuint compile_shader(GLenum type, const char *name, const char *source)
{
	GLuint shader = glCreateShader(type);
	glShaderSource(shader, 1, &source, NULL);
	glCompileShader(shader);
	GLint ok = 0;
	glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
	char info[512] = { 0 };
	glGetShaderInfoLog(shader, sizeof(info) - 1, NULL, info);
	log_line("%s shader compile: %s%s%s", name, ok ? "ok" : "FAILED", info[0] ? " - " : "", info);
	return ok ? shader : 0;
}

// GLSL 1.20, as MojoShader generates for FNA3D's desktop OpenGL path.
static const char *VERTEX_SHADER =
	"#version 120\n"
	"attribute vec2 a_position;\n"
	"attribute vec2 a_texcoord;\n"
	"uniform float u_offset;\n"
	"varying vec2 v_texcoord;\n"
	"void main() {\n"
	"	v_texcoord = a_texcoord + vec2(u_offset, 0.0);\n"
	"	gl_Position = vec4(a_position, 0.0, 1.0);\n"
	"}\n";

static const char *FRAGMENT_SHADER =
	"#version 120\n"
	"uniform sampler2D u_texture;\n"
	"varying vec2 v_texcoord;\n"
	"void main() {\n"
	"	gl_FragColor = texture2D(u_texture, v_texcoord);\n"
	"}\n";

int main(void)
{
	sceIoMkdir("ux0:data/vgltest", 0777);
	sceIoRemove(LOG_PATH);
	log_line("vgltest start");
	log_line("libshacccg.suprx: ur0:data/ %s, ur0:data/external/ %s",
		file_exists("ur0:data/libshacccg.suprx") ? "present" : "missing",
		file_exists("ur0:data/external/libshacccg.suprx") ? "present" : "missing");
	log_free_memory("before vglInit");

	// 8 MB legacy pool; textures and framebuffers go to vitaGL's own memory pools.
	// The return value only reports a resolution fallback (GL_TRUE = requested size was too large),
	// not success or failure.
	if (vglInitExtended(0x800000, 960, 544, 0x1000000, SCE_GXM_MULTISAMPLE_NONE)) {
		log_line("vglInitExtended: resolution fell back to the display maximum");
	}
	log_free_memory("after vglInit");
	log_line("GL_VENDOR: %s", (const char *)glGetString(GL_VENDOR));
	log_line("GL_RENDERER: %s", (const char *)glGetString(GL_RENDERER));
	log_line("GL_VERSION: %s", (const char *)glGetString(GL_VERSION));
	const char *glsl = (const char *)glGetString(GL_SHADING_LANGUAGE_VERSION);
	log_line("GL_SHADING_LANGUAGE_VERSION: %s", glsl ? glsl : "(null)");
	const char *extensions = (const char *)glGetString(GL_EXTENSIONS);
	log_line("GL_EXTENSIONS: %.400s", extensions ? extensions : "(null)");

	// Without SceShaccCg vitaGL reports every compile as successful and crashes later, so skip the
	// shader path entirely and only exercise texture upload and clear/swap.
	int have_compiler = file_exists("ur0:data/libshacccg.suprx") || file_exists("ur0:data/external/libshacccg.suprx");
	if (!have_compiler) {
		log_line("no shader compiler: skipping shaders, drawing clears only");
	}
	GLuint vs = have_compiler ? compile_shader(GL_VERTEX_SHADER, "vertex", VERTEX_SHADER) : 0;
	GLuint fs = have_compiler ? compile_shader(GL_FRAGMENT_SHADER, "fragment", FRAGMENT_SHADER) : 0;
	GLuint program = 0;
	if (vs && fs) {
		program = glCreateProgram();
		glAttachShader(program, vs);
		glAttachShader(program, fs);
		glBindAttribLocation(program, 0, "a_position");
		glBindAttribLocation(program, 1, "a_texcoord");
		glLinkProgram(program);
		GLint linked = 0;
		glGetProgramiv(program, GL_LINK_STATUS, &linked);
		log_line("program link: %s", linked ? "ok" : "FAILED");
		if (!linked) {
			program = 0;
		}
	}

	// Procedural checker texture, RGBA8.
	enum { TEX = 256 };
	unsigned char *pixels = malloc(TEX * TEX * 4);
	for (int y = 0; y < TEX; y++) {
		for (int x = 0; x < TEX; x++) {
			unsigned char *p = pixels + (y * TEX + x) * 4;
			int checker = ((x / 32) + (y / 32)) & 1;
			p[0] = checker ? 255 : (unsigned char)x;
			p[1] = checker ? 200 : (unsigned char)y;
			p[2] = checker ? 40 : 160;
			p[3] = 255;
		}
	}
	GLuint texture;
	glGenTextures(1, &texture);
	glBindTexture(GL_TEXTURE_2D, texture);
	glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, TEX, TEX, 0, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_REPEAT);
	glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_REPEAT);
	free(pixels);
	log_line("texture upload: glGetError 0x%04X", glGetError());
	log_free_memory("after texture");

	// Interleaved position (x, y) + texcoord (u, v), triangle strip.
	static const GLfloat quad[] = {
		-0.8f, -0.8f, 0.0f, 2.0f,
		 0.8f, -0.8f, 2.0f, 2.0f,
		-0.8f,  0.8f, 0.0f, 0.0f,
		 0.8f,  0.8f, 2.0f, 0.0f,
	};
	GLuint vbo;
	glGenBuffers(1, &vbo);
	glBindBuffer(GL_ARRAY_BUFFER, vbo);
	glBufferData(GL_ARRAY_BUFFER, sizeof(quad), quad, GL_STATIC_DRAW);

	GLint offset_location = program ? glGetUniformLocation(program, "u_offset") : -1;
	GLint sampler_location = program ? glGetUniformLocation(program, "u_texture") : -1;

	SceCtrlData pad;
	SceUInt64 start = sceKernelGetProcessTimeWide();
	SceUInt64 last_progress = start;
	unsigned frames = 0;
	for (;;) {
		SceUInt64 now = sceKernelGetProcessTimeWide();
		sceCtrlPeekBufferPositive(0, &pad, 1);
		if ((pad.buttons & SCE_CTRL_START) || now - start > 20000000ULL) {
			break;
		}
		int trace = frames < 2;
		if (trace) {
			log_line("frame %u: clear", frames);
		}
		glClearColor(0.1f, 0.15f, 0.25f, 1.0f);
		glClear(GL_COLOR_BUFFER_BIT);
		if (trace) {
			log_line("frame %u: draw", frames);
		}
		if (program) {
			glUseProgram(program);
			glUniform1f(offset_location, (float)((now - start) % 4000000ULL) / 4000000.0f);
			glUniform1i(sampler_location, 0);
			glActiveTexture(GL_TEXTURE0);
			glBindTexture(GL_TEXTURE_2D, texture);
			glBindBuffer(GL_ARRAY_BUFFER, vbo);
			glEnableVertexAttribArray(0);
			glEnableVertexAttribArray(1);
			glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(GLfloat), (void *)0);
			glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(GLfloat), (void *)(2 * sizeof(GLfloat)));
			glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
		}
		if (trace) {
			log_line("frame %u: swap", frames);
		}
		vglSwapBuffers(GL_FALSE);
		if (trace) {
			log_line("frame %u: done", frames);
		}
		frames++;
		if (now - last_progress >= 5000000ULL) {
			last_progress = now;
			log_line("alive at %llu ms, %u frames, glGetError 0x%04X", (now - start) / 1000ULL, frames, glGetError());
		}
	}
	SceUInt64 elapsed = sceKernelGetProcessTimeWide() - start;
	log_line("frames: %u in %llu ms (%.1f fps)", frames, elapsed / 1000ULL, elapsed ? frames * 1000000.0 / elapsed : 0.0);
	log_free_memory("before quit");
	// This vitaGL has no teardown call; process exit releases GXM and memory blocks.
	log_line("vgltest end");
	sceKernelExitProcess(0);
	return 0;
}
