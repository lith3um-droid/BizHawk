# Follow-up brief: deeper 32X tooling for the Chaotix teardown

For a later Claude Code session, cloud or local, working in the user's fork
[`lith3um-droid/bizhawk`](https://github.com/lith3um-droid/bizhawk), branch
`32x-hooks`. Written 2026-09-27 at the end of the session that built that
branch. You have no access to that conversation; everything you need is here,
in `docs/32x-hooks-brief.md` (the first brief: its **Hard rules** still apply)
and in `docs/32x-hooks.md` (the reference for what exists).

**First steps:**
1. Check out `32x-hooks` and read `docs/32x-hooks.md` and
   `docs/32x-hooks-brief.md`.
2. Rebuild the environment (see *Rebuilding*) and run both test suites.
   Confirm they pass before changing anything.
3. Work on a new branch from `32x-hooks`, for example `32x-hooks-2`, and
   push it to the fork as you go.

## Why

The user is porting Knuckles' Chaotix to Godot by transcribing the original
code routine by routine, checking each routine against recordings made in
BizHawk. That worked for two Master System and Game Gear games.

For Chaotix:
- **The 68000 comes first.** It runs the game logic. Its routines are
  recorded at entry and return and replayed against the port.
- **The two SH-2s are treated as black boxes at first**, checked by input and
  output rather than transcribed:
  - the master draws scaled sprites, so feed it the 68000's draw list and
    compare the framebuffer;
  - the slave mixes PWM audio, so compare the sound.

The first branch made routine-level recording possible. This brief is about
making it precise and making the SH-2 black boxes observable.

## State at handoff (branch `32x-hooks`, based on tag `2.11.1`)

- **Native execute hooks**, for the 68000 and both SH-2s:
  - Files: `waterbox/picodrive/`: `bizhawk.c`, `bizhawk_hooks.h`,
    `cpu/fame/famec.c`, `cpu/sh2/sh2.h`, `cpu/sh2/mame/sh2pico_bizhook.c`.
  - Exports: `SetExecCallback(cb(cpu, addr, opcode))`,
    `SetExecWatchList(cpu, addrs, count, watchAll)` and
    `GetRegisters(cpu, out)`. CPU 0 is the 68000, 1 the master SH-2, 2 the
    slave.
  - The hook state is `ECL_INVISIBLE`, so savestates leave it out.
- **The native harness and test ROMs**, written from source, are in
  `waterbox/picodrive/hooktest/`: `asm.py`, `mkroms.py`, `harness.c`,
  `run_tests.py` and `smoke_32x.py`.
- **C#:**
  - `PicoDrive.IDebuggable.cs` provides the scopes "M68K BUS", "SH2 Master"
    and "SH2 Slave", and 66 registers.
  - `LibPicoDriveHooks` is bound only if the core exports it, so the new DLL
    also runs the stock core.
- **Headless tests:** `src/BizHawk.Tests.PicoDriveHooks/`, run with
  `run_tests.sh`. `PICODRIVE_BASELINE_WBX` points at an unmodified core, and
  the opt-in `BIZHAWK_32X_SMOKE_ROM` names a real ROM.
- **Deliverables:** `Dist/32x-hooks/` holds `picodrive.wbx.zst`,
  `BizHawk.Emulation.Cores.dll`, `SHA256SUMS` and `smoke_32x.lua`. It is
  capital `Dist` on purpose: `dist/` would collide with the existing folder
  on Windows.
- **The call recorder** (`CallRecorder.cs`, `PicoDrive.CallRecorder.cs`):
  - **Turned on** by `PICODRIVE_CALL_RECORDER=<config file>`. Unset, it
    isn't created and costs nothing.
  - **Config statements:** `routine`, `region`, `frames`, `maxcalls`
    (default 1000), `discover` and `out`.
  - **What it records:** registers and regions at entry and return, with
    returns detected automatically. That is the long at A7 plus A7 + 4 on the
    68000, and PR plus R15 on the SH-2.
  - **Bookkeeping:** a stack of up to 256 pending calls per CPU, with each
    call flagged returned, abandoned or truncated.
  - **Output:** `.pdcr` files plus `index.json`, and `discover-<cpu>.txt`.
  - **Tools:** `Dist/32x-hooks/pdcr.py` reads the output;
    `Dist/32x-hooks/record.bat` and `recorder-example.txt` show how to use it
    in EmuHawk.
  - **Coexistence with Lua:** each CPU's native list is the union of the
    recorder's addresses and Lua's, so Lua gets exactly the hits it got
    before.
  - **Tests:** they reach the recorder's internals through an
    `InternalsVisibleTo` line in `BizHawk.Emulation.Cores.csproj`.
  - **Docs:** `docs/32x-hooks.md`, *Call recorder*.
- **The "32X RAM" and "32X FB" memory domains** now exist for 32X
  cartridges; stock 2.11.1 doesn't expose them. Per-frame video, audio and
  RAM hashes are unchanged by this.

## What to build, in priority order

Each item is useful on its own. Stop at any boundary with the tests green
and the docs updated.

**P1: read and write callbacks, and exact outputs per call.**
- **Callbacks:** read and write on all three scopes, filtered in the core as
  the execute hooks are: a per-CPU flag, a prefilter, then exact lists or a
  watch-all flag.
  - SH-2 instruction fetches are never reported as reads.
  - Lua's `event.on_bus_read`/`on_bus_write` then work on this core. Today
    they register without error but never fire.
- **Recorder "writes" mode** (extends `CallRecorder.cs`): for a recorded call, log every write the
  routine makes (address, size, value, CPU, and whether it happened inside
  an interrupt handler). The port can then be checked against exactly what
  the routine wrote, instead of a RAM diff that also catches interrupt
  writes.

**P2: make the SH-2 side observable.** New memory domains that read the
emulator's own arrays, never through bus handlers, so reading has no side
effects:
- `SH2M Cache` and `SH2S Cache`: each SH-2's 4 KB `data_array`. The slave
  mixes audio in its cache used as RAM;
- the 32X palette;
- the 32X system registers: 68000 side `$A15100-$A1517F` and SH-2 side
  `0x20004000-`, including the communication ports and PWM.

Say in the docs which are writable.

**P3: capture for the black boxes.** New recorder directives:
- `snapshot <cpu> <addr> <regions…>`: capture regions (and registers) every
  time an address executes, with no return tracking. Use it for the draw
  list at the point the SH-2 picks it up.
- `frame <regions…>`: capture at the end of every frame. Use it for the
  finished framebuffer, the palette and the comm ports.
- `audio`: dump each frame's audio output as raw PCM, with its length.

Update `pdcr.py` to read them.

**P4: instruction traces for chosen calls.**
- `trace <cpu> <addr>` records, for each call of that routine, every
  instruction executed until it returns: PC, opcode and registers after
  each, and interrupt entries marked. Watch-all runs only while inside the
  call.
- This is for diffing the port against the original when a probe fails.
  Consider also implementing `ITraceable`, if it adds little.

**P5: a headless batch runner.**
- A command-line tool that loads the ROM, the user's own firmware folder
  (32X BIOS files, needed for deterministic movies) and a `.bk2` movie.
- It plays the movie without EmuHawk, runs a recorder config, and writes the
  outputs.
- It must use the movie's sync settings and prove determinism, for example
  by matching a hash that EmuHawk logged at the end of the movie.
- This can't be tested in the cloud with BIOS files (hard rule 1). Test it
  with homebrew in non-deterministic mode, and leave the BIOS check to the
  user or a local session.

**P6, small items:**
- `TotalExecutedCycles` per CPU;
- execute callbacks for the Z80 (CZ80);
- a per-slice test for the 68000 hook to remove its idle cost. It costs
  about 2% on Mega Drive-only work with LTO, but a 68000 hook enabled from
  an SH-2 callback would then start up to a scanline late. Measure and
  decide.

## Hard rules

All the rules in `docs/32x-hooks-brief.md` apply, in particular:
- No ROMs, BIOS files or game data in the repo, ever. Committed tests use
  only programs written from source; extend `mkroms.py`.
  - The user may upload their own Chaotix ROM for a local smoke test. Keep it
    in scratch space, never commit it or anything derived from it, and don't
    print ROM bytes.
- Stay inside the user's fork. Nothing on TASEmulators repositories unless
  the user says so.
- Stay on tag `2.11.1` and don't change `BizHawk.Emulation.Common` or other
  shared interfaces. Everything must work by swapping the same two files
  into a copy of BizHawk 2.11.1.
- PicoDrive's licence is non-commercial: treat the result as a private
  research instrument, and leave the licence files alone.
- Small commits with clear messages and a `Co-Authored-By` trailer naming
  your model.

## Verified facts (2026-09-27)

**Building and running**
- **Compilers:** the shipped 2.11.1 `picodrive.wbx` was built with Ubuntu
  clang 16 and LLD 16, so FAME/C runs in its `FAMEC_NO_GOTOS` mode. This
  branch builds with clang 18, and the harness covers both FAME modes.
- **Savestates don't move between different `.wbx` builds.** The waterbox
  refuses with "Bad hash for state", and any rebuild does the same. Within
  one build, loading a state keeps the current callbacks.
- **Test projects:**
  - `src/BizHawk.Tests.Testroms*` doesn't build at 2.11.1 (DummyFrontend vs
    `ICoreFileProvider`). Don't depend on it.
  - `BizHawk.Tests.PicoDriveHooks` stands alone.

