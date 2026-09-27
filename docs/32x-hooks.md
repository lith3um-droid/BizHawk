# Execute callbacks and registers for the 32X (PicoDrive)

Branch `32x-hooks` of this fork, based on tag `2.11.1`, gives BizHawk's PicoDrive core execute callbacks and
registers for its three CPUs: the main 68000 and the master and slave SH-2s of the 32X. Lua scripts and tools reach
them through the usual API: `event.on_bus_exec`, `event.on_bus_exec_any` and `emu.getregisters`.

It is a research instrument. PicoDrive's licence (`waterbox/picodrive/COPYING`) is non-commercial and asks that
modified redistributions come with their complete source, which this fork is.

Files: `Dist/32x-hooks/` has `picodrive.wbx.zst` and `BizHawk.Emulation.Cores.dll` built from this branch, their
SHA-256 sums in `SHA256SUMS`, `smoke_32x.lua`, and for the call recorder (see *Call recorder*) `record.bat`,
`recorder-example.txt` and `pdcr.py`.

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

## Call recorder

The core can record calls of chosen routines by itself, with no Lua: for each call, the CPU's registers and chosen
memory regions on entry and again on return, to replay against your own code. It also counts call targets, to find
the routines worth recording (*discover*). It is off unless the environment variable `PICODRIVE_CALL_RECORDER` names
a config file when the game loads; unset, it adds no watches and costs nothing. It works through the two files of
*Installing on Windows*.

### On Windows

1. Install the two files as above, in the copy of BizHawk.
2. Copy `recorder-example.txt` next to `EmuHawk.exe` as `recorder.txt`, and put in your routines and regions (see
   *Config file*).
3. Copy `record.bat` next to `EmuHawk.exe`, set `ROM` (and `MOVIE`, see *Movies*) in it, and run it. It sets the
   variable for that EmuHawk only (don't set it system-wide) and starts EmuHawk with the game, from the command line:
   `EmuHawk.exe [--movie=<movie>] <rom>` (in 2.11.1 the game is the positional argument `rom`, and `--movie path`
   works as well as `--movie=path`).
4. When the game loads, an on-screen message says `Call recorder on: ...`. A mistake in the config file shows a
   message box listing each wrong line, and the game runs with the recorder off.
5. Play through the frames of the window. The files are complete when the window ends, or when you close the game
   (File > Close ROM) or EmuHawk.
6. Read them with `python pdcr.py summary calls` (see *Output*).

Each time the core starts (loading the game, starting a movie, Reboot Core), the recorder starts over and overwrites
the files in its output folder: copy a recording away before playing again.

### Movies

`record.bat` with `MOVIE` set runs `EmuHawk.exe --movie=<movie> <rom>`: EmuHawk loads the game, then starts the core
again to play the movie read-only from power-on, and the recorder starts again with it, before any frame runs. Frame
numbers are then the movie's: frame 0 is the movie's first frame of input. This makes a recording repeatable, and
records exactly the frames you want.

Movies need the 32X BIOS files in EmuHawk's firmware (Config > Firmware): without them the core refuses the
deterministic mode that movies use. That couldn't be tested here (no BIOS files). Play the movie straight through:
loading a savestate, which rewinding and TAStudio's seeking do, cuts the pending calls short (they are written as
truncated) and replays frames, so the recording gets calls of both timelines.

### Config file

Text, one statement per line; `#` starts a comment. Numbers: hex as `0x...` or `$...`, otherwise decimal.

