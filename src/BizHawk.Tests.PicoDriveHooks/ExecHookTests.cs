using System.Collections.Generic;
using System.IO;
using System.Linq;

using BizHawk.Emulation.Common;

namespace BizHawk.Tests.PicoDriveHooks
{
	/// <summary>
	/// Execute callbacks and registers through <see cref="IDebuggable"/>, on the test ROMs of
	/// <c>waterbox/picodrive/hooktest</c>, with the same checks as its <c>run_tests.py</c>.
	/// </summary>
	[TestClass]
	public sealed class ExecHookTests
	{
		private const int FRAMES = 90;

		private const uint EXECUTE = (uint)MemoryCallbackFlags.AccessExecute;

		/// <summary>one callback: what it got, the registers inside it, the long at A7, and the variables in RAM</summary>
		private sealed record class Hit(int Frame, string Name, string Scope, uint Addr, uint Value, uint Flags,
			IDictionary<string, RegisterValue> Regs, uint Stack, IReadOnlyDictionary<string, uint> Vars);

		private static Hit Record(Rig rig, RomInfo info, string name, string scope, uint addr, uint value, uint flags)
		{
			var regs = rig.Debuggable.GetCpuFlagsAndRegisters();
			var vars = info.Vars.ToDictionary(static kv => kv.Key, kv => rig.PeekGuest(kv.Value));
			var stack = scope is Rig.M68K ? rig.PeekGuest((uint)regs["M68K A7"].Value) : 0;
			return new(rig.Core.Frame, name, scope, addr, value, flags, regs, stack, vars);
		}

		private static string Prefix(string scope) => scope switch
		{
			Rig.M68K => "M68K ",
			Rig.Master => "SH2M ",
			_ => "SH2S ",
		};

		[TestMethod]
		public void RegisterNames()
		{
			var info = RomInfo.Read("md_hooks.bin");
			using var rig = Rig.Load(TestEnv.HookedCore, info.Path);
			rig.Frames(2);
			var regs = rig.Debuggable.GetCpuFlagsAndRegisters();
			string[] sh2 = [ .. Enumerable.Range(0, 16).Select(static i => $"R{i}"), "PC", "PR", "SR", "GBR", "VBR", "MACH", "MACL" ];
			string[] want =
			[
				.. Enumerable.Range(0, 8).Select(static i => $"M68K D{i}"),
				.. Enumerable.Range(0, 8).Select(static i => $"M68K A{i}"),
				"M68K PC", "M68K SR", "M68K USP", "M68K SSP",
				.. sh2.Select(static r => $"SH2M {r}"),
				.. sh2.Select(static r => $"SH2S {r}"),
			];
			CollectionAssert.AreEquivalent(want, regs.Keys.ToArray());
			Assert.AreEqual(16, regs["M68K SR"].BitSize);
			Assert.IsTrue(regs.Where(static kv => kv.Key is not "M68K SR").All(static kv => kv.Value.BitSize is 32));
			// no 32X on a Mega Drive cartridge: the SH-2s are zeros
			Assert.IsTrue(regs.Where(static kv => kv.Key.StartsWith("SH2")).All(static kv => kv.Value.Value is 0));
			CollectionAssert.AreEqual(Rig.Scopes, rig.Callbacks.AvailableScopes);
		}

		[TestMethod]
		public void Hits32X()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			var l = info.Labels;
			using var rig = Rig.Load(TestEnv.HookedCore, info.Path);
			var log = new List<Hit>();
			(string Scope, string[] Names)[] watches =
			[
				(Rig.M68K, [ "sub68", "ret68" ]),
				(Rig.Master, [ "sub1", "m_ret1", "sub2", "m_ret2", "m_call3", "m_slot3", "sub3", "m_ret3" ]),
				(Rig.Slave, [ "ssub", "s_ret" ]),
			];
			foreach (var (scope, names) in watches)
			{
				foreach (var name in names)
				{
					rig.AddExec(scope, l[name], name, (a, v, f) => log.Add(Record(rig, info, name, scope, a, v, f)));
				}
			}

