# PicoDrive execute hook tests

Headless tests of the 32X core's execute callbacks and registers (see `docs/32x-hooks.md`), through the real stack:
`PicoDrive`, `WaterboxCore`, the waterbox host and a real `picodrive.wbx.zst`, driven only through `IDebuggable`
(`MemoryCallbacks.Add(...)`, `GetCpuFlagsAndRegisters()`), `IMemoryDomains` and `IStatable`.
Like the other core test suites, this project isn't in the main solution.

The ROMs are the ones `waterbox/picodrive/hooktest/mkroms.py` builds from source; no ROM is committed.
The tests check what that folder's `run_tests.py` checks natively.

## Running (Linux)

Needs the .NET 8 SDK, Python 3 and `libzstd.so.1`.
```sh
src/BizHawk.Tests.PicoDriveHooks/run_tests.sh                        # everything
src/BizHawk.Tests.PicoDriveHooks/run_tests.sh --filter Hits32X      # extra arguments go to dotnet test
```
`run_tests.sh`:
- builds the test ROMs into `test_output/picodrive-hooks/roms`;
- makes `test_output/picodrive-hooks/dll` with `libwaterboxhost.so`, and points `BIZHAWK_HOME` at its parent and
  `LD_LIBRARY_PATH` at it; each test copies the core it loads into that folder;
- runs `dotnet test -c Release -p:TestProjTargetFrameworkOverride=net8.0`.

Environment variables:

| variable | |
|---|---|
| `PICODRIVE_HOOKS_WBX` | the core under test; default `Assets/dll/picodrive.wbx.zst` |
| `PICODRIVE_BASELINE_WBX` | an unmodified core built with the same toolchain (`make install` in `waterbox/picodrive` at tag `2.11.1`); without it the regression test is inconclusive and the speed test skips the baseline |
| `PICODRIVE_STOCK_WBX` | the core 2.11.1 ships; `run_tests.sh` takes it from the tag |
| `PICODRIVE_HOOKS_NATIVE_COUNTS` | the native harness's per-frame instruction counts; `run_tests.sh` makes them if `hooktest/run_tests.py` has built the harness |
| `BIZHAWK_32X_SMOKE_ROM` | a 32X cartridge of your own, for the opt-in smoke test; nothing from it is written anywhere |

## The tests

- `ExecHookTests`: register names; every hit on both test ROMs with the caller's and the routine's registers, `PR`,
  delay slots, `MACH`/`MACL`, USP/SSP, the opcode as the value, the counts per frame against the routines' own
  counters in RAM, and aliases that must not fire; address-less callbacks counting a frame's instructions per CPU;
  removing callbacks; a savestate round trip.
- `BaselineTests`: per-frame hashes of video, audio and all writable memory against the baseline core, and with every
  instruction watched; the 32X memory domains of a cartridge loaded as EmuHawk loads it; frames per second; savestates
  across cores.
- `CallRecorderTests`: the call recorder (`PICODRIVE_CALL_RECORDER`, see `docs/32x-hooks.md`), which the tests turn on
  with a config file of their own in `test_output/picodrive-hooks/recorder/`: calls of the routines on all three CPUs
  against the routines' counters in RAM, with the caller's and the routine's registers, the return address and the
  counter's increment in the recorded memory; discover mode; emulation and the execute callbacks unchanged while
  recording; config errors; frames per second; and the bookkeeping of nested, recursive, abandoned and tail calls
  against a made-up CPU.
- `SmokeTest`: what `Dist/32x-hooks/smoke_32x.lua` does, on the cartridge `BIZHAWK_32X_SMOKE_ROM` names; and
  `RecorderSmoke`, the call recorder on that cartridge with the config file `PICODRIVE_RECORDER_SMOKE_CONFIG` names, for
  `PICODRIVE_RECORDER_SMOKE_FRAMES` frames (600 by default), with the frames per second with and without it. Keep that
  config file and its output outside the repository.