| statement | |
|---|---|
| `routine <cpu> <address> [name] [regions=label,...]` | a routine to record. `cpu` is `m68k`, `sh2m` or `sh2s`. 68000 addresses are 24-bit (see *Addresses*: code runs from `$88xxxx` once the 32X is on); SH-2 addresses are as executed, so the cached and cache-through aliases are different routines. `name` names the file (letters, digits, `_`, `-`, `.`; default `<cpu>_<address>`). Without `regions=`, every region is captured. |
| `region <domain> <start> <length> <label>` | memory captured at entry and at return: `length` bytes of a BizHawk memory domain from `start`, an offset in the domain. Domains: `68K RAM` (the 68000's `$FF0000`), `32X RAM` (SDRAM, `0x06000000` on the SH-2s), `32X FB`, `VRAM`, `CRAM`, `VSRAM`, `Z80 RAM`, `MD CART`, `SRAM`. The name may be quoted; unquoted, it is everything before the last three words. |
| `frames <first> <last>` | record frames `first` to `last`, inclusive. The first frame after power-on is 0; EmuHawk's frame counter shows the number of the next frame to run. Default: from frame 0 until the game is closed. |
| `maxcalls <n>` | stop recording a routine after `n` calls; default 1000. `index.json` says which routines stopped. |
| `discover <cpu> <frames>` | count call targets on that CPU for the first `frames` frames of the window, watching every instruction of it (see *Discover*). One per CPU. |
| `out <folder>` | the output folder, relative to the config file; default `calls`. |

A config needs at least one `routine` or `discover`. Errors (an unknown keyword, a bad number, an odd address, an
unknown domain or label, a region past the end of its domain, the same routine or name twice, an unreadable file or
output folder) turn the recorder off with a message that lists them all.

### Return detection

On entry (the routine's address runs), the recorder takes the CPU's registers and the regions, and works out where
and how the call must return:

- **68000:** the return address is the long at A7, read from 68K RAM (the stack must be in `$E00000-$FFFFFF`); the
  call returns at the first execution of that address with A7 equal to its value at entry + 4.
- **SH-2:** the return address is PR; the call returns at the first execution of PR with R15 equal to its value at
  entry. `BSR`, `BSRF` and `JSR` set PR to the call's address + 4, after the delay slot.

When the return runs (before its instruction), the recorder takes the registers and regions again and writes the
call, flagged *returned*. It watches the return addresses too, and keeps a stack of pending calls per CPU, so nesting
and recursion work: a return ends the innermost pending call it matches. A pending call is written as *abandoned*,
with the state at that moment, when:

- an outer call returns first (every call inside it is abandoned);
- at an instruction the recorder watches, the stack pointer is above its value at entry (on the 68000, the stack
  pointer of the mode the call was made in, SSP or USP, so an interrupt handler's stack doesn't count);
- 256 calls are pending on that CPU and another starts (the oldest is abandoned).

A call still pending when recording stops (the window ends, the game closes, a savestate loads) is written as
*truncated*, with the state at that moment. Don't replay abandoned or truncated calls as if they returned.

Limits:

- **Interrupt handlers and traps** return by `RTE`, not to the long at A7 or to PR: record one and it is abandoned,
  usually at the first watched instruction after its `RTE`, or truncated. The exit state is not the one at `RTE`.
- **Routines that never return** (main loops, routines that pop their return address and jump away) are abandoned
  when the stack pointer rises past their entry value, or truncated.
- **Tail calls.** A routine that ends by jumping to another returns when that one returns, and its exit state is
  taken there. If the routine jumped to is recorded too, it starts with the same return address and stack pointer,
  and both return together.
- **SH-2 routines not entered by `BSR`/`BSRF`/`JSR`** (a `BRA`, a `JMP`, falling into them) find in PR whatever
  was there. They return correctly only if that was the caller's own return and R15 matches (a tail call after the
  caller restored PR and R15); otherwise they end abandoned or truncated.
- **Abandonment is seen late:** the recorder looks only at the instructions it watches (entries and return
  addresses, or every instruction while discover runs on that CPU), so an abandoned call's exit state is from the
  first of those after the stack pointer rose, not from the moment it did.
- **Stacks:** code that switches stacks (tasks with a stack each) or moves its return address defeats the stack
  pointer checks.
- **Order:** each file has its calls in the order they ended, so a nested call comes before the call around it;
  the sequence number counts entries across all routines. Across CPUs, entries are in PicoDrive's slice order (see
  *Limits*).

### Discover

For the first N frames of the window, the recorder watches every instruction of that CPU and counts call targets:

- **68000:** after a `JSR` or `BSR`, the next instruction executed is the target if A7 dropped by 4 and the long at
  A7 is the address after the call. An interrupt taken right after the call hides that call.
