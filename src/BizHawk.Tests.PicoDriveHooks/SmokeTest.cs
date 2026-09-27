using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BizHawk.Tests.PicoDriveHooks
{
	/// <summary>
	/// Opt-in, on a real 32X cartridge named by <c>BIZHAWK_32X_SMOKE_ROM</c>: the same steps as
	/// <c>Dist/32x-hooks/smoke_32x.lua</c>, through the same API the Lua libraries call.
	/// </summary>
	[TestClass]
	public sealed class SmokeTest
	{
		private const int FRAMES = 300;

		private const int MAX_PRINTED = 10;

		[TestMethod]
		public void Smoke32X()
		{
			var romPath = TestEnv.Need(TestEnv.SmokeRom, "BIZHAWK_32X_SMOKE_ROM");
			// as EmuHawk loads a .32x, with no gamedb options
			using var rig = Rig.Load(TestEnv.HookedCore, romPath, preinit32X: false);

			// 1. the entry points, as memory.read_u32_be(0x3E0, "MD CART") reads them
			// loaded as EmuHawk loads it, the 32X domains exist only if the gamedb gives the game the option "32X"
			Console.WriteLine($"memory domains: {string.Join(", ", rig.Domains.Select(static d => d.Name))}");
			var cart = rig.Domains["MD CART"]!;
			var master = cart.PeekUint(0x3E0, bigEndian: true);
			var slave = cart.PeekUint(0x3E4, bigEndian: true);
			var header = Encoding.ASCII.GetString(Enumerable.Range(0x3C0, 16).Select(a => cart.PeekByte(a)).ToArray());
			var raw = string.Join(' ', Enumerable.Range(0x3E0, 8).Select(a => $"{cart.PeekByte(a):X2}"));
			Console.WriteLine($"32X header at $3C0 \"{header}\", bytes at $3E0: {raw}");
			Console.WriteLine($"master SH-2 entry {master:X8}, slave SH-2 entry {slave:X8}");
			// the domain's byte order is the cartridge's: big-endian, as in the file
			var file = File.ReadAllBytes(romPath);
			uint FromFile(int a) => (uint)(file[a] << 24 | file[a + 1] << 16 | file[a + 2] << 8 | file[a + 3]);
			Assert.AreEqual(FromFile(0x3E0), master, "master entry, MD CART vs the file");
			Assert.AreEqual(FromFile(0x3E4), slave, "slave entry, MD CART vs the file");

			// 2. execute callbacks at the entry points, with the registers inside them
			var hits = new List<string>();
			var vbrs = new List<(string Cpu, ulong Vbr)>();
			void Show(string cpu, string prefix, uint addr, uint value)
			{
				var r = rig.Debuggable.GetCpuFlagsAndRegisters();
				ulong R(string name) => r[$"{prefix} {name}"].Value;
				vbrs.Add((cpu, R("VBR")));
				hits.Add($"frame {rig.Core.Frame} {cpu}: addr {addr:X8} op {value:X4} PC {R("PC"):X8} PR {R("PR"):X8} SR {R("SR"):X3} GBR {R("GBR"):X8} VBR {R("VBR"):X8} R15 {R("R15"):X8}");
			}

			var entries = new[]
			{
				rig.AddExec(Rig.Master, master, "smoke: master entry", (a, v, _) => Show("master", "SH2M", a, v)),
				rig.AddExec(Rig.Slave, slave, "smoke: slave entry", (a, v, _) => Show("slave ", "SH2S", a, v)),
			};
			rig.Frames(FRAMES);
			entries.ToList().ForEach(rig.Remove);
			Console.WriteLine($"{hits.Count} hits at the entry points in {FRAMES} frames:");
			hits.Take(MAX_PRINTED).ToList().ForEach(Console.WriteLine);
			Assert.IsTrue(vbrs.Exists(static h => h.Cpu is "master") && vbrs.Exists(static h => h.Cpu is "slave "), "both SH-2s start at their entry points");
			// PicoDrive's BIOS-less boot takes the VBRs from the header too
			Assert.AreEqual(cart.PeekUint(0x3E8, bigEndian: true), vbrs.First(static h => h.Cpu is "master").Vbr, "master VBR");
			Assert.AreEqual(cart.PeekUint(0x3EC, bigEndian: true), vbrs.First(static h => h.Cpu is "slave ").Vbr, "slave VBR");

			// 3. each CPU's instructions in one frame, one scope per frame, with address-less callbacks
			foreach (var scope in Rig.Scopes)
			{
				long n = 0;
				var callback = rig.AddExec(scope, null, "smoke: count", (_, _, _) => n++);
				rig.Frame();
				rig.Remove(callback);
				Console.WriteLine($"frame {rig.Core.Frame - 1}: {scope,-10} {n,8} instructions");
			}

			// and all three at once: each callback then counts every CPU
			var all = new long[Rig.Scopes.Length];
			var callbacks = Enumerable.Range(0, Rig.Scopes.Length).Select(i => rig.AddExec(Rig.Scopes[i], null, "smoke: count all", (_, _, _) => all[i]++)).ToList();
			rig.Frame();
			callbacks.ForEach(rig.Remove);
			Console.WriteLine($"frame {rig.Core.Frame - 1}: all three scopes at once, each callback counted {string.Join(' ', all)}");
			Assert.IsTrue(all.All(n => n == all[0]));
		}
	}
}
