// Native test harness for PicoDrive's BizHawk interface, without the waterbox.
//
// Links the core's sources and bizhawk.c, stubs emulibc, and drives the same
// exports as the C# side: Init, FrameAdvance, GetMemoryAreas, and the
// execute hook exports if the core has them (they are weak here, so the same
// driver also builds against an unmodified core).
//
// usage: harness [options] ROM
//   -f N          run N frames (default 300)
//   -32x          preallocate the 32X memory, as the C# side does for 32X games
//   -hash FILE    per-frame hashes of the video, audio and writable memory areas
//   -w CPU:A[,A]  watch these hex addresses on CPU (0 68000, 1 master SH-2,
//                 2 slave SH-2) and log each hit with the CPU's registers
//   -a CPU        watch every instruction of CPU and count them
//   -log FILE     where hits go (default stdout)
//   -p A          at each logged hit, also log the 32-bit value at hex
//                 address A (68000 RAM or 32X SDRAM)
//   -counts FILE  per-frame hit counts per CPU
//   -q            count hits on -w addresses too, without logging them
//   -bench        print the run time and frames per second
// FILE may be "-" for stdout.

#define _GNU_SOURCE
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <limits.h>

#include "../../emulibc/emulibc.h"
#include "../../emulibc/waterboxcore.h"

// emulibc stubs: no pools to seal or hide outside the waterbox
void *alloc_sealed(size_t size) { return calloc(1, size); }
void *alloc_invisible(size_t size) { return calloc(1, size); }
void *alloc_plain(size_t size) { return calloc(1, size); }
void _debug_puts(const char *s) { fprintf(stderr, "%s\n", s); }

typedef struct
{
	FrameInfo b;
	uint32_t Buttons;
} MyFrameInfo;

int Init(int cd, int _32xPreinit, int regionAutoOrder, int regionOverride);
void FrameAdvance(MyFrameInfo *f);
void GetMemoryAreas(MemoryArea *m);
int Is32xActive(void);
__attribute__((weak)) void SetExecCallback(void (*cb)(int cpu, uint32_t addr, uint32_t opcode));
__attribute__((weak)) int SetExecWatchList(int cpu, const uint32_t *addrs, int count, int watchAll);
__attribute__((weak)) int GetRegisters(int cpu, uint32_t *out);

#define NCPU 3
#define MAX_WATCH 256
#define MAX_PROBE 32
#define MAX_AREAS 16

static uint32_t watch[NCPU][MAX_WATCH];
static int nwatch[NCPU], watch_all[NCPU];
static uint32_t probe[MAX_PROBE];
static int nprobe;
static FILE *log_out;
static int quiet;
static int frame;
static uint64_t hits[NCPU];
static MemoryArea areas[MAX_AREAS];

static FILE *open_out(const char *name)
{
	FILE *f = strcmp(name, "-") ? fopen(name, "w") : stdout;
	if (!f)
	{
		perror(name);
		exit(1);
	}
	return f;
}

static const MemoryArea *find_area(const char *name)
{
	int i;
	for (i = 0; i < MAX_AREAS; i++)
	{
		if (areas[i].Data && !strcmp(areas[i].Name, name))
			return &areas[i];
	}
	return NULL;
}

// both areas hold 16-bit words in host order
static int peek32(uint32_t addr, uint32_t *val)
{
	const MemoryArea *a = NULL;
	uint32_t off = 0;
	const uint16_t *w;

	if ((addr & 0xffe00000) == 0x00e00000)
	{
		a = find_area("68K RAM");
		off = addr & 0xffff;
	}
	else if ((addr & 0xdffc0000) == 0x06000000)
	{
		a = find_area("32X RAM");
		off = addr & 0x3ffff;
	}
	if (!a || (off & 1) || off + 4 > (uint32_t)a->Size)
		return 0;
	w = (const uint16_t *)((const uint8_t *)a->Data + off);
	*val = (uint32_t)w[0] << 16 | w[1];
	return 1;
}