- **SH-2:** after a `BSR`, `BSRF` or `JSR` and its delay slot, the next instruction is the target if PR is the
  call's address + 4.

`discover-<cpu>.txt` lists the targets, busiest first: the target's address, the calls, and the number of distinct
call sites (the address of the call instruction). `JMP` and `BRA` are not counted. Calls that straddle the edges of
the discover frames may or may not count.

### Output

The output folder gets one `<name>.pdcr` per routine, `index.json`, and `discover-<cpu>.txt` per discover line.
Writes are buffered; the files are complete once recording stops.

A `.pdcr` file is little-endian throughout (`str` is a `u16` byte count, then UTF-8):

| part | fields |
|---|---|
| header | `"PDCR"`, `u32` version (1), `u32` cpu (0 = 68000, 1 = master SH-2, 2 = slave SH-2), `u32` address, `str` name, `u32` register count, `u32` region count, then per region: `str` domain, `u32` start, `u32` length, `str` label |
| record | `u32` frame (at entry), `u32` sequence number, `u32` flags (1 returned, 2 abandoned, 4 truncated), the registers at entry, the registers at exit (`u32` each), the regions' bytes at entry, the regions' bytes at exit |

The registers are in the order of `emu.getregisters`' names (see *Registers*): D0-D7, A0-A7, PC, SR, USP, SSP (20)
for the 68000; R0-R15, PC, PR, SR, GBR, VBR, MACH, MACL (23) for an SH-2. The entry PC is the routine's address; a
returned call's exit PC is its return address. Region bytes are as the domain shows them, which for RAM is the CPUs'
big-endian order, region after region. Every record has the same size, `12 + 8 * registers + 2 * region bytes`, so
the count is the file's size less the header, divided by it. All of 68K RAM is 128 KB per call: mind `maxcalls`.

`index.json` has, per routine, its name, file, cpu, address, regions, record size, the calls written and how many
returned, were abandoned or truncated, whether it stopped at `maxcalls`, and the frames of its first and last call;
per discover line, the frames and the instructions, calls and targets counted; and whether recording failed
(a write error, which also shows an on-screen message).

`pdcr.py` (Python 3) reads them:
```
python pdcr.py summary calls                   # index.json, every routine, the discover files
python pdcr.py calls calls/my_routine.pdcr -n 50
python pdcr.py show calls/my_routine.pdcr 12   # call 12: registers at entry and exit, the region bytes that changed
python pdcr.py show calls/my_routine.pdcr s345 # the call with sequence number 345
```
As a module, `pdcr.read(path)` returns the routine, whose `calls()` yields each call's registers and bytes.

### With Lua

Each CPU's watch list is the union of the recorder's addresses and those of the execute callbacks, and each hit
goes to the recorder, to `MemoryCallbacks`, or both: the callbacks get exactly the hits they get without the
recorder (an address-less callback doesn't see the recorder's addresses), and the recorder looks only at its own.

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
- **Call recorder** (`CallRecorder.cs`, `PicoDrive.CallRecorder.cs`): created by the `PicoDrive` constructor when the
  variable is set, it starts and stops at frame boundaries (`FrameAdvancePrep`/`FrameAdvancePost`), writes the pending
  calls as truncated on a state load and when the core is disposed, and gives `RefreshExecWatchList` its addresses: the
  routines still recording and every return address seen, which stay watched until recording stops, so the list
  seldom changes. It reads regions straight from the core's memory areas, undoing their byte swap, because the
  domains of swapped memory read a byte at a time.

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
  one frame each counted 11,607 68000, 198,266 master and 166,264 slave instructions;
- the 32X domains: loaded as EmuHawk loads it, the 32X test ROM has `32X RAM` and `32X FB` and the Mega Drive one
  doesn't; per-frame video, audio and 68K RAM hashes, 300 frames, are identical to the 32X memory allocated late (as
  2.11.1 did), on both ROMs.

