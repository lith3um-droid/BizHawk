# Execute callbacks and registers for the 32X (PicoDrive)

Branch `32x-hooks` of this fork, based on tag `2.11.1`, gives BizHawk's PicoDrive core execute callbacks and
registers for its three CPUs: the main 68000 and the master and slave SH-2s of the 32X. Lua scripts and tools reach
them through the usual API: `event.on_bus_exec`, `event.on_bus_exec_any` and `emu.getregisters`.

It is a research instrument. PicoDrive's licence (`waterbox/picodrive/COPYING`) is non-commercial and asks that
modified redistributions come with their complete source, which this fork is.

Files: `Dist/32x-hooks/` has `picodrive.wbx.zst` and `BizHawk.Emulation.Cores.dll` built from this branch, their
SHA-256 sums in `SHA256SUMS`, and `smoke_32x.lua`.

## Installing on Windows

1. **Copy** your BizHawk 2.11.1 folder, e.g. `BizHawk-2.11.1` to `BizHawk-2.11.1-32x-hooks`. Never change the
   original install.
2. Check the two files against `SHA256SUMS`, e.g. in PowerShell:
   `Get-FileHash picodrive.wbx.zst, BizHawk.Emulation.Cores.dll -Algorithm SHA256`.
3. Copy `picodrive.wbx.zst` and `BizHawk.Emulation.Cores.dll` into the **copy's** `dll\` folder, replacing the files
   there.
4. Start `EmuHawk.exe` from the copy and load a 32X game; 2.11.1 runs 32X games on PicoDrive.
5. Keep the copy's savestates apart from the original's: they don't load across the two (see *Savestates*).

The new DLL also loads the stock `picodrive.wbx.zst`, which then has no callbacks or registers (tested). The stock
DLL should run the new core file the same way (untested).

## What you get

### Scopes

| scope | CPU | address passed to callbacks |
|---|---|---|
| `M68K BUS` | the main 68000 | the PC, masked to 24 bits |
| `SH2 Master` | the master SH-2 | the PC as executed, all 32 bits |
| `SH2 Slave` | the slave SH-2 | the PC as executed, all 32 bits |

A callback runs **before** the instruction at its address. It gets `(addr, val, flags)`: `val` is the instruction's
first 16-bit word, and `flags` is `AccessExecute` (`0x4000`) alone, as in Genplus-gx. Plain Mega Drive and Sega CD
games have the same three scopes; the SH-2 ones never fire there.

### Registers

`emu.getregisters()` always returns all 66 names, 32 bits each except `M68K SR` (16):

- `M68K D0`…`M68K D7`, `M68K A0`…`M68K A7`, `M68K PC`, `M68K SR`, `M68K USP`, `M68K SSP`;
- `SH2M R0`…`SH2M R15`, `SH2M PC`, `SH2M PR`, `SH2M SR`, `SH2M GBR`, `SH2M VBR`, `SH2M MACH`, `SH2M MACL`;
- the same with `SH2S` for the slave.

`A7` is the active stack pointer; `USP` and `SSP` are both stack pointers whatever the mode. Inside a callback, the
PC of the reporting CPU is the reported instruction's address and the other registers are as they are before it
runs. The SH-2 registers are zeros until the 32X starts. Registers can't be set (`SetCpuRegister` throws
`NotImplementedException`).

### Addresses

- **68000:** 24 bits. Register a callback at a 24-bit address; one with the top byte set (`0xFFFF8000`) never
  matches, unless its mask clears that byte. Once the 32X adapter is on, the cartridge is also seen at `$880000`,
  and code often runs from there: watch the address the CPU executes.
- **SH-2:** `ppc` as executed. Cached and cache-through aliases are different addresses: SDRAM is `0x06000000` and
  `0x26000000`, the cartridge `0x02000000` and `0x22000000`. Watch both if code runs from both.
- **Delay slots** report their own address: a `BSR` at `a` reports `a`, then its delay slot `a+2`, then the target.
  In the slot, `PR` is already the return address.
- **Masks:** when every execute callback of a scope has an address and a full mask (`0xFFFFFF` or more on the
  68000, `0xFFFFFFFF` on an SH-2), the core stops only at those addresses. Otherwise, with an address-less callback
  or a partial mask, that CPU reports every instruction and BizHawk does the matching, which is much slower.

### Lua

```lua
event.on_bus_exec(function(addr, val, flags)
  local r = emu.getregisters()
  console.log(string.format("SH2M %08X  PR %08X  R4 %08X", addr, r["SH2M PR"], r["SH2M R4"]))
end, 0x06000400, "an SH-2 routine", "SH2 Master")
```

