// SDL3 smoke test for the PS Vita toolchain (milestone 1).
//
// Opens a 960x544 window with SDL's Vita (GXM) renderer, animates the background, draws a square per
// touch point and changes color on gamepad buttons. Writes the renderer name, free memory as reported
// by the Vita kernel, frame count and input events to ux0:data/sdl3test/log.txt.
// Exits after 20 seconds or when Start is pressed.

#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

#define LOG_PATH "ux0:data/sdl3test/log.txt"

// Each line is appended with sceIo directly (open, write, close) so it lands even if the app is
// killed; newlib's buffered FILE output didn't reach the file in Vita3K. Also printed to stdout,
// which Vita3K shows in its own log.
static void log_line(const char *fmt, ...)
{
	char line[256];
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
	line[length] = '\0';
	printf("%s", line);
	SceUID fd = sceIoOpen(LOG_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0666);
	if (fd >= 0) {
		sceIoWrite(fd, line, (SceSize)length);
		sceIoClose(fd);
	}
}

// How much the C heap really gives: allocate 1 MB blocks until malloc fails, then free them.
static void log_malloc_probe(const char *when)
{
	enum { BLOCK = 1024 * 1024, MAX_BLOCKS = 1024 };
	static void *blocks[MAX_BLOCKS];
	int count = 0;
	while (count < MAX_BLOCKS) {
		void *p = malloc(BLOCK);
		if (!p) {
			break;
		}
		// Touch one byte per page so the memory is really committed.
		for (int i = 0; i < BLOCK; i += 4096) {
			((volatile char *)p)[i] = 1;
		}
		blocks[count++] = p;
	}
	for (int i = 0; i < count; i++) {
		free(blocks[i]);
	}
	log_line("[%s] malloc probe: %d MB allocatable in 1 MB blocks", when, count);
}

static void log_free_memory(const char *when)
{
	SceKernelFreeMemorySizeInfo info;
	info.size = sizeof(info);
	if (sceKernelGetFreeMemorySize(&info) == 0) {
		log_line("[%s] free memory: user %d KB, cdram %d KB, phycont %d KB", when,
			info.size_user / 1024, info.size_cdram / 1024, info.size_phycont / 1024);
	} else {
		log_line("[%s] sceKernelGetFreeMemorySize failed", when);
	}
}

int main(int argc, char *argv[])
{
	(void)argc;
	(void)argv;
	sceIoMkdir("ux0:data/sdl3test", 0777);
	sceIoRemove(LOG_PATH);
	log_line("sdl3test start, SDL %d.%d.%d, build: %s", SDL_MAJOR_VERSION, SDL_MINOR_VERSION, SDL_MICRO_VERSION, SDL3TEST_VARIANT);
	log_free_memory("before SDL_Init");
	log_malloc_probe("before SDL_Init");
	log_free_memory("after malloc probe");

	if (!SDL_Init(SDL_INIT_VIDEO | SDL_INIT_GAMEPAD | SDL_INIT_EVENTS)) {
		log_line("SDL_Init failed: %s", SDL_GetError());
		return 1;
	}
	SDL_Window *window = NULL;
	SDL_Renderer *renderer = NULL;
	if (!SDL_CreateWindowAndRenderer("sdl3test", 960, 544, 0, &window, &renderer)) {
		log_line("SDL_CreateWindowAndRenderer failed: %s", SDL_GetError());
		SDL_Quit();
		return 1;
	}
	log_line("renderer: %s", SDL_GetRendererName(renderer));
	log_free_memory("after window+renderer");

	int gamepads = 0;
	SDL_JoystickID *ids = SDL_GetGamepads(&gamepads);
	for (int i = 0; i < gamepads; i++) {
		SDL_OpenGamepad(ids[i]);
	}
	SDL_free(ids);
	log_line("gamepads: %d", gamepads);

	Uint8 tint = 0;
	float touch_x[8], touch_y[8];
	int touches = 0;
	Uint64 start = SDL_GetTicks();
	Uint64 last_progress = start;
	Uint64 frames = 0;
	bool running = true;
	while (running) {
		SDL_Event event;
		while (SDL_PollEvent(&event)) {
			switch (event.type) {
			case SDL_EVENT_QUIT:
				running = false;
				break;
			case SDL_EVENT_GAMEPAD_BUTTON_DOWN:
				log_line("button %d down", event.gbutton.button);
				tint = (Uint8)(tint + 64);
				if (event.gbutton.button == SDL_GAMEPAD_BUTTON_START) {
					running = false;
				}
				break;
			case SDL_EVENT_FINGER_DOWN:
			case SDL_EVENT_FINGER_MOTION:
				if (touches < 8) {
					touch_x[touches] = event.tfinger.x * 960.0f;
					touch_y[touches] = event.tfinger.y * 544.0f;
					touches++;
				}
				if (event.type == SDL_EVENT_FINGER_DOWN) {
					log_line("touch at %.0f,%.0f", event.tfinger.x * 960.0f, event.tfinger.y * 544.0f);
				}
				break;
			default:
				break;
			}
		}

		Uint64 now = SDL_GetTicks();
		Uint8 wave = (Uint8)((now / 8) % 256);
		SDL_SetRenderDrawColor(renderer, wave, (Uint8)(255 - wave), tint, 255);
		SDL_RenderClear(renderer);
		SDL_SetRenderDrawColor(renderer, 255, 255, 255, 255);
		for (int i = 0; i < touches; i++) {
			SDL_FRect r = { touch_x[i] - 20.0f, touch_y[i] - 20.0f, 40.0f, 40.0f };
			SDL_RenderFillRect(renderer, &r);
		}
		touches = 0;
		SDL_RenderPresent(renderer);
		frames++;

		if (now - last_progress >= 5000) {
			last_progress = now;
			log_line("alive at %llu ms, %llu frames", (unsigned long long)(now - start), (unsigned long long)frames);
		}
		if (now - start > 20000) {
			running = false;
		}
	}

	Uint64 elapsed = SDL_GetTicks() - start;
	log_line("frames: %llu in %llu ms (%.1f fps)", (unsigned long long)frames, (unsigned long long)elapsed,
		elapsed ? frames * 1000.0 / elapsed : 0.0);
	log_free_memory("before quit");
	SDL_DestroyRenderer(renderer);
	SDL_DestroyWindow(window);
	SDL_Quit();
	log_line("sdl3test end");
	return 0;
}