**BizHawk behaviour at 2.11.1**
- In `MemoryCallbackSystem`, an address-less callback fires for every scope.
  Enumerating the callbacks inside `CallbackAdded`/`CallbackRemoved` throws,
  so track them from the events.
- `PicoDrive.cs` only preallocated 32X memory when the gamedb option `32X`
  was set, and no entry has it. This branch also passes the flag for
  `VSystemID.Raw.Sega32X`.

**How the hooks work**
- **SH-2 hooks cost nothing when idle:** `sh2_execute()` picks between the
  unchanged interpreter and a hooked copy once per time slice.
- **The 68000 hook** tests one flag per instruction.
- **Speed through the full stack**, 32X test ROM, frames per second:

  | Configuration | fps |
  |---|---|
  | Unmodified | 512 |
  | Hooked, idle | 531 |
  | 4 watched addresses | about 465 |
  | Address-less on all three CPUs | 83 |

- **Order and skipped iterations:**
  - PicoDrive runs the CPUs in slices, so callbacks across CPUs arrive in
    slice order.
  - It syncs the SH-2s inside 32X register accesses, so an SH-2 callback can
    run while the 68000 is mid-instruction.
  - Its poll detection puts SH-2s that spin on the comm ports or SDRAM to
    sleep, skipping loop iterations.
  - MAME's busy-loop hacks never fired in testing.
