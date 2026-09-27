// Execute hooks for the BizHawk frontend (see bizhawk.c).
//
// bizhawk.c sets a per-CPU flag while there is an execute callback and that
// CPU watches something. The 68000 tests it before each instruction, the
// SH-2s once per time slice, to pick a copy of the interpreter that reports.
// Then each instruction's address is tested against the CPU's filter, and
// only a hit calls the slow path, which does the exact match.

#ifndef BIZHAWK_HOOKS_H
#define BIZHAWK_HOOKS_H

// CPU ids used by the exported functions
#define BIZ_CPU_M68K  0 // main 68000 (not the Sega CD sub CPU)
#define BIZ_CPU_MSH2  1 // 32X master SH-2
#define BIZ_CPU_SSH2  2 // 32X slave SH-2
#define BIZ_CPU_COUNT 3

#ifdef __GNUC__
#define BIZ_UNLIKELY(x) __builtin_expect(!!(x), 0)
#else
#define BIZ_UNLIKELY(x) (x)
#endif

extern unsigned char biz_exec_hook_on[BIZ_CPU_COUNT];

// Per CPU, bit (addr >> 1) & 0xffff is set if an instruction at such an
// address may be watched. Watch-all sets them all.
extern unsigned char biz_exec_filter[BIZ_CPU_COUNT][0x10000 / 8];
#define BIZ_EXEC_FILTER(filter, addr) \
	((filter)[(addr) >> 4 & 0x1fff] & 1 << ((addr) >> 1 & 7))

// pc: address of the instruction about to execute, sr: the SR composed from
// the live flags, opcode: its first word; called after a filter hit
void biz_m68k_exec_hook(unsigned int pc, unsigned int sr, unsigned int opcode);

// called after the opcode fetch and a filter hit; reports sh2->ppc
struct SH2_;
void biz_sh2_exec_hook(struct SH2_ *sh2, unsigned int opcode);

#endif
