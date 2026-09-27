using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive;

using Newtonsoft.Json.Linq;

namespace BizHawk.Tests.PicoDriveHooks
{
	/// <summary>One routine's file from the call recorder, as <c>Dist/32x-hooks/pdcr.py</c> reads it.</summary>
	internal sealed class Pdcr
	{
		public sealed record class Call(uint Frame, uint Seq, uint Flags, uint[] Entry, uint[] Exit, byte[] EntryBytes, byte[] ExitBytes)
		{
			public bool Returned => (Flags & CallRecorder.FLAG_RETURNED) != 0;

			/// <summary>a big-endian long of the entry or exit bytes</summary>
			public uint Long(bool exit, int offset)
			{
				var b = exit ? ExitBytes : EntryBytes;
				return (uint)(b[offset] << 24 | b[offset + 1] << 16 | b[offset + 2] << 8 | b[offset + 3]);
			}
		}

		public int Cpu;
		public uint Address;
		public string Name = "";
		public int RegisterCount;
		public readonly List<(string Domain, uint Start, uint Length, string Label)> Regions = new();
		public readonly List<Call> Calls = new();

		public static Pdcr Read(string path)
		{
			using var r = new BinaryReader(File.OpenRead(path));
			string Str() => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
			Assert.AreEqual("PDCR", Encoding.ASCII.GetString(r.ReadBytes(4)), path);
			Assert.AreEqual(1U, r.ReadUInt32(), "version");
			var ret = new Pdcr { Cpu = (int)r.ReadUInt32(), Address = r.ReadUInt32(), Name = Str(), RegisterCount = (int)r.ReadUInt32() };
			var regions = r.ReadUInt32();
			for (var i = 0; i < regions; i++)
			{
				ret.Regions.Add((Str(), r.ReadUInt32(), r.ReadUInt32(), Str()));
			}

			var bytes = (int)ret.Regions.Sum(static g => g.Length);
			var size = 12 + 8 * ret.RegisterCount + 2 * bytes;
			while (r.BaseStream.Length - r.BaseStream.Position >= size)
			{
				uint[] Regs() => Enumerable.Range(0, ret.RegisterCount).Select(_ => r.ReadUInt32()).ToArray();
				ret.Calls.Add(new(r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), Regs(), Regs(), r.ReadBytes(bytes), r.ReadBytes(bytes)));
			}