static void log_peek(uint32_t addr)
{
	uint32_t v;

	if (peek32(addr, &v))
		fprintf(log_out, " %08x", v);
	else
		fprintf(log_out, " -");
}

static void on_exec(int cpu, uint32_t addr, uint32_t opcode)
{
	uint32_t regs[32];
	int i, n;

	hits[cpu]++;
	if (watch_all[cpu] || quiet || !log_out)
		return;
	n = GetRegisters(cpu, regs);
	fprintf(log_out, "H %d %d %08x %04x", frame, cpu, addr, opcode);
	for (i = 0; i < n; i++)
		fprintf(log_out, " %08x", regs[i]);
	fprintf(log_out, " |");
	// the long at A7, i.e. a return address at a routine's entry
	if (cpu == 0)
		log_peek(regs[15] & 0xffffff);
	for (i = 0; i < nprobe; i++)
		log_peek(probe[i]);
	fputc('\n', log_out);
}

static uint64_t fnv1a(const void *p, size_t n)
{
	const uint8_t *b = p;
	uint64_t h = 0xcbf29ce484222325ull;
	while (n--)
		h = (h ^ *b++) * 0x100000001b3ull;
	return h;
}

static char tmp_dir[PATH_MAX], rom_link[PATH_MAX + 16];

static void cleanup(void)
{
	if (rom_link[0])
		unlink(rom_link);
	if (tmp_dir[0])
		rmdir(tmp_dir);
}

// bizhawk.c opens "romfile.md" (and the BIOS files, if any) in the current
// directory, so run in an empty directory holding only a link to the ROM
static void enter_rom_dir(const char *rom)
{
	char path[PATH_MAX];
	const char *t = getenv("TMPDIR");

	if (!realpath(rom, path))
	{
		perror(rom);
		exit(1);
	}
	snprintf(tmp_dir, sizeof(tmp_dir), "%s/pdhook.XXXXXX", t ? t : "/tmp");
	if (!mkdtemp(tmp_dir))
	{
		perror("mkdtemp");
		exit(1);
	}
	atexit(cleanup);
	snprintf(rom_link, sizeof(rom_link), "%s/romfile.md", tmp_dir);
	if (symlink(path, rom_link) || chdir(tmp_dir))
	{
		perror(rom_link);
		exit(1);
	}
}

