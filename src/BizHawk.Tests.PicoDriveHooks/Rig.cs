using System.Collections.Generic;
using System.IO;
using System.Linq;

using BizHawk.Common;
using BizHawk.Common.PathExtensions;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive;

using Newtonsoft.Json.Linq;

namespace BizHawk.Tests.PicoDriveHooks
{
	/// <summary>Paths from the environment, which <c>run_tests.sh</c> sets.</summary>
	internal static class TestEnv
	{
		/// <summary>the ROMs and label files <c>waterbox/picodrive/hooktest/mkroms.py</c> writes</summary>
		public static string RomDir => Required("PICODRIVE_HOOKS_ROMS");

		/// <summary>the branch's <c>picodrive.wbx.zst</c></summary>
		public static string HookedCore => Required("PICODRIVE_HOOKS_WBX");

		/// <summary>an unmodified core built with the same toolchain</summary>
		public static string? BaselineCore => Optional("PICODRIVE_BASELINE_WBX");

		/// <summary>the core BizHawk 2.11.1 ships</summary>
		public static string? StockCore => Optional("PICODRIVE_STOCK_WBX");

		/// <summary>the native harness's <c>-counts</c> output for the 32X test ROM</summary>
		public static string? NativeCounts => Optional("PICODRIVE_HOOKS_NATIVE_COUNTS");

		/// <summary>a 32X cartridge for the opt-in smoke test</summary>
		public static string? SmokeRom => Optional("BIZHAWK_32X_SMOKE_ROM");

		public static string Need(string? value, string name)
			=> value ?? throw new AssertInconclusiveException($"{name} isn't set");

		private static string? Optional(string name)
			=> Environment.GetEnvironmentVariable(name) is { Length: > 0 } s ? s : null;

		private static string Required(string name)
			=> Optional(name) ?? throw new AssertInconclusiveException($"{name} isn't set; run the tests with run_tests.sh");
	}

	/// <summary>A PicoDrive instance loaded from a given core file, as EmuHawk loads one, without firmware.</summary>
	internal sealed class Rig : IDisposable
	{
		public const string M68K = "M68K BUS";
		public const string Master = "SH2 Master";
		public const string Slave = "SH2 Slave";

		public static readonly string[] Scopes = [ M68K, Master, Slave ];

		public readonly PicoDrive Core;
		public readonly IDebuggable Debuggable;
		public readonly IMemoryDomains Domains;

		private Rig(PicoDrive core)
		{
			Core = core;
			Debuggable = core.ServiceProvider.GetService<IDebuggable>()!;
			Domains = core.ServiceProvider.GetService<IMemoryDomains>()!;
		}

		public IMemoryCallbackSystem Callbacks => Debuggable.MemoryCallbacks;

		/// <param name="corePath">a <c>picodrive.wbx.zst</c>, copied to the dll folder the core loads from</param>
		/// <param name="preinit32X">the gamedb option <c>32X</c>, with which the 32X memory domains exist from the start</param>
		/// <param name="system">the game's system; by default <c>32X</c> for a <c>.32x</c> file and <c>GEN</c> otherwise, as EmuHawk sets it</param>
		/// <param name="messages">gets the core's message boxes and on-screen messages</param>
		public static Rig Load(string corePath, string romPath, bool preinit32X = true, string? system = null, Action<string>? messages = null)
		{
			var dest = Path.Combine(PathUtils.DllDirectoryPath, "picodrive.wbx.zst");
			if (Path.GetFullPath(corePath) != Path.GetFullPath(dest))
			{
				File.Copy(corePath, dest, overwrite: true);
			}

			var is32X = romPath.EndsWith(".32x", StringComparison.OrdinalIgnoreCase);
			var game = new GameInfo
			{
				Name = Path.GetFileNameWithoutExtension(romPath),
				System = system ?? (is32X ? VSystemID.Raw.Sega32X : VSystemID.Raw.GEN),
			};
			if (is32X && preinit32X)
			{
				game.AddOption("32X", "true");
			}

			var comm = new CoreComm(m => messages?.Invoke(m), (m, _) => messages?.Invoke(m), new NoFirmware(), default, null!);
			return new(new PicoDrive(comm, game, File.ReadAllBytes(romPath), deterministic: false, syncSettings: null));
		}

		public void Frame()
			=> Core.FrameAdvance(NullController.Instance, render: true, rendersound: true);

		public void Frames(int n)
		{
			for (var i = 0; i < n; i++)
			{
				Frame();
			}
		}