			// never reported: the cache-through alias of sub1 on the master, and the master's sub1 on the slave
			var bogus = new List<string>();
			rig.AddExec(Rig.Master, l["sub1"] | 0x20000000, "sub1 alias", (a, _, _) => bogus.Add($"master {a:X8}"));
			rig.AddExec(Rig.Slave, l["sub1"], "master's sub1", (a, _, _) => bogus.Add($"slave {a:X8}"));

			var c = new Checker();
			(string Routine, string Counter)[] counters = [ ("sub68", "count68"), ("sub1", "cnt1"), ("sub2", "cnt2"), ("sub3", "cnt3"), ("ssub", "s_cnt") ];
			var perFrame = new List<int[]>();
			for (var frame = 0; frame < FRAMES; frame++)
			{
				var before = counters.Select(r => log.Count(h => h.Name == r.Routine)).ToArray();
				rig.Frame();
				var after = counters.Select(r => log.Count(h => h.Name == r.Routine)).ToArray();
				perFrame.Add([ .. after.Zip(before, static (a, b) => a - b) ]);
				// the routines count their calls: each entry's hits so far equal the count, or one more if a call hadn't counted yet
				for (var i = 0; i < counters.Length; i++)
				{
					var count = rig.PeekGuest(info.Vars[counters[i].Counter]);
					c.Ok($"frame {frame}: {counters[i].Routine} hit {after[i]} times, counted {count}", after[i] - count is 0 or 1);
				}
			}

