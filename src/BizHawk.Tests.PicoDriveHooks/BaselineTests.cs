using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using BizHawk.Emulation.Common;

namespace BizHawk.Tests.PicoDriveHooks
{
	/// <summary>
	/// The hooked core against an unmodified one, both loaded through this assembly: emulation, speed and savestates.
	/// </summary>
	[TestClass]
	public sealed class BaselineTests
	{
		private const int HASH_FRAMES = 300;

		private static List<ulong> Hashes(string core, string rom, int frames, Action<Rig>? setup = null)
		{
			using var rig = Rig.Load(core, rom);
			setup?.Invoke(rig);
			var ret = new List<ulong>();
			for (var i = 0; i < frames; i++)
			{
				rig.Frame();
				ret.Add(rig.FrameHash());
			}

			return ret;
		}

		private static void AssertSame(string what, List<ulong> want, List<ulong> got)
		{
			var diff = -1;
			for (var i = 0; i < want.Count && diff < 0; i++)
			{
				if (want[i] != got[i])
				{
					diff = i;
				}
			}

			Console.WriteLine($"{what}: {(diff < 0 ? $"identical, {want.Count} frames" : $"differ from frame {diff}")}");
			Assert.AreEqual(-1, diff, what);
		}

		/// <summary>Per-frame hashes of video, audio and all writable memory, with no callbacks and with every instruction watched.</summary>
		[TestMethod]
		[DataRow("32x_hooks.32x")]
		[DataRow("md_hooks.bin")]
		public void SameEmulationAsBaseline(string name)
		{
			var baseline = TestEnv.Need(TestEnv.BaselineCore, "PICODRIVE_BASELINE_WBX");
			var rom = RomInfo.Read(name).Path;
			var hooked = Hashes(TestEnv.HookedCore, rom, HASH_FRAMES);
			AssertSame($"{name}: hooked core vs baseline core, no callbacks", Hashes(baseline, rom, HASH_FRAMES), hooked);
			long calls = 0;
			var watched = Hashes(TestEnv.HookedCore, rom, HASH_FRAMES, rig =>
			{
				foreach (var scope in Rig.Scopes)
				{
					rig.AddExec(scope, null, "every instruction", (_, _, _) => calls++);
				}
			});
			Assert.IsTrue(calls > HASH_FRAMES * 1000L);
			AssertSame($"{name}: address-less callbacks on all three scopes vs none", hooked, watched);
		}

		/// <summary>
		/// Loaded as EmuHawk loads it (the system from the extension, no gamedb option), a 32X cartridge has the "32X RAM" and
		/// "32X FB" domains and a Mega Drive one doesn't. Emulation is the same as with the 32X memory allocated late, which
		/// is what 2.11.1 did for every 32X cartridge: the core gets that flag for a game whose system isn't <c>32X</c>.
		/// </summary>
		[TestMethod]
		[DataRow("32x_hooks.32x")]
		[DataRow("md_hooks.bin")]
		public void ThirtyTwoXDomainsAsEmuHawkLoads(string name)
		{
			var rom = RomInfo.Read(name).Path;
			var is32X = name.EndsWith(".32x");
			List<ulong> Run(string? system, out string[] domains)
			{
				using var rig = Rig.Load(TestEnv.HookedCore, rom, preinit32X: false, system: system);
				domains = [ .. rig.Domains.Select(static d => d.Name) ];
				var ret = new List<ulong>();
				for (var i = 0; i < HASH_FRAMES; i++)
				{
					rig.Frame();
					ret.Add(rig.FrameHash("68K RAM"));
				}

				return ret;
			}

			var now = Run(system: null, out var domains);
			var before = Run(system: VSystemID.Raw.GEN, out var domainsBefore);
			Console.WriteLine($"{name}: domains {string.Join(", ", domains)}; with the flag off: {string.Join(", ", domainsBefore)}");
			Assert.AreEqual(is32X, domains.Contains("32X RAM"), "32X RAM");
			Assert.AreEqual(is32X, domains.Contains("32X FB"), "32X FB");
			Assert.IsFalse(domainsBefore.Contains("32X RAM") || domainsBefore.Contains("32X FB"), "the flag off");
			AssertSame($"{name}: video, audio and 68K RAM, as EmuHawk loads it vs the 32X memory allocated late", before, now);
		}