		public MemoryCallback AddExec(string scope, uint? addr, string name, Action<uint, uint, uint> action)
		{
			var callback = new MemoryCallback(scope, MemoryCallbackType.Execute, name, (a, v, f) =>
			{
				action(a, v, f);
				return null;
			}, addr, null);
			Callbacks.Add(callback);
			return callback;
		}

		public void Remove(MemoryCallback callback)
			=> Callbacks.Remove(callback.Callback);

		/// <summary>reads a long as the CPUs see it, from 68000 RAM or SDRAM (either alias)</summary>
		public uint PeekGuest(uint addr)
			=> (addr & 0xDFFC0000) == 0x06000000
				? Domains["32X RAM"]!.PeekUint(addr & 0x3FFFF, bigEndian: true)
				: Domains["68K RAM"]!.PeekUint(addr & 0xFFFF, bigEndian: true);

		/// <summary>a hash of the frame's video, audio and every writable memory domain, or the domains named</summary>
		public ulong FrameHash(params string[] domains)
		{
			var h = 0xCBF29CE484222325UL;
			void Add(byte b) => h = (h ^ b) * 0x100000001B3UL;
			var video = Core.GetVideoBuffer();
			for (var i = 0; i < Core.BufferWidth * Core.BufferHeight; i++)
			{
				var px = video[i];
				Add((byte)px);
				Add((byte)(px >> 8));
				Add((byte)(px >> 16));
			}

			Core.GetSamplesSync(out var samples, out var nsamp);
			for (var i = 0; i < nsamp * 2; i++)
			{
				Add((byte)samples[i]);
				Add((byte)(samples[i] >> 8));
			}

			foreach (var domain in Domains.Where(d => domains.Length is 0 ? d.Writable && d.Name is not "Waterbox PageData" : domains.Contains(d.Name)))
			{
				var buf = new byte[domain.Size];
				domain.BulkPeekByte(0L.RangeToExclusive(domain.Size), buf);
				foreach (var b in buf)
				{
					Add(b);
				}
			}

			return h;
		}

		public void Dispose()
			=> Core.Dispose();

		private sealed class NoFirmware : ICoreFileProvider
		{
			public string GetRetroSaveRAMDirectory(string corePath) => throw new NotSupportedException();

			public string GetRetroSystemPath(string corePath) => throw new NotSupportedException();

			public string GetUserPath(string sysID, bool temp) => throw new NotSupportedException();

			public byte[]? GetFirmware(FirmwareID id, string? msg = null) => null;

			public byte[] GetFirmwareOrThrow(FirmwareID id, string? msg = null)
				=> throw new MissingFirmwareException($"no firmware in these tests: {id}");

			public (byte[] FW, GameInfo Game) GetFirmwareWithGameInfoOrThrow(FirmwareID id, string? msg = null)
				=> throw new MissingFirmwareException($"no firmware in these tests: {id}");
		}
	}

	/// <summary>A test ROM and the labels <c>mkroms.py</c> wrote for it.</summary>
	internal sealed class RomInfo
	{
		public readonly string Path;

		public readonly IReadOnlyDictionary<string, uint> Labels;

		/// <summary>the variables in 68000 RAM and SDRAM</summary>
		public readonly IReadOnlyDictionary<string, uint> Vars;

		public readonly JObject Json;

		private RomInfo(string path)
		{
			Path = path;
			Json = JObject.Parse(File.ReadAllText(path + ".json"));
			Labels = Section("labels");
			Vars = new[] { "ram", "sh2vars" }.SelectMany(Section).ToDictionary(static kv => kv.Key, static kv => kv.Value);
		}

		public static RomInfo Read(string name)
			=> new(System.IO.Path.Combine(TestEnv.RomDir, name));

		public uint this[string label] => Labels[label];

		private IReadOnlyDictionary<string, uint> Section(string name)
			=> Json[name] is JObject o ? o.Properties().ToDictionary(static p => p.Name, static p => (uint)p.Value) : new Dictionary<string, uint>();
	}

	/// <summary>Counts checks and collects the failures, to report them all at once.</summary>
	internal sealed class Checker
	{
		private readonly List<string> _failures = new();

		public int Count { get; private set; }

		public void Eq(string what, ulong got, ulong want)
		{
			Count++;
			if (got != want)
			{
				_failures.Add($"{what}: got {got:X}, want {want:X}");
			}
		}

		public void Ok(string what, bool cond)
		{
			Count++;
			if (!cond)
			{
				_failures.Add(what);
			}
		}

		public void AssertAll(string title)
		{
			Console.WriteLine($"{title}: {Count} checks, {_failures.Count} failures");
			if (_failures.Count > 0)
			{
				Assert.Fail($"{_failures.Count} failures, the first ones:\n{string.Join('\n', _failures.Take(30))}");
			}
		}
	}
}
