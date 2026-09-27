-- smoke_32x.lua: a smoke check of the 32X execute callbacks and registers in the
-- 32x-hooks build of BizHawk's PicoDrive core (docs/32x-hooks.md in the fork).
--
-- Run it in a COPY of BizHawk 2.11.1 whose dll folder has this folder's
-- picodrive.wbx.zst and BizHawk.Emulation.Cores.dll. Load your 32X cartridge
-- with the PicoDrive core, reboot the core (Emulation > Reboot Core), pause, and
-- start this script from the Lua Console before frame 5, then unpause: the SH-2s
-- pass their entry points only once, just after power-on. Output goes to the
-- Lua Console.
--
-- Only BizHawk 2.11.1's own Lua API is used:
--  1. memory.read_u32_be(0x3E0 and 0x3E4, "MD CART"): the master and slave SH-2
--     entry points in the cartridge's 32X header, big-endian longs;
--  2. event.on_bus_exec(fn, address, name, scope) there, on the scopes
--     "SH2 Master" and "SH2 Slave", printing the first hits with
--     emu.getregisters() for FRAMES frames. The callback gets (addr, val, flags):
--     the address as executed, the instruction's first word, and the flags;
--  3. event.on_bus_exec_any(fn, name, scope), the address-less execute callback,
--     counting each CPU's instructions for one frame. In 2.11.1 the callback
--     system calls an address-less callback for every scope, not only its own,
--     so with all three registered each would count all three CPUs. So the
--     script counts one scope per frame, then shows the effect with one frame of
--     all three.
--
-- This script was NOT run: EmuHawk can't run where it was written. The headless
-- test src/BizHawk.Tests.PicoDriveHooks/SmokeTest.cs does the same steps through
-- the same core interfaces the Lua functions call, and passed on a 32X cartridge.

local FRAMES = 300     -- frames to watch the entry points for
local MAX_PRINTED = 10 -- hits printed per entry point
local SCOPES = { "M68K BUS", "SH2 Master", "SH2 Slave" }

local function has_scope(name)
	for _, scope in pairs(event.availableScopes()) do
		if scope == name then
			return true
		end
	end
	return false
end

if not (has_scope("SH2 Master") and has_scope("SH2 Slave")) then
	console.log("No \"SH2 Master\" and \"SH2 Slave\" scopes: the core is not PicoDrive with the 32x-hooks files.")
	return
end
if emu.getsystemid() ~= "32X" then
	console.log("The system is " .. emu.getsystemid() .. ", not 32X: the SH-2s may never run.")
end
if emu.framecount() >= 5 then
	console.log(string.format("Frame %d: the entry points were probably passed already; reboot the core and start this script first to see those hits.", emu.framecount()))
end

-- 1. the entry points
local master = memory.read_u32_be(0x3E0, "MD CART")
local slave = memory.read_u32_be(0x3E4, "MD CART")
console.log(string.format("32X header: master SH-2 entry %08X, slave SH-2 entry %08X", master, slave))

-- 2. execute callbacks at the entry points
local hits = { master = 0, slave = 0 }
local function show(label, prefix, addr, val)
	hits[label] = hits[label] + 1
	if hits[label] > MAX_PRINTED then
		return
	end
	local r = emu.getregisters()
	console.log(string.format("frame %d %-6s: addr %08X op %04X PC %08X PR %08X SR %03X GBR %08X VBR %08X R15 %08X",
		emu.framecount(), label, addr, val, r[prefix .. " PC"], r[prefix .. " PR"], r[prefix .. " SR"],
		r[prefix .. " GBR"], r[prefix .. " VBR"], r[prefix .. " R15"]))
end

local ids = {
	event.on_bus_exec(function(addr, val, flags) show("master", "SH2M", addr, val) end, master, "smoke_32x master entry", "SH2 Master"),
	event.on_bus_exec(function(addr, val, flags) show("slave", "SH2S", addr, val) end, slave, "smoke_32x slave entry", "SH2 Slave"),
}
for _ = 1, FRAMES do
	emu.frameadvance()
end
for _, id in ipairs(ids) do
	event.unregisterbyid(id)
end
console.log(string.format("%d master and %d slave hits at the entry points in %d frames", hits.master, hits.slave, FRAMES))

-- 3. each CPU's instructions in one frame, one scope per frame
for _, scope in ipairs(SCOPES) do
	local n = 0
	local id = event.on_bus_exec_any(function() n = n + 1 end, "smoke_32x count", scope)
	emu.frameadvance()
	event.unregisterbyid(id)
	console.log(string.format("frame %d: %-10s %8d instructions", emu.framecount() - 1, scope, n))
end

-- and all three at once: each callback then counts every CPU
local all, all_ids = { 0, 0, 0 }, {}
for i, scope in ipairs(SCOPES) do
	all_ids[i] = event.on_bus_exec_any(function() all[i] = all[i] + 1 end, "smoke_32x count all", scope)
end
emu.frameadvance()
for _, id in ipairs(all_ids) do
	event.unregisterbyid(id)
end
console.log(string.format("frame %d: all three scopes at once, each callback counted %d %d %d",
	emu.framecount() - 1, all[1], all[2], all[3]))