		/// <summary>Frames per second through the whole stack, on the 32X test ROM.</summary>
		[TestMethod]
		public void Speed()
		{
			var info = RomInfo.Read("32x_hooks.32x");
			var configs = new List<(string What, string Core, int Frames, Action<Rig>? Setup)>();
			if (TestEnv.BaselineCore is { } baseline)
			{
				configs.Add(("baseline core", baseline, 600, null));
			}

			long hits = 0;
			configs.Add(("hooked core, no callbacks", TestEnv.HookedCore, 600, null));
			configs.Add(("hooked core, 4 watches never hit", TestEnv.HookedCore, 600, rig =>
			{
				rig.AddExec(Rig.M68K, info["spin"], "cold", (_, _, _) => hits++);
				rig.AddExec(Rig.Master, info["sub1"] | 0x20000000, "cold", (_, _, _) => hits++);
				rig.AddExec(Rig.Master, info["master_start"], "cold", (_, _, _) => hits++);
				rig.AddExec(Rig.Slave, info["sub1"], "cold", (_, _, _) => hits++);
			}));
			configs.Add(("hooked core, 4 watches that hit", TestEnv.HookedCore, 600, rig =>
			{
				rig.AddExec(Rig.M68K, info["sub68"], "hot", (_, _, _) => hits++);
				rig.AddExec(Rig.Master, info["sub1"], "hot", (_, _, _) => hits++);
				rig.AddExec(Rig.Master, info["sub2"], "hot", (_, _, _) => hits++);
				rig.AddExec(Rig.Slave, info["ssub"], "hot", (_, _, _) => hits++);
			}));
			configs.Add(("hooked core, address-less on all three CPUs", TestEnv.HookedCore, 60, rig =>
			{
				foreach (var scope in Rig.Scopes)
				{
					rig.AddExec(scope, null, "all", (_, _, _) => hits++);
				}
			}));

			Console.WriteLine("32X test ROM, fps median of 5 runs (min-max):");
			foreach (var (what, core, frames, setup) in configs)
			{
				using var rig = Rig.Load(core, info.Path);
				setup?.Invoke(rig);
				rig.Frames(30);
				hits = 0;
				var fps = new List<double>();
				for (var rep = 0; rep < 5; rep++)
				{
					var sw = Stopwatch.StartNew();
					rig.Frames(frames);
					fps.Add(frames / sw.Elapsed.TotalSeconds);
				}

				fps.Sort();
				Console.WriteLine($"  {what,-50} {fps[2],7:F1}  ({fps[0]:F1}-{fps[4]:F1})  callbacks/frame {hits / (5.0 * frames):F0}");
			}
		}

		/// <summary>
		/// Whether savestates of an unmodified core load in the hooked one, and the other way. The waterbox host writes a
		/// SHA-256 of the core file into each state and refuses a state from another file.
		/// </summary>
		[TestMethod]
		public void SavestatesAcrossCores()
		{
			var rom = RomInfo.Read("32x_hooks.32x").Path;
			var others = new[] { ("baseline", TestEnv.BaselineCore), ("stock 2.11.1", TestEnv.StockCore) }.Where(static c => c.Item2 is not null).ToList();
			if (others.Count is 0)
			{
				Assert.Inconclusive("neither PICODRIVE_BASELINE_WBX nor PICODRIVE_STOCK_WBX is set");
			}

			foreach (var (what, core) in others)
			{
				Try($"{what} state -> hooked core", core!, TestEnv.HookedCore);
				Try($"hooked state -> {what} core", TestEnv.HookedCore, core!);
			}

			void Try(string what, string from, string to)
			{
				byte[] state;
				List<ulong> want;
				using (var rig = Rig.Load(from, rom))
				{
					rig.Frames(60);
					state = rig.Core.CloneSavestate();
					want = Enumerable.Range(0, 30).Select(_ => { rig.Frame(); return rig.FrameHash(); }).ToList();
				}

				using var other = Rig.Load(to, rom);
				try
				{
					other.Core.LoadStateBinary(state);
				}
				catch (Exception e)
				{
					Console.WriteLine($"{what}: refused, {e.GetType().Name}: {e.Message}");
					return;
				}

				// if a state ever loads, emulation must go on exactly as in the core that made it
				var got = Enumerable.Range(0, 30).Select(_ => { other.Frame(); return other.FrameHash(); }).ToList();
				AssertSame($"{what}: loaded, then", want, got);
			}
		}
	}
}