- **SH-2 addresses:** cached (`0x0…`) and cache-through (`0x2…`) are
  distinct addresses. Delay-slot instructions report their own address, and
  PR is already set by then.
- **SH-2 cache:** the SH7604's cache is write-through, so SDRAM stays current
  for ordinary writes. Data kept only in the cache used as RAM
  (`data_array`) is invisible until P2.

**Chaotix** (measured on the user's ROM; the ROM itself never left scratch
space)
- **From the 32X header:** the master starts at `0x060001A0` and the slave
  at `0x060001A4`. Both are first hit in frame 5, with VBR `0x06000000` and
  `0x06000080` and stacks at `0x06040000` and `0x0603F800`.
- **Instructions per frame:** 68000 about 11,600; master 58,000 to 198,000;
  slave a steady 166,000.
- **From the recompilations' docs:** the SH-2 program sits at ROM
  `0x077800` (36 KB), with a 1 KB slave overlay at `0x07FC00`.
- **Recorder smoke, with no input** (title screen and attract mode):
  - discovery found 8,663 68000 calls to 69 targets over frames 0-599, and
    5,103 master SH-2 calls to 8 targets over frames 540-599;
  - recording the three busiest 68000 routines with all of 68K RAM, and the
    busiest master routine with a small SDRAM region, captured 100% of the
    returns;
  - capturing all 64 KB of 68K RAM per call is limited by disk writes, so
    prefer small regions, or P1's writes mode.

