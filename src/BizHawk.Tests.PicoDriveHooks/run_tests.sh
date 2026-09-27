#!/bin/sh
# Runs the PicoDrive execute hook tests headless, on Linux; see readme.md.
# Extra arguments go to `dotnet test`, e.g. --filter Hits32X
set -e
here="$(dirname "$(realpath "$0")")"
root="$(realpath "$here/../..")"
work="$root/test_output/picodrive-hooks"
mkdir -p "$work/dll" "$work/roms"
# the dll folder the core loads from: $BIZHAWK_HOME/dll, and libwaterboxhost.so through LD_LIBRARY_PATH;
# the tests copy each picodrive.wbx.zst they load into it
cp "$root/Assets/dll/libwaterboxhost.so" "$work/dll/"
python3 "$root/waterbox/picodrive/hooktest/mkroms.py" "$work/roms"
export BIZHAWK_HOME="$work"
export LD_LIBRARY_PATH="$work/dll${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export PICODRIVE_HOOKS_ROMS="$work/roms"
export PICODRIVE_HOOKS_WBX="${PICODRIVE_HOOKS_WBX:-$root/Assets/dll/picodrive.wbx.zst}"
# the core BizHawk 2.11.1 ships, for the savestate test
if [ -z "$PICODRIVE_STOCK_WBX" ] && git -C "$root" cat-file -e 2.11.1:Assets/dll/picodrive.wbx.zst 2>/dev/null; then
	git -C "$root" show 2.11.1:Assets/dll/picodrive.wbx.zst > "$work/stock-picodrive.wbx.zst"
	export PICODRIVE_STOCK_WBX="$work/stock-picodrive.wbx.zst"
fi
# the native harness's instruction counts, if waterbox/picodrive/hooktest/run_tests.py has built it
harness="$root/waterbox/picodrive/hooktest/build/clang/harness"
if [ -z "$PICODRIVE_HOOKS_NATIVE_COUNTS" ] && [ -x "$harness" ]; then
	"$harness" -32x -f 60 -a 0 -a 1 -a 2 -counts "$work/native-counts.txt" "$work/roms/32x_hooks.32x" >/dev/null
	export PICODRIVE_HOOKS_NATIVE_COUNTS="$work/native-counts.txt"
fi
cd "$here"
exec dotnet test -c Release -p:TestProjTargetFrameworkOverride=net8.0 -l "console;verbosity=detailed" "$@"