			Assert.AreEqual(r.BaseStream.Length, r.BaseStream.Position, $"{path} ends in a partial record");
			return ret;
		}
	}

	/// <summary>
	/// The call recorder (<c>PICODRIVE_CALL_RECORDER</c>) on the test ROMs of <c>waterbox/picodrive/hooktest</c>, and its
	/// bookkeeping of nested, recursive and abandoned calls against a made-up CPU.
	/// </summary>
	[TestClass]
	public sealed class CallRecorderTests
	{
		private const string ENV_VAR = "PICODRIVE_CALL_RECORDER";

		// register indexes, as GetRegisters writes them
		private const int D0 = 0, A0 = 8, A7 = 15, PC = 16, SR = 17, USP = 18;
		private const int R0 = 0, R15 = 15, PR = 17, MACH = 21, MACL = 22;

		/// <summary>an empty folder for this test's config and output</summary>
		private string WorkDir(string name)
		{
			var dir = Path.Combine(Path.GetDirectoryName(TestEnv.RomDir)!, "recorder", name);
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, recursive: true);
			}

			Directory.CreateDirectory(dir);
			return dir;
		}

		/// <summary>loads a ROM with the recorder reading <paramref name="config"/>, written to <paramref name="dir"/></summary>
		private static Rig LoadRecording(string dir, string config, string rom, List<string>? messages = null)
		{
			var path = Path.Combine(dir, "config.txt");
			File.WriteAllText(path, config);
			Environment.SetEnvironmentVariable(ENV_VAR, path);
			try
			{
				return Rig.Load(TestEnv.HookedCore, rom, messages: m => messages?.Add(m));
			}
			finally
			{
				Environment.SetEnvironmentVariable(ENV_VAR, null);
			}
		}

		private static Dictionary<string, uint> Counters(Rig rig, RomInfo info)
			=> info.Vars.ToDictionary(static kv => kv.Key, kv => rig.PeekGuest(kv.Value));

		/// <summary>the RAM counters of the routines, before frame <c>first</c> and after frame <c>last</c> of the window</summary>
		private static (Dictionary<string, uint> Before, Dictionary<string, uint> After) RunWindow(Rig rig, RomInfo info, int first, int last, int frames)
		{
			Dictionary<string, uint>? before = null, after = null;
			for (var frame = 0; frame < frames; frame++)
			{
				if (frame == first)
				{
					before = Counters(rig, info);
				}

				rig.Frame();
				if (frame == last)
				{
					after = Counters(rig, info);
				}
			}

			return (before!, after!);
		}

		[TestMethod]
		public void Records32X()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			var l = info.Labels;
			var v = info.Vars;
			var dir = WorkDir(nameof(Records32X));
			const int FIRST = 20, LAST = 69;
			var config = $"""
				# the test ROM's routines on all three CPUs, and their counters
				routine m68k 0x{l["sub68"]:X} sub68 regions=ram,odd
				routine sh2m 0x{l["sub1"]:X} sub1 regions=vars
				routine sh2m ${l["sub2"]:X} sub2 regions=vars
				routine sh2m 0x{l["sub3"]:X}   # named sh2m_..., with every region
				routine sh2s 0x{l["ssub"]:X} ssub regions=vars
				region 68K RAM 0 8 ram                           # iter, count68
				region 68K RAM 5 3 odd                           # the low 3 bytes of count68, at an odd address
				region "32X RAM" 0x{v["m_iter"] & 0x3FFFF:X} 0x18 vars   # m_iter, cnt1, cnt2, cnt3, s_iter, s_cnt
				frames {FIRST} {LAST}
				maxcalls 100000
				""";
			var messages = new List<string>();
			(Dictionary<string, uint> Before, Dictionary<string, uint> After) counters;
			using (var rig = LoadRecording(dir, config, info.Path, messages))
			{
				counters = RunWindow(rig, info, FIRST, LAST, LAST + 10);
			}

			Console.WriteLine(string.Join('\n', messages));
			Assert.IsTrue(messages.Exists(static m => m.StartsWith("Call recorder on")), "message");
			var index = JObject.Parse(File.ReadAllText(Path.Combine(dir, "calls", "index.json")));
			var c = new Checker();
			var mul = unchecked(0x11110001U * 0x11110002U);
			var dmul = unchecked(0x22220001UL * 0x22220002UL);
			var sub3 = $"sh2m_{l["sub3"]:X8}";
			(string File, string Label, string Counter, string Ret)[] routines =
			[
				("sub68", "sub68", "count68", "ret68"),
				("sub1", "sub1", "cnt1", "m_ret1"),
				("sub2", "sub2", "cnt2", "m_ret2"),
				(sub3, "sub3", "cnt3", "m_ret3"),
				("ssub", "ssub", "s_cnt", "s_ret"),
			];
			foreach (var (file, label, counter, ret) in routines)
			{
				var pdcr = Pdcr.Read(Path.Combine(dir, "calls", file + ".pdcr"));
				var entry = (JObject)index["routines"]!.First(r => (string)r["name"]! == file);
				var want = counters.After[counter] - counters.Before[counter];
				var calls = pdcr.Calls;
				var w0 = $"{label}: {calls.Count} calls, counted {want}";
				// a call can straddle an edge of the window: entered in it and counted after it, or the other way
				c.Ok(w0, calls.Count - (long)want is -1 or 0 or 1);
				c.Ok(w0 + " (at least 10 a frame)", want >= 10 * (LAST - FIRST + 1));
				c.Eq(label + " index.json calls", (ulong)(int)entry["calls"]!, (ulong)calls.Count);
				c.Eq(label + " index.json returned", (ulong)(int)entry["returned"]!, (ulong)calls.Count(static k => k.Returned));
				c.Eq(label + " address", pdcr.Address, l[label]);
				c.Eq(label + " cpu", (ulong)pdcr.Cpu, label is "sub68" ? 0UL : label is "ssub" ? 2UL : 1UL);
				c.Eq(label + " registers", (ulong)pdcr.RegisterCount, label is "sub68" ? 20UL : 23UL);
				// the counter's offset in the call's bytes, and the iteration's
				var is68K = label is "sub68";
				var vars = is68K ? [ "iter", "count68" ] : new[] { "m_iter", "cnt1", "cnt2", "cnt3", "s_iter", "s_cnt" };
				var offset = (label == "sub3" ? 11 : 0) + 4 * Array.IndexOf(vars, counter);
				var iterOffset = (label == "sub3" ? 11 : 0) + 4 * (label is "ssub" ? 4 : 0);
				c.Eq(label + " regions", (ulong)pdcr.Regions.Count, label is "sub3" ? 3UL : label is "sub68" ? 2UL : 1UL);
				var counted = calls.Count > 0 ? calls[0].Long(exit: false, offset) : 0;
				c.Ok($"{label}: the first call's counter {counted}, before the window {counters.Before[counter]}", counted - counters.Before[counter] is 0 or 1);
				for (var i = 0; i < calls.Count; i++)
				{
					var k = calls[i];
					var w = $"{label} #{i} (frame {k.Frame})";
					var last = i == calls.Count - 1;
					c.Ok(w + $" flags {k.Flags}", k.Flags == CallRecorder.FLAG_RETURNED || (last && k.Flags == CallRecorder.FLAG_TRUNCATED));
					c.Ok(w + " frame in the window", k.Frame is >= FIRST and <= LAST);
					c.Ok(w + " seq increases", i is 0 || k.Seq > calls[i - 1].Seq);
					c.Eq(w + " entry PC", k.Entry[PC], l[label]);
					c.Eq(w + " counter at entry", k.Long(exit: false, offset), counted + (uint)i);
					if (!k.Returned)
					{
						continue;
					}

					c.Eq(w + " counter incremented", k.Long(exit: true, offset), k.Long(exit: false, offset) + 1);
					c.Eq(w + " exit PC = the return address", k.Exit[PC], l[ret]);
					var iter = k.Long(exit: false, iterOffset);
					switch (label)
					{
						case "sub68":
							c.Eq(w + " D0", k.Entry[D0], 0x6800A001);
							c.Eq(w + " D7 = iteration", k.Entry[D0 + 7], iter);
							c.Eq(w + " SR", k.Entry[SR] & 0xFF00, 0x2700);
							c.Eq(w + " exit D1", k.Exit[D0 + 1], 0x68000042);
							c.Eq(w + " exit A7", k.Exit[A7], k.Entry[A7] + 4);
							c.Eq(w + " odd region", (ulong)(k.EntryBytes[8] << 16 | k.EntryBytes[9] << 8 | k.EntryBytes[10]), k.Long(exit: false, 4) & 0xFFFFFF);
							c.Eq(w + " odd region at exit", (ulong)(k.ExitBytes[8] << 16 | k.ExitBytes[9] << 8 | k.ExitBytes[10]), k.Long(exit: true, 4) & 0xFFFFFF);
							break;
						case "sub1":
							c.Eq(w + " R4", k.Entry[R0 + 4], 0x11110001);
							c.Eq(w + " R5", k.Entry[R0 + 5], 0x11110002);
							c.Eq(w + " R7 = iteration", k.Entry[R0 + 7], iter);
							c.Eq(w + " PR", k.Entry[PR], l["m_ret1"]);
							c.Eq(w + " exit R9", k.Exit[R0 + 9], 0xA1A1A1A1);
							c.Eq(w + " exit R10", k.Exit[R0 + 10], 0xA1A1A1A2);
							c.Eq(w + " exit MACL", k.Exit[MACL], mul);
							break;
						case "sub2":
							c.Eq(w + " R1 = JSR target", k.Entry[R0 + 1], l["sub2"]);
							c.Eq(w + " R4", k.Entry[R0 + 4], 0x22220001);
							c.Eq(w + " R7 = iteration", k.Entry[R0 + 7], iter);
							c.Eq(w + " exit R9", k.Exit[R0 + 9], 0xB2B2B2B1);
							c.Eq(w + " exit MACH:MACL", (ulong)k.Exit[MACH] << 32 | k.Exit[MACL], dmul);
							break;
						case "sub3":
							c.Eq(w + " R4", k.Entry[R0 + 4], 0x33330001);
							c.Eq(w + " R5 from the delay slot", k.Entry[R0 + 5], 0x33);
							c.Eq(w + " exit R9", k.Exit[R0 + 9], 0xC3C3C3C1);
							c.Eq(w + " exit R6 from the RTS delay slot", k.Exit[R0 + 6], 0x44);
							break;
						case "ssub":
							c.Eq(w + " R4", k.Entry[R0 + 4], 0x55550001);
							c.Eq(w + " R5 from the delay slot", k.Entry[R0 + 5], 0x5A);
							c.Eq(w + " R7 = iteration", k.Entry[R0 + 7], iter);
							c.Eq(w + " exit R9", k.Exit[R0 + 9], 0x5555AAAA);
							break;
					}

					if (!is68K)
					{
						c.Eq(w + " exit R15", k.Exit[R15], k.Entry[R15]);
					}
				}

				Console.WriteLine($"{label}: {calls.Count} calls in frames {FIRST}-{LAST}, counter +{want}, {calls.Count(static k => k.Returned)} returned");
			}

			c.AssertAll("recorder, 32x_hooks");
		}

		[TestMethod]
		public void RecordsMD()
		{
			var info = RomInfo.Read("md_hooks.bin");
			var l = info.Labels;
			var dir = WorkDir(nameof(RecordsMD));
			// sub_a is called by JSR in supervisor mode, sub_b by BSR in user mode; trap0 returns by RTE, so it never returns
			var config = $"""
				routine m68k 0x{l["sub_a"]:X} sub_a
				routine m68k 0x{l["sub_b"]:X} sub_b
				routine m68k 0x{l["trap0"]:X} trap0
				region 68K RAM 0 0x14 vars    # iter, count_a, count_b, count_vbl, count_trap
				frames 10 39
				maxcalls 100000
				""";
			(Dictionary<string, uint> Before, Dictionary<string, uint> After) counters;
			using (var rig = LoadRecording(dir, config, info.Path))
			{
				counters = RunWindow(rig, info, 10, 39, 45);
			}

			var c = new Checker();
			foreach (var (name, counter, offset) in new[] { ("sub_a", "count_a", 4), ("sub_b", "count_b", 8), ("trap0", "count_trap", 16) })
			{
				var calls = Pdcr.Read(Path.Combine(dir, "calls", name + ".pdcr")).Calls;
				var want = counters.After[counter] - counters.Before[counter];
				c.Ok($"{name}: {calls.Count} calls, counted {want}", calls.Count - (long)want is -1 or 0 or 1 && want > 100);
				var counted = calls[0].Long(exit: false, offset);
				c.Ok($"{name}: the first call's counter {counted}, before the window {counters.Before[counter]}", counted - counters.Before[counter] is 0 or 1);
				for (var i = 0; i < calls.Count; i++)
				{
					var k = calls[i];
					var w = $"{name} #{i}";
					var last = i == calls.Count - 1;
					c.Eq(w + " counter at entry", k.Long(exit: false, offset), counted + (uint)i);
					if (last && k.Flags == CallRecorder.FLAG_TRUNCATED)
					{
						continue;
					}

					c.Eq(w + " counter incremented", k.Long(exit: true, offset), k.Long(exit: false, offset) + 1);
					switch (name)
					{
						case "sub_a":
							c.Eq(w + " flags", k.Flags, CallRecorder.FLAG_RETURNED);
							c.Eq(w + " D0", k.Entry[D0], 0x1234A001);
							c.Eq(w + " A2", k.Entry[A0 + 2], 0x00ABCD00);
							c.Eq(w + " supervisor mode", k.Entry[SR] & 0x2000, 0x2000);
							c.Eq(w + " exit PC", k.Exit[PC], l["ret_a"]);
							c.Eq(w + " exit D2", k.Exit[D0 + 2], 0xAAAA0001);
							c.Eq(w + " exit D3", k.Exit[D0 + 3], 0xAAAA0002);
							break;
						case "sub_b":
							c.Eq(w + " flags", k.Flags, CallRecorder.FLAG_RETURNED);
							c.Eq(w + " D1", k.Entry[D0 + 1], 0x5678B002);
							c.Eq(w + " A3", k.Entry[A0 + 3], 0x00DCBA00);
							c.Eq(w + " user mode", k.Entry[SR] & 0x2000, 0);
							c.Eq(w + " A7 = USP", k.Entry[A7], k.Entry[USP]);
							c.Eq(w + " exit PC", k.Exit[PC], l["ret_b"]);
							c.Eq(w + " exit D2", k.Exit[D0 + 2], 0xBBBB0001);
							c.Eq(w + " exit CCR", k.Exit[SR] & 0x1F, 0x1F);
							c.Eq(w + " exit A7", k.Exit[A7], k.Entry[A7] + 4);
							break;
						default:
							// abandoned at the next recorded instruction, sub_a's entry, whose A7 is above the trap's
							c.Eq(w + " flags", k.Flags, CallRecorder.FLAG_ABANDONED);
							c.Eq(w + " exit PC", k.Exit[PC], l["sub_a"]);
							break;
					}
				}

				Console.WriteLine($"{name}: {calls.Count} calls, counter +{want}, flags {string.Join(' ', calls.GroupBy(static k => k.Flags).Select(static g => $"{g.Key}x{g.Count()}"))}");
			}

			c.AssertAll("recorder, md_hooks");
		}

		private static Dictionary<uint, (int Calls, int Sites)> ReadDiscovery(string path)
			=> File.ReadAllLines(path).Where(static s => !s.StartsWith("#"))
				.Select(static s => s.Split([ ' ' ], StringSplitOptions.RemoveEmptyEntries))
				.ToDictionary(static f => Convert.ToUInt32(f[0], 16), static f => (int.Parse(f[1]), int.Parse(f[2])));

		[TestMethod]
		public void DiscoverFindsRoutines()
		{
			var c = new Checker();
			foreach (var (rom, cpus) in new[]
			{
				("32x_hooks.32x", new[] { ("m68k", new[] { ("sub68", "count68") }), ("sh2m", new[] { ("sub1", "cnt1"), ("sub2", "cnt2"), ("sub3", "cnt3") }), ("sh2s", new[] { ("ssub", "s_cnt") }) }),
				("md_hooks.bin", new[] { ("m68k", new[] { ("sub_a", "count_a"), ("sub_b", "count_b") }) }),
			})
			{
				var info = RomInfo.Read(rom);
				var dir = WorkDir(nameof(DiscoverFindsRoutines) + "-" + rom);
				// 30 frames from frame 10
				var config = string.Join('\n', cpus.Select(static p => $"discover {p.Item1} 30")) + "\nframes 10 200\n";
				(Dictionary<string, uint> Before, Dictionary<string, uint> After) counters;
				var sw = Stopwatch.StartNew();
				using (var rig = LoadRecording(dir, config, info.Path))
				{
					counters = RunWindow(rig, info, 10, 39, 45);
				}

				Console.WriteLine($"{rom}: discovering on {cpus.Length} CPU(s), 45 frames in {sw.Elapsed.TotalSeconds:F1} s");
				foreach (var (cpu, routines) in cpus)
				{
					var path = Path.Combine(dir, "calls", $"discover-{cpu}.txt");
					var found = ReadDiscovery(path);
					Console.WriteLine(string.Join('\n', File.ReadLines(path).Take(3 + routines.Length)));
					var counts = found.Values.Select(static t => t.Calls).ToList();
					c.Ok($"{rom} {cpu}: sorted by calls", counts.SequenceEqual(counts.OrderByDescending(static n => n)));
					foreach (var (label, counter) in routines)
					{
						var want = (long)counters.After[counter] - counters.Before[counter];
						var got = found.TryGetValue(info[label], out var t) ? t : (0, 0);
						// a call can straddle the window's edges; on the Mega Drive ROM, VBlank can take the instruction after a JSR
						c.Ok($"{rom} {cpu} {label}: {got.Item1} calls from {got.Item2} sites, counted {want}", Math.Abs(got.Item1 - want) <= 2 && got.Item2 is 1 && want > 100);
					}
				}
			}

			c.AssertAll("discover");
		}

		/// <summary>Recording must not change emulation: per-frame hashes of video, audio and all writable memory.</summary>
		[TestMethod]
		public void RecordingDoesNotChangeEmulation()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			var l = info.Labels;
			var dir = WorkDir(nameof(RecordingDoesNotChangeEmulation));
			var config = $"""
				routine m68k 0x{l["sub68"]:X}
				routine sh2m 0x{l["sub1"]:X}
				routine sh2s 0x{l["ssub"]:X}
				region 68K RAM 0 0x1000 ram
				region 32X RAM 0 0x400 sdram
				discover sh2m 40
				frames 0 150
				maxcalls 500
				""";
			List<ulong> Hashes(Rig rig) => Enumerable.Range(0, 200).Select(_ => { rig.Frame(); return rig.FrameHash(); }).ToList();
			List<ulong> off, on;
			using (var rig = Rig.Load(TestEnv.HookedCore, info.Path))
			{
				off = Hashes(rig);
			}

			using (var rig = LoadRecording(dir, config, info.Path))
			{
				on = Hashes(rig);
			}

			var diff = off.Zip(on, static (a, b) => a == b).ToList().IndexOf(false);
			Console.WriteLine($"recording vs not, 200 frames: {(diff < 0 ? "identical" : $"differ from frame {diff}")}");
			Assert.AreEqual(-1, diff);
			var index = JObject.Parse(File.ReadAllText(Path.Combine(dir, "calls", "index.json")));
			Assert.IsTrue(index["routines"]!.All(static r => (int)r["calls"]! is 500 && (bool)r["maxcallsReached"]!), index.ToString());
		}

		/// <summary>The execute callbacks get the same hits with the recorder on, and it records with them on.</summary>
		[TestMethod]
		public void CallbacksUnchangedWhileRecording()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			var l = info.Labels;
			var dir = WorkDir(nameof(CallbacksUnchangedWhileRecording));
			var config = $"""
				routine m68k 0x{l["sub68"]:X} sub68
				routine sh2m 0x{l["sub2"]:X} sub2
				routine sh2s 0x{l["ssub"]:X} ssub
				discover sh2s 20
				""";
			List<string> Run(Rig rig)
			{
				var log = new List<string>();
				// address-less on the 68000's scope only: it must not see the SH-2 instructions the recorder watches
				rig.AddExec(Rig.M68K, null, "every 68000 instruction", (a, _, _) => log.Add($"any {a:X}"));
				rig.AddExec(Rig.Master, l["sub1"], "sub1", (a, _, _) => log.Add($"sub1 {a:X}"));
				rig.AddExec(Rig.Master, l["sub2"], "sub2", (a, _, _) => log.Add($"sub2 {a:X}"));
				rig.AddExec(Rig.Slave, l["s_ret"], "s_ret", (a, _, _) => log.Add($"s_ret {a:X}"));
				rig.Frames(30);
				return log;
			}

			List<string> without, with;
			using (var rig = Rig.Load(TestEnv.HookedCore, info.Path))
			{
				without = Run(rig);
			}

			using (var rig = LoadRecording(dir, config, info.Path))
			{
				with = Run(rig);
			}

			Console.WriteLine($"callback hits in 30 frames: {without.Count} without the recorder, {with.Count} with it");
			CollectionAssert.AreEqual(without, with);
			foreach (var name in new[] { "sub68", "sub2", "ssub" })
			{
				var calls = Pdcr.Read(Path.Combine(dir, "calls", name + ".pdcr")).Calls;
				Assert.IsTrue(calls.Count > 100 && !calls.Where(static k => !k.Returned).Skip(1).Any(), $"{name}: {calls.Count} calls");
			}

			Assert.IsTrue(ReadDiscovery(Path.Combine(dir, "calls", "discover-sh2s.txt")).ContainsKey(l["ssub"]));
		}

		[TestMethod]
		public void BadConfigTurnsRecorderOff()
		{
			var info = RomInfo.Read("md_hooks.bin");
			var dir = WorkDir(nameof(BadConfigTurnsRecorderOff));
			var config = """
				routine m68k 0x1234 a
				routine z80 0x100
				routine m68k 0x1235
				region 32X RAM 0 16 sdram
				region 68K RAM 0xFFF0 0x20 big
				frames 10 5
				bogus line
				""";
			var messages = new List<string>();
			using (var rig = LoadRecording(dir, config, info.Path, messages))
			{
				rig.Frames(5);
			}

			var message = string.Join('\n', messages);
			Console.WriteLine(message);
			StringAssert.StartsWith(message, "Call recorder off");
			foreach (var line in new[] { "line 2:", "line 3:", "line 4:", "line 5:", "line 6:", "line 7:" })
			{
				StringAssert.Contains(message, line);
			}

			Assert.IsFalse(Directory.Exists(Path.Combine(dir, "calls")));
			messages.Clear();
			Environment.SetEnvironmentVariable(ENV_VAR, Path.Combine(dir, "missing.txt"));
			try
			{
				using var rig = Rig.Load(TestEnv.HookedCore, info.Path, messages: messages.Add);
				rig.Frames(2);
			}
			finally
			{
				Environment.SetEnvironmentVariable(ENV_VAR, null);
			}

			Console.WriteLine(string.Join('\n', messages));
			StringAssert.StartsWith(string.Join('\n', messages), "Call recorder off");
		}

		/// <summary>frames per second with the recorder off and on, on the 32X test ROM</summary>
		[TestMethod]
		public void RecorderSpeed()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			var l = info.Labels;
			var routines = $"""
				routine m68k 0x{l["sub68"]:X}
				routine sh2m 0x{l["sub1"]:X}
				routine sh2m 0x{l["sub2"]:X}
				routine sh2m 0x{l["sub3"]:X}
				routine sh2s 0x{l["ssub"]:X}
				maxcalls 100000000

				""";
			(string What, string? Config, int Frames)[] configs =
			[
				("recorder off", null, 300),
				("5 routines, 32 bytes of SDRAM each", routines + "region 32X RAM 0x30000 0x20 vars\n", 300),
				("sub68 with all 64 KB of 68K RAM", $"routine m68k 0x{l["sub68"]:X}\nregion 68K RAM 0 0x10000 ram\nmaxcalls 100000000\n", 5),
				("discover sh2m", "discover sh2m 1000000\n", 60),
			];
			Console.WriteLine("32X test ROM, fps median of 5 runs (min-max), after 10 frames:");
			foreach (var (what, config, frames) in configs)
			{
				var dir = WorkDir(nameof(RecorderSpeed));
				using (var rig = config is null ? Rig.Load(TestEnv.HookedCore, info.Path) : LoadRecording(dir, config, info.Path))
				{
					rig.Frames(10);
					var fps = new List<double>();
					for (var rep = 0; rep < 5; rep++)
					{
						var sw = Stopwatch.StartNew();
						rig.Frames(frames);
						fps.Add(frames / sw.Elapsed.TotalSeconds);
					}

					fps.Sort();
					Console.Write($"  {what,-40} {fps[2],7:F1}  ({fps[0]:F1}-{fps[4]:F1})");
				}

				var index = Path.Combine(dir, "calls", "index.json");
				if (config?.StartsWith("routine", StringComparison.Ordinal) is true)
				{
					var calls = JObject.Parse(File.ReadAllText(index))["routines"]!.Sum(static r => (int)r["calls"]!);
					var bytes = Directory.GetFiles(Path.Combine(dir, "calls"), "*.pdcr").Sum(static f => new FileInfo(f).Length);
					Console.Write($"  {calls / (10.0 + 5 * frames):F0} calls/frame, {bytes / 1e6:F1} MB");
				}

				Console.WriteLine();
				Directory.Delete(dir, recursive: true);
			}
		}

		/// <summary>a CPU made up of scripted hits, for the bookkeeping of nested, recursive, abandoned and tail calls</summary>
		private sealed class FakeHost : ICallRecorderHost
		{
			public readonly byte[] Ram = new byte[0x10000];
			public readonly uint[] Regs = new uint[23];

			public int Frame { get; set; }

			public IReadOnlyList<(string Name, long Size)> Domains { get; } = [ ("68K RAM", 0x10000) ];

			public void GetRegisters(int cpu, uint[] regs)
				=> Array.Copy(Regs, regs, 23);

			public void ReadDomain(int domain, long start, byte[] dest, int offset, int length)
				=> Array.Copy(Ram, start, dest, offset, length);

			public void WatchListChanged(int cpu)
			{
			}
		}

		[TestMethod]
		public void NestingRecursionAndAbandonedCalls()
		{
			var dir = WorkDir(nameof(NestingRecursionAndAbandonedCalls));
			var path = Path.Combine(dir, "config.txt");
			File.WriteAllText(path, """
				routine sh2m 0x100 a
				routine sh2m 0x200 b
				routine m68k 0x300 c
				region 68K RAM 0 2 mark
				maxcalls 8
				""");
			var host = new FakeHost();
			var recorder = CallRecorder.Create(path, host, "fake", static m => Console.WriteLine(m));
			var steps = new List<string>();
			// a hit on the master SH-2 at addr, with R15 and PR; mark tells the calls apart
			void Sh2(uint addr, uint r15, uint pr = 0, byte mark = 0)
			{
				host.Regs[R15] = r15;
				host.Regs[PR] = pr;
				host.Regs[16] = addr;
				host.Ram[0] = mark;
				recorder.OnExec(1, addr, 0x0009);
			}

			recorder.BeginFrame(0);
			// nested: a calls b
			Sh2(0x100, 0x1000, 0x1004, 1);
			Sh2(0x200, 0x0FF0, 0x0110, 2);
			Sh2(0x110, 0x0FF0);
			Sh2(0x1004, 0x1000);
			// recursive: a calls a calls a
			Sh2(0x100, 0x1000, 0x1004, 3);
			Sh2(0x100, 0x0FF0, 0x0120, 4);
			Sh2(0x100, 0x0FE0, 0x0120, 5);
			Sh2(0x120, 0x0FE0);
			Sh2(0x120, 0x0FF0);
			Sh2(0x1004, 0x1000);
			// the outer call returns first: b is abandoned
			Sh2(0x100, 0x1000, 0x1004, 6);
			Sh2(0x200, 0x0FF0, 0x0130, 7);
			Sh2(0x1004, 0x1000);
			// the stack pointer rises past b's entry value at the next recorded instruction (a's entry): b is abandoned
			Sh2(0x200, 0x0F00, 0x0140, 8);
			Sh2(0x100, 0x0F10, 0x0150, 9);
			Sh2(0x150, 0x0F10);
			// a tail call: a jumps to b, which returns for both
			Sh2(0x100, 0x1000, 0x1004, 10);
			Sh2(0x200, 0x1000, 0x1004, 11);
			Sh2(0x1004, 0x1000);
			// on the 68000: a call in user mode, then one in supervisor mode (an interrupt) with A7 above the first's
			void M68K(uint addr, uint a7, bool super, byte mark = 0)
			{
				host.Regs[A7] = a7;
				host.Regs[SR] = super ? 0x2700U : 0;
				host.Regs[USP] = super ? 0x00FFFE00 : a7;
				host.Regs[USP + 1] = super ? a7 : 0x00FFFF80;
				host.Regs[PC] = addr;
				host.Ram[0] = mark;
				recorder.OnExec(0, addr, 0x4E71);
			}

			host.Ram[0xFE00] = 0x00; host.Ram[0xFE01] = 0x00; host.Ram[0xFE02] = 0x04; host.Ram[0xFE03] = 0x56; // return to $456
			host.Ram[0xFF70] = 0x00; host.Ram[0xFF71] = 0x00; host.Ram[0xFF72] = 0x07; host.Ram[0xFF73] = 0x80; // return to $780
			M68K(0x300, 0x00FFFE00, super: false, 12);
			M68K(0x300, 0x00FFFF70, super: true, 13);
			M68K(0x780, 0x00FFFF74, super: true);
			M68K(0x456, 0x00FFFE04, super: false);
			// pending when the recording stops: truncated; a's 9th call is past maxcalls
			Sh2(0x100, 0x1000, 0x1004, 14);
			Sh2(0x100, 0x1000, 0x1004, 15);
			host.Frame = 1;
			recorder.EndFrame(1);
			recorder.Dispose();

			string Show(Pdcr.Call k) => $"{k.EntryBytes[0]}:{k.Flags}";
			var a = Pdcr.Read(Path.Combine(dir, "calls", "a.pdcr")).Calls;
			var b = Pdcr.Read(Path.Combine(dir, "calls", "b.pdcr")).Calls;
			var c = Pdcr.Read(Path.Combine(dir, "calls", "c.pdcr")).Calls;
			Console.WriteLine($"a {string.Join(' ', a.Select(Show))}\nb {string.Join(' ', b.Select(Show))}\nc {string.Join(' ', c.Select(Show))}");
			// mark:flags in the order written (returned 1, abandoned 2, truncated 4)
			Assert.AreEqual("1:1 5:1 4:1 3:1 6:1 9:1 10:1 14:4", string.Join(' ', a.Select(Show)));
			Assert.AreEqual("2:1 7:2 8:2 11:1", string.Join(' ', b.Select(Show)));
			Assert.AreEqual("13:1 12:1", string.Join(' ', c.Select(Show)));
			// the exit state is the one at the return, or at the instruction that abandoned the call
			Assert.AreEqual(0x1004U, a[3].Exit[16]);
			Assert.AreEqual(0x1004U, b[1].Exit[16]);
			Assert.AreEqual(0x100U, b[2].Exit[16]);
			Assert.AreEqual(0x456U, c[1].Exit[PC]);
			var index = JObject.Parse(File.ReadAllText(Path.Combine(dir, "calls", "index.json")));
			var ia = index["routines"]![0]!;
			Assert.AreEqual(8, (int)ia["calls"]!);
			Assert.IsTrue((bool)ia["maxcallsReached"]!);
			Assert.AreEqual(1, (int)ia["truncated"]!);
		}
	}
}