			var mul = unchecked(0x11110001U * 0x11110002U);
			var dmul = unchecked(0x22220001UL * 0x22220002UL);
			var hits = new Dictionary<string, int>();
			foreach (var e in log)
			{
				var n = hits.TryGetValue(e.Name, out var k) ? k : 0;
				var w = $"{e.Name} #{n} (frame {e.Frame})";
				var v = e.Vars;
				ulong R(string reg) => e.Regs[Prefix(e.Scope) + reg].Value;
				c.Eq(w + " flags", e.Flags, EXECUTE);
				c.Eq(w + " address", e.Addr, l[e.Name]);
				c.Eq(w + " PC", R("PC"), e.Addr);
				if (e.Scope is not Rig.M68K)
				{
					c.Eq(w + " GBR", R("GBR"), 0x20004000);
					c.Eq(w + " VBR", R("VBR"), (uint)info.Json["vbr"]!);
					c.Eq(w + " SR", R("SR"), e.Scope is Rig.Master ? 0xF1U : 0xF0U);
					c.Eq(w + " R15", R("R15"), e.Scope is Rig.Master ? 0x06040000U : 0x0603F800U);
				}

				switch (e.Name)
				{
					case "sub68":
						c.Eq(w + " D0", R("D0"), 0x6800A001);
						c.Eq(w + " D7 = iteration", R("D7"), v["iter"]);
						c.Eq(w + " return address", e.Stack, l["ret68"]);
						c.Eq(w + " SR", R("SR") & 0xFF00, 0x2700);
						c.Eq(w + " count", v["count68"], (uint)n);
						break;
					case "ret68":
						c.Eq(w + " D1", R("D1"), 0x68000042);
						break;
					case "sub1":
						c.Eq(w + " PR", R("PR"), l["m_ret1"]);
						c.Eq(w + " R4", R("R4"), 0x11110001);
						c.Eq(w + " R5", R("R5"), 0x11110002);
						c.Eq(w + " R7 = iteration", R("R7"), v["m_iter"]);
						c.Eq(w + " count", v["cnt1"], (uint)n);
						break;
					case "m_ret1":
						c.Eq(w + " R9", R("R9"), 0xA1A1A1A1);
						c.Eq(w + " R10", R("R10"), 0xA1A1A1A2);
						c.Eq(w + " MACL", R("MACL"), mul);
						break;
					case "sub2":
						c.Eq(w + " PR", R("PR"), l["m_ret2"]);
						c.Eq(w + " R1 = JSR target", R("R1"), l["sub2"]);
						c.Eq(w + " R4", R("R4"), 0x22220001);
						c.Eq(w + " R5", R("R5"), 0x22220002);
						c.Eq(w + " R7 = iteration", R("R7"), v["m_iter"]);
						c.Eq(w + " count", v["cnt2"], (uint)n);
						break;
					case "m_ret2":
						c.Eq(w + " R9", R("R9"), 0xB2B2B2B1);
						c.Eq(w + " MACH:MACL", R("MACH") << 32 | R("MACL"), dmul);
						break;
					case "m_call3":
						c.Eq(w + " value = opcode (BSRF R2)", e.Value, 0x0203);
						c.Eq(w + " R2", R("R2"), unchecked(l["sub3"] - (l["m_call3"] + 4)));
						break;
					case "m_slot3":
						// the delay slot runs after BSRF set PR, before it jumps
						c.Eq(w + " value = opcode (MOV #0x33,R5)", e.Value, 0xE533);
						c.Eq(w + " PR", R("PR"), l["m_ret3"]);
						c.Eq(w + " R5 not yet set", R("R5"), 0x22220002);
						c.Eq(w + " follows BSRF", (ulong)hits.GetValueOrDefault("m_call3"), (ulong)n + 1);
						break;
					case "sub3":
						c.Eq(w + " PR", R("PR"), l["m_ret3"]);
						c.Eq(w + " R4", R("R4"), 0x33330001);
						c.Eq(w + " R5 from the delay slot", R("R5"), 0x33);
						c.Eq(w + " count", v["cnt3"], (uint)n);
						break;
					case "m_ret3":
						c.Eq(w + " R9", R("R9"), 0xC3C3C3C1);
						c.Eq(w + " R6 from the RTS delay slot", R("R6"), 0x44);
						break;
					case "ssub":
						c.Eq(w + " PR", R("PR"), l["s_ret"]);
						c.Eq(w + " R4", R("R4"), 0x55550001);
						c.Eq(w + " R5 from the delay slot", R("R5"), 0x5A);
						c.Eq(w + " R7 = iteration", R("R7"), v["s_iter"]);
						c.Eq(w + " count", v["s_cnt"], (uint)n);
						break;
					case "s_ret":
						c.Eq(w + " R9", R("R9"), 0x5555AAAA);
						break;
				}

				hits[e.Name] = n + 1;
			}

			foreach (var (call, ret) in new[] { ("sub68", "ret68"), ("sub1", "m_ret1"), ("sub2", "m_ret2"), ("sub3", "m_ret3"), ("ssub", "s_ret") })
			{
				var d = hits.GetValueOrDefault(call) - hits.GetValueOrDefault(ret);
				c.Ok($"{call} hits {hits.GetValueOrDefault(call)}, {ret} hits {hits.GetValueOrDefault(ret)}", d is 0 or 1);
			}

			// once the SH-2s run, every frame calls every routine
			for (var frame = 10; frame < FRAMES; frame++)
			{
				c.Ok($"frame {frame}: hits per routine {string.Join(' ', perFrame[frame])}", perFrame[frame].All(static n => n >= 10));
			}

			c.Ok("never reported: " + string.Join(", ", bogus.Distinct()), bogus.Count is 0);
			Console.WriteLine("hits in {0} frames: {1}", FRAMES, string.Join(' ', hits.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}={kv.Value}")));
			Console.WriteLine("hits per frame, frames 10-12 ({0}): {1}", string.Join(' ', counters.Select(static r => r.Routine)),
				string.Join(" | ", perFrame.Skip(10).Take(3).Select(static f => string.Join(' ', f))));
			c.AssertAll("32x_hooks");
		}

