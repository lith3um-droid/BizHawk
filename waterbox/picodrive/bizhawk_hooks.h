// Execute hooks for the BizHawk frontend (see bizhawk.c).
//
// The CPU cores test one per-CPU flag before each instruction and call the
// slow path only when it is set, which bizhawk.c does while an execute
// callback is installed and that CPU has something to watch.

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

// pc: address of the instruction about to execute, sr: the SR composed from
// the live flags, opcode: its first word
void biz_m68k_exec_hook(unsigned int pc, unsigned int sr, unsigned int opcode);

// called after the opcode fetch; reports sh2->ppc
struct SH2_;
void biz_sh2_exec_hook(struct SH2_ *sh2, unsigned int opcode);

#endif