int main(int argc, char **argv)
{
	int frames = 300, preinit = 0, bench = 0, i, cpu;
	const char *rom = NULL;
	FILE *hash_out = NULL, *count_out = NULL;
	MyFrameInfo fi;
	struct timespec t0, t1;
	double secs;

	for (i = 1; i < argc; i++)
	{
		const char *a = argv[i];
		const char *v = i + 1 < argc ? argv[i + 1] : NULL;
		if (!strcmp(a, "-f") && v)
			frames = atoi(argv[++i]);
		else if (!strcmp(a, "-32x"))
			preinit = 1;
		else if (!strcmp(a, "-hash") && v)
			hash_out = open_out(argv[++i]);
		else if (!strcmp(a, "-log") && v)
			log_out = open_out(argv[++i]);
		else if (!strcmp(a, "-counts") && v)
			count_out = open_out(argv[++i]);
		else if (!strcmp(a, "-bench"))
			bench = 1;
		else if (!strcmp(a, "-q"))
			quiet = 1;
		else if (!strcmp(a, "-a") && v)
		{
			cpu = atoi(argv[++i]);
			if (cpu < 0 || cpu >= NCPU)
				goto usage;
			watch_all[cpu] = 1;
		}
		else if (!strcmp(a, "-p") && v && nprobe < MAX_PROBE)
			probe[nprobe++] = strtoul(argv[++i], NULL, 16);
		else if (!strcmp(a, "-w") && v)
		{
			char *s = argv[++i], *end;
			cpu = strtol(s, &end, 10);
			if (cpu < 0 || cpu >= NCPU || *end != ':')
				goto usage;
			s = end;
			while (*s == ':' || *s == ',')
			{
				if (nwatch[cpu] == MAX_WATCH)
					goto usage;
				watch[cpu][nwatch[cpu]++] = strtoul(s + 1, &end, 16);
				s = end;
			}
		}
		else if (a[0] != '-' && !rom)
			rom = a;
		else
			goto usage;
	}
	if (!rom)
		goto usage;

	enter_rom_dir(rom);
	if (!Init(0, preinit, 0, 0))
	{
		fprintf(stderr, "Init failed\n");
		return 1;
	}
	GetMemoryAreas(areas);

	for (cpu = 0; cpu < NCPU; cpu++)
	{
		if (!nwatch[cpu] && !watch_all[cpu])
			continue;
		if (!SetExecCallback || !SetExecWatchList || !GetRegisters)
		{
			fprintf(stderr, "this core has no execute hooks\n");
			return 1;
		}
		SetExecWatchList(cpu, watch[cpu], nwatch[cpu], watch_all[cpu]);
		if (!log_out && !quiet && nwatch[cpu])
			log_out = stdout;
	}
	if (SetExecCallback && (log_out || count_out || bench))
		SetExecCallback(on_exec);

	memset(&fi, 0, sizeof(fi));
	fi.b.VideoBuffer = calloc(512 * 512, sizeof(uint32_t));
	fi.b.SoundBuffer = calloc(4096, sizeof(int16_t));

	clock_gettime(CLOCK_MONOTONIC, &t0);
	for (frame = 0; frame < frames; frame++)
	{
		uint64_t before[NCPU];
		memcpy(before, hits, sizeof(hits));
		FrameAdvance(&fi);
		if (count_out)
		{
			fprintf(count_out, "C %d", frame);
			for (cpu = 0; cpu < NCPU; cpu++)
				fprintf(count_out, " %llu", (unsigned long long)(hits[cpu] - before[cpu]));
			fputc('\n', count_out);
		}
		if (hash_out)
		{
			// areas that exist only once the 32X starts show up then
			GetMemoryAreas(areas);
			fprintf(hash_out, "F %d %dx%d video=%016llx audio=%016llx", frame,
				fi.b.Width, fi.b.Height,
				(unsigned long long)fnv1a(fi.b.VideoBuffer, (size_t)fi.b.Width * fi.b.Height * 4),
				(unsigned long long)fnv1a(fi.b.SoundBuffer, (size_t)fi.b.Samples * 4));
			for (i = 0; i < MAX_AREAS; i++)
			{
				const MemoryArea *m = &areas[i];
				if (m->Data && (m->Flags & MEMORYAREA_FLAGS_WRITABLE))
					fprintf(hash_out, " [%s]=%016llx", m->Name,
						(unsigned long long)fnv1a(m->Data, (size_t)m->Size));
			}
			fputc('\n', hash_out);
		}
	}
	clock_gettime(CLOCK_MONOTONIC, &t1);
	secs = (t1.tv_sec - t0.tv_sec) + (t1.tv_nsec - t0.tv_nsec) / 1e9;

	if (bench)
	{
		printf("frames %d  time %.3f s  fps %.1f  32x %d", frames, secs, frames / secs, Is32xActive());
		for (cpu = 0; cpu < NCPU; cpu++)
			printf("  cpu%d %.0f/frame", cpu, (double)hits[cpu] / frames);
		printf("\n");
	}
	if (log_out)
		fflush(log_out);
	if (hash_out)
		fflush(hash_out);
	if (count_out)
		fflush(count_out);
	return 0;

usage:
	fprintf(stderr, "usage: %s [-f N] [-32x] [-hash FILE] [-w CPU:A[,A]] [-a CPU] [-log FILE] [-p A] [-counts FILE] [-q] [-bench] ROM\n", argv[0]);
	return 2;
}