		[TestMethod]
		public void HitsMD()
		{
			var info = RomInfo.Read("md_hooks.bin");
			var l = info.Labels;
			var ssp = (uint)info.Json["ssp"]!;
			var usp = (uint)info.Json["usp"]!;
			var opcodes = new Dictionary<string, uint>
			{
				["sub_a"] = 0x52B9, ["ret_a"] = 0x027C, ["sub_b"] = 0x52B9, ["ret_b"] = 0x4E40,
				["trap0"] = 0x52B9, ["ret_trap"] = 0x52B9, ["vblank"] = 0x52B9,
			};
			using var rig = Rig.Load(TestEnv.HookedCore, info.Path);
			var log = new List<Hit>();
			foreach (var name in opcodes.Keys)
			{
				rig.AddExec(Rig.M68K, l[name], name, (a, v, f) => log.Add(Record(rig, info, name, Rig.M68K, a, v, f)));
			}

			rig.Frames(FRAMES);
			var c = new Checker();
			var hits = new Dictionary<string, int>();
			foreach (var e in log)
			{
				var n = hits.TryGetValue(e.Name, out var k) ? k : 0;
				var w = $"{e.Name} #{n}";
				var v = e.Vars;
				ulong R(string reg) => e.Regs["M68K " + reg].Value;
				c.Eq(w + " flags", e.Flags, EXECUTE);
				c.Eq(w + " PC", R("PC"), e.Addr);
				c.Eq(w + " value = opcode", e.Value, opcodes[e.Name]);
				switch (e.Name)
				{
					case "sub_a":
						c.Eq(w + " D0", R("D0"), 0x1234A001);
						c.Eq(w + " D1", R("D1"), 0x1234A002);
						c.Eq(w + " A2", R("A2"), 0x00ABCD00);
						c.Eq(w + " D7 = iteration", R("D7"), v["iter"]);
						c.Eq(w + " return address", e.Stack, l["ret_a"]);
						c.Eq(w + " SR", R("SR") & 0xFF00, 0x2000);
						c.Eq(w + " SSP", R("SSP"), ssp - 4);
						c.Eq(w + " A7", R("A7"), ssp - 4);
						c.Eq(w + " USP", R("USP"), usp);
						c.Eq(w + " count", v["count_a"], (uint)n);
						break;
					case "ret_a":
						c.Eq(w + " D2", R("D2"), 0xAAAA0001);
						c.Eq(w + " D3", R("D3"), 0xAAAA0002);
						c.Eq(w + " SR", R("SR"), 0x2008);
						c.Eq(w + " A7", R("A7"), ssp);
						break;
					case "sub_b":
						c.Eq(w + " D0", R("D0"), 0x5678B001);
						c.Eq(w + " D1", R("D1"), 0x5678B002);
						c.Eq(w + " A3", R("A3"), 0x00DCBA00);
						c.Eq(w + " return address", e.Stack, l["ret_b"]);
						c.Eq(w + " SR (user mode)", R("SR") & 0xFF00, 0x0000);
						c.Eq(w + " USP", R("USP"), usp - 4);
						c.Eq(w + " A7", R("A7"), usp - 4);
						c.Eq(w + " SSP", R("SSP"), ssp);
						c.Eq(w + " count", v["count_b"], (uint)n);
						break;
					case "ret_b":
						c.Eq(w + " D2", R("D2"), 0xBBBB0001);
						c.Eq(w + " SR", R("SR"), 0x001F);
						c.Eq(w + " USP", R("USP"), usp);
						break;
					case "trap0":
						c.Eq(w + " SR", R("SR"), 0x201F);
						c.Eq(w + " SSP", R("SSP"), ssp - 6);
						c.Eq(w + " USP", R("USP"), usp);
						c.Eq(w + " stacked SR", e.Stack >> 16, 0x001F);
						c.Eq(w + " count", v["count_trap"], (uint)n);
						break;
					case "ret_trap":
						c.Eq(w + " SR", R("SR"), 0x201F);
						c.Eq(w + " A7", R("A7"), ssp);
						break;
					case "vblank":
						c.Eq(w + " SR", R("SR") & 0x2700, 0x2600);
						c.Eq(w + " count", v["count_vbl"], (uint)n);
						break;
				}

				hits[e.Name] = n + 1;
			}

			foreach (var (call, ret) in new[] { ("sub_a", "ret_a"), ("sub_b", "ret_b"), ("trap0", "ret_trap") })
			{
				c.Ok($"{call} hits {hits.GetValueOrDefault(call)}, {ret} hits {hits.GetValueOrDefault(ret)}",
					hits.GetValueOrDefault(call) - hits.GetValueOrDefault(ret) is 0 or 1);
			}

			c.Ok($"sub_a hit {hits.GetValueOrDefault("sub_a")} times", hits.GetValueOrDefault("sub_a") > FRAMES * 10);
			c.Ok($"vblank hit {hits.GetValueOrDefault("vblank")} times in {FRAMES} frames", hits.GetValueOrDefault("vblank") is >= FRAMES - 1 and <= FRAMES);
			Console.WriteLine("hits in {0} frames: {1}", FRAMES, string.Join(' ', hits.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}={kv.Value}")));
			c.AssertAll("md_hooks");
		}

		/// <summary>
		/// Counts one frame's instructions on each CPU with an address-less callback, one scope at a time, replaying the
		/// same frame from a savestate; then with all three at once.
		/// </summary>
		[TestMethod]
		public void AddresslessCallbacksCountInstructions()
		{
			const int START = 30;
			var info = RomInfo.Read("32x_hooks.32x");
			using var rig = Rig.Load(TestEnv.HookedCore, info.Path);
			rig.Frames(START);
			var state = rig.Core.CloneSavestate();
			var counts = new long[Rig.Scopes.Length];
			for (var i = 0; i < Rig.Scopes.Length; i++)
			{
				rig.Core.LoadStateBinary(state);
				var wrongFlags = 0;
				var callback = rig.AddExec(Rig.Scopes[i], null, "count", (_, _, f) =>
				{
					counts[i]++;
					if (f != EXECUTE) wrongFlags++;
				});
				rig.Frame();
				rig.Remove(callback);
				Assert.AreEqual(0, wrongFlags);
			}

			// MemoryCallbackSystem calls an address-less callback whatever the scope, so with all three watched each one
			// sees every CPU's instructions
			rig.Core.LoadStateBinary(state);
			var all = new long[Rig.Scopes.Length];
			var callbacks = Enumerable.Range(0, Rig.Scopes.Length).Select(i => rig.AddExec(Rig.Scopes[i], null, "count all", (_, _, _) => all[i]++)).ToList();
			rig.Frame();
			callbacks.ForEach(rig.Remove);

			Console.WriteLine("instructions in frame {0}: 68000 {1}, master {2}, slave {3}; each of 3 address-less callbacks at once: {4}",
				START, counts[0], counts[1], counts[2], string.Join(' ', all));
			Assert.IsTrue(counts.All(static n => n > 1000), "every CPU runs");
			CollectionAssert.AreEqual(Enumerable.Repeat(counts.Sum(), 3).ToArray(), all);

			// the native harness, if run_tests.sh made its counts. They can't be exactly the same: PicoPower() picks where
			// the 68000 starts in the frame with rand(), which is musl's in the waterbox and glibc's in the harness.
			if (TestEnv.NativeCounts is { } path)
			{
				var native = File.ReadLines(path).Select(static s => s.Split(' ')).First(f => f[0] is "C" && f[1] == START.ToString())
					.Skip(2).Select(long.Parse).ToArray();
				Console.WriteLine("the native harness, same frame: {0}", string.Join(' ', native));
				for (var i = 0; i < counts.Length; i++)
				{
					Assert.IsTrue(Math.Abs(counts[i] - native[i]) <= native[i] / 200, $"{Rig.Scopes[i]}: {counts[i]} here, {native[i]} natively");
				}
			}
		}

		[TestMethod]
		public void RemovingACallbackStopsIt()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			using var rig = Rig.Load(TestEnv.HookedCore, info.Path);
			rig.Frames(10);
			int sub1 = 0, ssub = 0, sub68 = 0, any68 = 0;
			var cbSub1 = rig.AddExec(Rig.Master, info["sub1"], "sub1", (_, _, _) => sub1++);
			var cbSsub = rig.AddExec(Rig.Slave, info["ssub"], "ssub", (_, _, _) => ssub++);
			var cbSub68 = rig.AddExec(Rig.M68K, info["sub68"], "sub68", (_, _, _) => sub68++);
			var cbAny = rig.AddExec(Rig.M68K, null, "any 68000", (_, _, _) => any68++);
			rig.Frames(3);
			Assert.IsTrue(sub1 > 0 && ssub > 0 && sub68 > 0 && any68 > 1000, $"{sub1} {ssub} {sub68} {any68}");

			rig.Remove(cbSub1);
			rig.Remove(cbAny);
			sub1 = ssub = sub68 = any68 = 0;
			rig.Frames(3);
			Assert.AreEqual(0, sub1, "removed");
			Assert.AreEqual(0, any68, "removed");
			Assert.IsTrue(ssub > 0 && sub68 > 0, "the others still fire");

			rig.Remove(cbSsub);
			rig.Remove(cbSub68);
			sub1 = ssub = sub68 = any68 = 0;
			rig.Frames(3);
			Assert.AreEqual(0, ssub + sub68);
			Assert.IsFalse(rig.Callbacks.HasExecutes);
		}

