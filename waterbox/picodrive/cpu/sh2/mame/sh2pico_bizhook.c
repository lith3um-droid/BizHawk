// The SH-2 interpreter once more, reporting each instruction to BizHawk's
// execute hook (see bizhawk.c). sh2_execute() runs this copy for a time
// slice while the CPU watches something, so the usual loop has no test.
// Hooks can only change mid-slice from a callback, and no callback runs in
// a slice of a CPU that watches nothing: the CPUs run one at a time.
// In a file of its own, each opcode handler keeps its single caller, and
// the compiler inlines them all, as it does in sh2pico.c.
#ifndef DRC_CMP
#define SH2_BIZHOOK
#include "sh2pico.c"
#endif