`event.on_bus_exec_any(fn, name, scope)` is the address-less form: every instruction. In 2.11.1,
`MemoryCallbackSystem` calls an address-less callback for every scope, not only its own, so it also sees the
instructions of every other CPU that currently reports (one with any execute callback). To count one CPU, register
the address-less callback on that scope alone. `smoke_32x.lua` does so, one frame per CPU.

## Limits

- **Order across CPUs.** PicoDrive runs the CPUs in time slices, so callbacks of different CPUs come in slice
  order, not in cycle order. Each CPU's own callbacks are in program order.
- **Skipped iterations.** PicoDrive puts an SH-2 that polls memory or a register to sleep until the location
  changes (`p32x_sh2_poll_event`), and MAME's SH-2 core fast-forwards `DT`/`BF $-2` delay loops
  (`BUSY_LOOP_HACKS`). Those iterations never run, so they aren't reported. Harmless for logging routine calls;
  don't count loop iterations.
- **SH-2 callbacks during a 68000 instruction.** When the 68000 touches the 32X, PicoDrive catches the SH-2s up
  (`p32x_sync_sh2s`) inside that 68000 access. Their callbacks then run mid-instruction, and the 68000 registers
  they read are those of an unfinished instruction.
- **Aliases and delay slots,** see *Addresses*.
- **Sega CD:** only the main 68000 is reported; the sub-CPU runs through the same code and is filtered out. The Z80
  isn't reported either.
- **Read and write callbacks are not done.** `event.on_bus_read`/`on_bus_write` register without an error on this
  core, but never fire.
- **No stepping and no cycle count:** `CanStep` is false, and `Step`, `SetCpuRegister` and `TotalExecutedCycles`
  throw `NotImplementedException`.
- A callback mustn't change emulation state. Adding and removing callbacks from inside one is fine.
- **`32X RAM` and `32X FB` domains:** stock 2.11.1 gives a 32X cartridge neither, so Lua can't read SDRAM or the
  framebuffer there: `PicoDrive` preallocates the 32X memory, which makes those domains exist, only for the gamedb
  option `32X`, and no gamedb entry has it. This branch also preallocates it for every game whose system is `32X`,
  which is every cartridge EmuHawk runs on PicoDrive, so they have `68K RAM`, `VRAM`, `Z80 RAM`, `CRAM`, `VSRAM`,
  `MD CART`, `32X RAM`, `32X FB` and `SRAM`. Emulation is unchanged: per-frame video, audio and 68K RAM hashes over
  300 frames are identical with and without it, on both test ROMs; Mega Drive ROMs get no new domains.

## Savestates

**Savestates made with the stock 2.11.1 core don't load in this build, and this build's don't load in the stock
one.** The waterbox host writes a SHA-256 of the core file into every state and refuses a state from any other
file: tested both ways against the stock 2.11.1 core file and against an unmodified core rebuilt here, all four
refused with `Waterbox Error: Bad hash for state`. Any rebuild of the core does the same. Movies that start from
power-on hold no state, so they shouldn't be affected (untested).

Within this build, the callbacks and watch lists are left out of states: loading a state keeps whatever callbacks
are registered at that moment, and they fire as before (tested).

## How it works

- **Native** (`waterbox/picodrive/bizhawk.c`, `bizhawk_hooks.h`, `cpu/fame/famec.c`, `cpu/sh2/`): the 68000 (FAME/C,
  in both its dispatch modes) and the MAME SH-2 interpreter test a per-CPU flag before each instruction; an SH-2 that
  watches something runs a reporting copy of the interpreter for its slice (`sh2pico_bizhook.c`). A 64K-bit filter
  and a sorted list pick the watched addresses. Exports: `SetExecCallback`, `SetExecWatchList`, `GetRegisters`. The
  hook state is `ECL_INVISIBLE`, so states leave it out.
- **C#** (`src/BizHawk.Emulation.Cores/Consoles/Sega/PicoDrive/`): `PicoDrive.IDebuggable.cs`. The exec delegate is in
  `PreInit`'s delegate array and is installed once. On `CallbackAdded`/`CallbackRemoved`/`ActiveChanged` it rebuilds
  each CPU's list from the execute callbacks of its scope, kept from those events because `MemoryCallbackSystem`
  can't be enumerated while they run. The three exports are bound apart (`LibPicoDriveHooks`) and only if the core
  file has them, since `BizInvoker` binds every import of an invoker at once; that is what lets this DLL load an
  unmodified core, which the regression test needs.

## What was tested

Nothing ran in EmuHawk itself (no GUI here); `smoke_32x.lua` was **not run**. Test programs are written from source
(`waterbox/picodrive/hooktest/mkroms.py`); no ROM is committed.