		/// <summary>Savestates leave the hooks out: loading one keeps the callbacks that are registered, and they hit the same.</summary>
		[TestMethod]
		public void SavestateRoundTrip()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			using var rig = Rig.Load(TestEnv.HookedCore, info.Path);
			var log = new List<string>();
			string[] keep = [ "PC", "PR", "R4", "R5", "R7", "D0", "D7" ];
			void Log(string name, string scope, uint addr)
			{
				var regs = rig.Debuggable.GetCpuFlagsAndRegisters();
				var r = keep.Select(k => Prefix(scope) + k).Where(regs.ContainsKey).Select(k => $"{regs[k].Value:X}");
				log.Add($"{rig.Core.Frame} {name} {addr:X8} {string.Join(' ', r)}");
			}

			foreach (var (scope, name) in new[] { (Rig.M68K, "sub68"), (Rig.Master, "sub1"), (Rig.Master, "m_slot3"), (Rig.Slave, "ssub") })
			{
				rig.AddExec(scope, info[name], name, (a, _, _) => Log(name, scope, a));
			}

			rig.Frames(20);
			var state = rig.Core.CloneSavestate();
			log.Clear();
			rig.Frames(20);
			var first = log.ToList();
			rig.Core.LoadStateBinary(state);
			log.Clear();
			rig.Frames(20);
			Assert.IsTrue(first.Count > 20 * 30, $"{first.Count} hits");
			CollectionAssert.AreEqual(first, log, "the same hits after loading the state");

			// what is registered when a state loads stays: here only ssub
			foreach (var callback in rig.Callbacks.ToList())
			{
				rig.Callbacks.Remove(callback.Callback);
			}

			rig.AddExec(Rig.Slave, info["ssub"], "ssub", (a, _, _) => Log("ssub", Rig.Slave, a));
			rig.Core.LoadStateBinary(state);
			log.Clear();
			rig.Frames(20);
			CollectionAssert.AreEqual(first.Where(static s => s.Contains(" ssub ")).ToList(), log, "only ssub, with the same hits");
			Console.WriteLine("{0} hits in 20 frames, the same after loading the state", first.Count);
		}
	}
}