**Call recorder** (`CallRecorderTests`, same run), all pass:
- the five test routines on all three CPUs, frames 20-69: 5,417 calls of `sub68`, 3,788 each of `sub1`, `sub2` and
  `sub3` and 5,448 of `ssub`, each as many as its counter in RAM went up, all returned; 292,779 checks of the caller's
  registers at entry, the routine's at exit, the exit PC (the return address), R15 or A7 back to their entry value,
  and the counter one higher at exit than at entry in the recorded bytes (including a region at an odd address);
- on the Mega Drive ROM, `sub_a` (JSR, supervisor mode) and `sub_b` (BSR, user mode, A7 = USP) all returned, and the
  TRAP handler, which returns by RTE, all abandoned: 63,495 checks;
- discover: every test routine found with its count within 2 of its counter, one call site each, over 30 frames on all
  three CPUs at once and on the Mega Drive ROM;
- recording three routines (4 KB and 1 KB regions) and discovering on the master changes no per-frame hash in 200
  frames; the routines stopped at `maxcalls`;
- execute callbacks, including an address-less one on the 68000 alone: the same 363,742 hits in 30 frames with and
  without the recorder watching all three CPUs (one of them in discover mode);
- a config with six kinds of mistakes, and a missing config file: the recorder is off with a message naming each
  line, and the game runs;
- nesting, recursion, an outer call returning first, the stack pointer rising, a tail call, `maxcalls`, a truncated
  call, and 68000 calls in user and supervisor mode, against a made-up CPU.

Knuckles' Chaotix (your cartridge, no input, `RecorderSmoke`, output kept outside the repository):
- discover over frames 0-599 on the 68000: 8,663 calls to 69 targets in 4.8 million instructions; over frames
  540-599 on the master SH-2: 5,103 calls to 8 targets in 5.5 million instructions;
- recording frames 300-599, the three busiest 68000 targets with all of 68K RAM and the busiest master target with
  256 bytes of SDRAM (`maxcalls 100000`): 268 calls each of the 68000 routines and 11,000 of the SH-2 one (between
  frames 448 and 596), every one returned; 35.2 MB per 68000 routine (131,244 bytes a call), 7.8 MB for the SH-2
  one (708 bytes a call). With the default `maxcalls` of 1000, the SH-2 routine would have stopped after about 14
  frames.

**Not tested:** EmuHawk and its tools (Lua Console, Debugger, trace logger), Windows, the Lua script itself, BIOS
mode (real 32X BIOS files, needed for movies), PAL timing, Sega CD and 32X CD games, a gcc-built waterbox core (the
shipped one is clang; the harness covered gcc). For the call recorder: `record.bat`, the movie workflow, EmuHawk's
messages (the tests capture them from `CoreComm`), recording across savestate loads, and .NET Framework.

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

**Call recorder**, full stack, 32X test ROM (`RecorderSpeed`), median fps of 5 runs, in two runs of the test:

| configuration | fps | calls per frame | written |
|---|---|---|---|
| recorder off (variable unset) | 527-538 | | |
| 5 routines on the three CPUs, 32 bytes each | 321-369 | 444 | 170 MB in 1,510 frames |
| `sub68` with all 64 KB of 68K RAM | 16-101 | 106 | 485 MB in 35 frames |
| discover on the master | 191-204 | | |

Unset, the recorder is not created, and the speed is that of the core with no callbacks (533 fps in the same run).
With small regions, a recorded call costs about 2 microseconds, besides the watches themselves (see above); big
regions are copied and written twice a call: with all of 68K RAM, 131 KB a call, the disk is the limit (the 16 fps
is the page cache flushing, the 101 fps what copying costs), so keep `maxcalls` low for big regions.

Knuckles' Chaotix, 600 frames from power-on, fps without and with the recorder (the same run each):

| configuration | off | on |
|---|---|---|
| discover on the 68000, frames 0-599 | 470 | 431 |
| discover on the master SH-2, frames 540-599 | 483 | 422 |
| 3 routines of the 68000 with 64 KB of 68K RAM, 1 of the master with 256 bytes, frames 300-599 | 478 | 413 |

These are .NET 8 on Linux; EmuHawk on Windows runs .NET Framework 4.8, which wasn't measured.