**Native harness** (`waterbox/picodrive/hooktest`, PicoDrive built with the host compiler, no waterbox),
`python3 waterbox/picodrive/hooktest/run_tests.py --gcc --pristine <2.11.1's waterbox/picodrive> --bench`: all OK.
- Every hit checked on both test ROMs, 90 frames, in both FAME/C modes (clang: function table; gcc: computed gotos):
  451,928 checks on the Mega Drive ROM and 769,776 on the 32X ROM, per compiler. They cover the PC, the registers
  the caller and the routine set, `PR`/the stacked return address, USP/SSP across user mode, a TRAP and an
  interrupt, delay slots, `MACH`/`MACL`, each routine's call counter in RAM, and aliases that must not fire.
- Per-frame hashes of video, audio and RAM, 600 frames: identical with hooks off, with every instruction watched,
  with logged watches, and against the unmodified core, for both compilers and both ROMs.
- `smoke_32x.py` on your own 32X cartridge (not in the repo): entry points `060001A0`/`060001A4` from the header,
  both hit once, at frame 5, with `GBR 20004000`, `VBR 06000000`/`06000080`, `R15 06040000`/`0603F800`.

**Full stack** (`src/BizHawk.Tests.PicoDriveHooks`, see its readme;
`PICODRIVE_BASELINE_WBX=<unmodified core> src/BizHawk.Tests.PicoDriveHooks/run_tests.sh`): the committed
`picodrive.wbx.zst` through `PicoDrive`, `WaterboxCore` and the waterbox host on Linux, only through
`MemoryCallbacks.Add`, `GetCpuFlagsAndRegisters`, memory domains and `IStatable`. All pass:
- the checks of the native harness on both ROMs: 854,668 on the 32X ROM (plus per-frame counts against the routines'
  own counters, about 76-109 hits per routine per frame), 401,162 on the Mega Drive ROM;
- the 66 register names and sizes, with the SH-2s zero on a Mega Drive cartridge;
- address-less callbacks, one scope at a time over the same frame (replayed from a state): 12,030 68000, 154,994
  master and 154,516 slave instructions in frame 30, within 0.02 % of the native harness (whose glibc `rand()`
  starts the 68000 elsewhere in the frame than the waterbox's musl one); with all three registered each callback
  counts all 321,540;
- removing callbacks stops them, and leaves the others firing;
- a savestate round trip: 7,376 hits in 20 frames, the same after loading; and a state loaded while other callbacks
  are registered keeps those;
- per-frame hashes of video, audio and all writable memory domains, 300 frames, both ROMs: identical to an
  unmodified core built with the same toolchain, and identical with every instruction watched;
- savestates across cores, as above;
- opt-in, `Smoke32X` on your cartridge, the steps of `smoke_32x.lua`: `MD CART` reads the header big-endian, the
  same bytes as the file; both entry points hit once, at frame 5, with the registers above;
  one frame each counted 11,607 68000, 198,266 master and 166,264 slave instructions.

**Not tested:** EmuHawk and its tools (Lua Console, Debugger, trace logger), Windows, the Lua script itself, BIOS
mode (real 32X BIOS files, needed for movies), PAL timing, Sega CD and 32X CD games, a gcc-built waterbox core (the
shipped one is clang; the harness covered gcc).

## Performance

**Full stack**, 32X test ROM, Linux, 5 runs of 600 frames (60 for address-less), median frames per second:

| configuration | fps | callbacks per frame |
|---|---|---|
| unmodified core | 512 | |
| this core, no callbacks | 531 | |
| 4 watches that never hit (68000, master x2, slave) | 463 | 0 |
| 4 watches that hit (`sub68`, `sub1`, `sub2`, `ssub`) | 468 | 369 |
| address-less on all three scopes | 83 | 321,540 instructions, each to 3 callbacks |

With nothing watched the difference is noise. Watching any address on an SH-2 costs about 12 %: that CPU runs the
reporting interpreter and filters each instruction. Watching everything costs about 32 ns per instruction, with
three callbacks each.

**Native harness**, same ROM, 600 frames x 8 runs pinned to one core, median fps:

| configuration | clang (as shipped) | gcc (computed gotos) |
|---|---|---|
| unmodified core | 479 | 376 |
| hooks, nothing watched | 488 | 341 |
| 4 watches that never hit | 436 | |
| 8 watches that hit, counting | 447 | |
| every instruction on all three CPUs, counting | 300 | |

In the gcc mode, which BizHawk doesn't ship, the idle hook costs about 9 %: FAME/C's computed-goto dispatch
jumps through one shared hook block.