**Recorder speed** (32X test ROM):
- off: the same as no callbacks;
- 5 routines with 32-byte regions: about 2 µs per call;
- discover mode on an SH-2: about 200 fps.

## Rebuilding (Ubuntu 24.04; worked 2026-09-27, with no network blocks)

```sh
git submodule update --init --depth 1 waterbox/musl
sudo apt-get install -y zstd binutils-m68k-linux-gnu binutils-sh4-linux-gnu libclang-rt-18-dev
# clang here ships without compiler-rt; the waterbox link needs it in the sysroot
mkdir -p waterbox/sysroot/lib/linux
ln -sf /usr/lib/llvm-18/lib/clang/18/lib/linux/libclang_rt.builtins-x86_64.a waterbox/sysroot/lib/linux/
(cd waterbox/musl && ./wbox_configure.sh && ./wbox_build.sh)
(cd waterbox/emulibc && make) && (cd waterbox/libco && make)
(cd waterbox/picodrive && make install -j4)   # rewrites Assets/dll/picodrive.wbx.zst
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet
export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH
dotnet build src/BizHawk.Emulation.Cores/BizHawk.Emulation.Cores.csproj -c Release -m \
  -p:Version=2.11.1 -p:SourceRevisionId=$(git rev-parse HEAD)
python3 waterbox/picodrive/hooktest/run_tests.py --gcc --pristine <2.11.1 checkout>/waterbox/picodrive --bench
PICODRIVE_BASELINE_WBX=<unmodified picodrive.wbx.zst> src/BizHawk.Tests.PicoDriveHooks/run_tests.sh
```

For an unmodified core to compare against, build `waterbox/picodrive` from
a checkout of tag `2.11.1` with the same toolchain.

## Testing

As before, and as `docs/32x-hooks.md` describes:
- **Native harness:** checks in both FAME modes, and per-frame
  video/audio/RAM hashes against the unmodified core with nothing watched.
- **Headless C# tests:** through the real waterbox and `IDebuggable`.
- **Extend `mkroms.py` with known answers:**
  - P1: routines that read and write known addresses, including
    unaligned, byte and long accesses and SH-2 cache-through aliases;
  - P2: known values in the cache used as RAM and in the comm ports;
  - P3: a known per-frame draw-list pattern and framebuffer.
- **Speed:** report idle, few-watches and watch-all numbers for every new
  hook, next to the table above.

## Deliverables

- **Source:** everything on the new branch, tests included.
- **Binaries:** a rebuilt `Dist/32x-hooks/` (both binaries and
  `SHA256SUMS`), with `pdcr.py` updated for any new record types.
- **Docs:** `docs/32x-hooks.md` updated with the new scopes, domains,
  directives, limits, what was tested and the speed numbers.
- **A final report:** what works, the measured overhead, what's untested,
  and open questions.

## Open decisions, for the user if they matter

- **Default `maxcalls`:** it is 1000. A busy Chaotix SH-2 routine hits that
  in about 14 frames, so set it per config or raise the default.
- **Restarting the core** (loading the game, starting a movie, Reboot Core)
  overwrites the recorder's output folder. Would timestamped subfolders be
  safer?
- **A savestate load** (rewind, TAStudio) truncates pending calls, and
  recording carries on. Should it stop instead?

- **Optional binding:** keep `LibPicoDriveHooks` bound only if exported, or
  bind strictly if this is ever offered upstream to issue #2234?
- **CPU in the flags:** encoding the CPU in the flags of address-less
  callbacks was considered and rejected, to keep GPGX's convention. Revisit
  only if P1 needs it.

## References

- The first brief: `docs/32x-hooks-brief.md`.
- The reference: `docs/32x-hooks.md`.
- BizHawk issues [#2234](https://github.com/TASEmulators/BizHawk/issues/2234)
  (debugging for 32X) and
  [#2898](https://github.com/TASEmulators/BizHawk/issues/2898) (PicoDrive
  update, out of scope).
- The 32X Hardware Manual, the Hitachi SH7604 manual, and the
  [32XDK wiki](https://github.com/viciious/32XDK).
