#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive
{
	/// <summary>What <see cref="CallRecorder"/> reads from the core, and tells it.</summary>
	internal interface ICallRecorderHost
	{
		/// <summary>the frame being emulated, or between frames the next one</summary>
		int Frame { get; }

		/// <summary>the memory domains the recorder can read, by index</summary>
		IReadOnlyList<(string Name, long Size)> Domains { get; }

		/// <summary>as <see cref="LibPicoDriveHooks.GetRegisters"/></summary>
		void GetRegisters(int cpu, uint[] regs);

		/// <summary>copies <paramref name="length"/> bytes of a domain from <paramref name="start"/>, in the domain's byte order</summary>
		void ReadDomain(int domain, long start, byte[] dest, int offset, int length);

		/// <summary>the recorder's watch list for <paramref name="cpu"/> changed</summary>
		void WatchListChanged(int cpu);
	}

	/// <summary>the config file has problems, one per line</summary>
	internal sealed class CallRecorderConfigException(string message) : Exception(message);

	/// <summary>
	/// Records calls of chosen routines: the CPU's registers and chosen memory on entry and on return, one file per routine.
	/// It also counts call targets (discover mode). It is on when the environment variable <see cref="ENV_VAR"/> names a
	/// config file. docs/32x-hooks.md, section "Call recorder", describes the config file, the output and the rules.
	/// </summary>
	internal sealed class CallRecorder : IDisposable
	{
		public const string ENV_VAR = "PICODRIVE_CALL_RECORDER";

		public const int DEFAULT_MAX_CALLS = 1000;

		/// <summary>pending calls per CPU; one more, and the oldest is written as abandoned</summary>
		public const int MAX_PENDING = 256;

		public const uint FLAG_RETURNED = 1;
		public const uint FLAG_ABANDONED = 2;
		public const uint FLAG_TRUNCATED = 4;

		public const int FORMAT_VERSION = 1;

		/// <summary>the config's names of <see cref="LibPicoDrive.Cpu"/></summary>
		public static readonly string[] CpuNames = [ "m68k", "sh2m", "sh2s" ];

		/// <summary>how many of <see cref="LibPicoDriveHooks.GetRegisters"/>'s values each CPU has</summary>
		public static readonly int[] RegisterCounts = [ 20, 23, 23 ];

		// register indexes: A7 on the 68000, R15 on an SH-2
		private const int SP = 15;
		private const int M68K_SR = 17;
		private const int M68K_USP = 18;
		private const int M68K_SSP = 19;
		private const int SH2_PR = 17;

		private sealed class Region(string domainName, int domain, long start, int length, string label)
		{
			public readonly string DomainName = domainName;
			public readonly int Domain = domain;
			public readonly long Start = start;
			public readonly int Length = length;
			public readonly string Label = label;
		}

		private sealed class Routine(int cpu, uint address, string name, Region[] regions)
		{
			public readonly int Cpu = cpu;
			public readonly uint Address = address;
			public readonly string Name = name;
			public readonly Region[] Regions = regions;
			public readonly int RegionBytes = regions.Sum(static r => r.Length);
			public BinaryWriter? Writer;
			public int Calls, Returned, Abandoned, Truncated;
			public int FirstFrame = -1, LastFrame = -1;
			private readonly Stack<byte[]> _free = new();

			public string FileName => Name + ".pdcr";

			public byte[] TakeBuffer() => _free.Count > 0 ? _free.Pop() : new byte[RegionBytes];

			public void GiveBuffer(byte[] buf) => _free.Push(buf);
		}

		private sealed class Pending(Routine routine, int frame, uint seq, uint[] entryRegs, byte[] entryBytes)
		{
			public readonly Routine Routine = routine;
			public readonly int Frame = frame;
			public readonly uint Seq = seq;
			public readonly uint[] EntryRegs = entryRegs;
			public readonly byte[] EntryBytes = entryBytes;

			/// <summary>false if the 68000's stack isn't in RAM: then the call can't return, only be abandoned or truncated</summary>
			public bool HasReturn;
			public uint ReturnAddress;

			/// <summary>the stack pointer at the return: A7 + 4 on the 68000, R15 on an SH-2</summary>
			public uint ReturnSp;

			/// <summary>the stack pointer at entry; past it, the call is abandoned</summary>
			public uint EntrySp;

			/// <summary>68000: entered in supervisor mode, so <see cref="EntrySp"/> is compared with SSP, else with USP</summary>
			public bool Super;
		}

		/// <summary>discover mode on one CPU</summary>
		private sealed class Discovery(int first, int last)
		{
			public readonly int First = first;
			public readonly int Last = last;
			public int LastSeen = -1;
			public bool Active;
			public long Instructions;
			public readonly Dictionary<uint, (int Calls, HashSet<uint> Sites)> Targets = new();

			/// <summary>0: no call; 68000: 1 after a JSR or BSR; SH-2: 1 after a call, 2 after its delay slot</summary>
			public int Stage;
			public uint CallAddress, CallLength, SpBefore;
		}

		private sealed class CpuState(int cpu)
		{
			public readonly int Cpu = cpu;

			/// <summary>the routines still taking calls, while recording</summary>
			public readonly Dictionary<uint, Routine> Entries = new();

			/// <summary>return addresses seen while recording; they stay in the watch list, which then changes seldom</summary>
			public readonly HashSet<uint> ReturnWatch = new();

			/// <summary>the pending calls, innermost last</summary>
			public readonly List<Pending> Pending = new();

			/// <summary>how many pending calls return to each address</summary>
			public readonly Dictionary<uint, int> PendingReturns = new();

			public Discovery? Discovery;
		}

		private readonly ICallRecorderHost _host;
		private readonly Action<string> _report;
		private readonly string _configPath;
		private readonly string _gameName;
		private readonly string _outDir;
		private readonly Routine[] _routines;
		private readonly int _first, _last, _maxCalls;
		private readonly CpuState[] _cpus = [ new(0), new(1), new(2) ];
		private readonly int _ram68K;
		private readonly uint[] _regs = new uint[LibPicoDrive.MAX_REGISTERS];
		private readonly byte[] _long = new byte[4];
		private readonly byte[] _exitBuffer;
		private bool _regsFresh;
		private bool _recording;
		private bool _failed;
		private bool _disposed;
		private uint _seq;

		public string OutDir => _outDir;

		public int RoutineCount => _routines.Length;

		private CallRecorder(ICallRecorderHost host, Action<string> report, string configPath, string gameName, string outDir,
			Routine[] routines, int first, int last, int maxCalls, int[] discover)
		{
			_host = host;
			_report = report;
			_configPath = configPath;
			_gameName = gameName;
			_outDir = outDir;
			_routines = routines;
			_first = first;
			_last = last;
			_maxCalls = maxCalls;
			_ram68K = host.Domains.Select(static d => d.Name).ToList().IndexOf("68K RAM");
			_exitBuffer = new byte[routines.Length > 0 ? routines.Max(static r => r.RegionBytes) : 0];
			for (var cpu = 0; cpu < _cpus.Length; cpu++)
			{
				if (discover[cpu] > 0)
				{
					_cpus[cpu].Discovery = new(first, (int)Math.Min(last, first + (long)discover[cpu] - 1));
				}
			}
		}

		/// <summary>reads the config file and creates the output files</summary>
		/// <param name="report">gets messages about the recording, e.g. that it stopped on an error</param>
		/// <exception cref="CallRecorderConfigException">every problem with the config file, or with the output folder</exception>
		public static CallRecorder Create(string configPath, ICallRecorderHost host, string gameName, Action<string> report)
		{
			var errors = new List<string>();
			string[] lines;
			try
			{
				configPath = Path.GetFullPath(configPath);
				lines = File.ReadAllLines(configPath);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				throw new CallRecorderConfigException($"can't read the config file {configPath}: {e.Message}");
			}

			var domains = host.Domains;
			var specs = new List<(int Line, int Cpu, uint Address, string Name, string[]? Labels)>();
			var regions = new List<Region>();
			int first = 0, last = int.MaxValue, maxCalls = DEFAULT_MAX_CALLS;
			var discover = new int[3];
			string? outDir = null;
			for (var n = 1; n <= lines.Length; n++)
			{
				var line = lines[n - 1];
				var hash = line.IndexOf('#');
				if (hash >= 0)
				{
					line = line.Substring(0, hash);
				}

				var t = Tokenize(line);
				if (t.Count is 0)
				{
					continue;
				}

				void Error(string message) => errors.Add($"line {n}: {message}");
				switch (t[0].ToLowerInvariant())
				{
					case "routine":
					{
						int cpu;
						if (t.Count < 3 || (cpu = ParseCpu(t[1])) < 0 || !TryParseNumber(t[2], out var addr))
						{
							Error("expected routine <m68k|sh2m|sh2s> <address> [name] [regions=label,...]");
							break;
						}

						if (cpu is (int)LibPicoDrive.Cpu.M68K)
						{
							addr &= 0xFFFFFF;
						}

						if ((addr & 1) != 0)
						{
							Error($"{t[2]} is odd; instructions are at even addresses");
						}

						string? name = null;
						string[]? labels = null;
						foreach (var arg in t.Skip(3))
						{
							if (arg.StartsWith("regions=", StringComparison.OrdinalIgnoreCase))
							{
								labels = arg.Substring(8).Split([ ',' ], StringSplitOptions.RemoveEmptyEntries);
							}
							else if (name is null)
							{
								name = arg;
							}
							else
							{
								Error($"unexpected \"{arg}\"");
							}
						}

						name ??= $"{CpuNames[cpu]}_{addr:X8}";
						if (name.Length is 0 || name.Any(static c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
						{
							Error($"the name \"{name}\" can have only letters, digits, '_', '-' and '.' (it names the file)");
						}

						specs.Add((n, cpu, addr, name, labels));
						break;
					}
					case "region":
					{
						// the domain's name can have spaces: it's everything before the last three words
						if (t.Count < 5 || !TryParseNumber(t[t.Count - 3], out var start) || !TryParseNumber(t[t.Count - 2], out var length))
						{
							Error("expected region <domain> <start> <length> <label>");
							break;
						}

						var domainName = string.Join(" ", t.Skip(1).Take(t.Count - 4));
						var label = t[t.Count - 1];
						var domain = domains.Select(static d => d.Name).ToList().IndexOf(domainName);
						if (domain < 0)
						{
							Error($"no memory domain \"{domainName}\"; this game has {string.Join(", ", domains.Select(static d => $"\"{d.Name}\""))}");
						}
						else if (length is 0 || start + (long)length > domains[domain].Size)
						{
							Error($"{domainName} is 0x{domains[domain].Size:X} bytes; 0x{start:X}+0x{length:X} isn't in it");
						}
						else if (regions.Exists(r => r.Label == label))
						{
							Error($"a second region \"{label}\"");
						}
						else
						{
							regions.Add(new(domainName, domain, start, (int)length, label));
						}

						break;
					}
					case "frames":
						if (t.Count != 3 || !TryParseNumber(t[1], out var f0) || !TryParseNumber(t[2], out var f1) || f0 > f1 || f1 > int.MaxValue)
						{
							Error("expected frames <first> <last>, first <= last");
							break;
						}

						first = (int)f0;
						last = (int)f1;
						break;
					case "maxcalls":
						if (t.Count != 2 || !TryParseNumber(t[1], out var max) || max is 0 or > int.MaxValue)
						{
							Error("expected maxcalls <n>, n >= 1");
							break;
						}

						maxCalls = (int)max;
						break;
					case "discover":
					{
						int cpu;
						if (t.Count != 3 || (cpu = ParseCpu(t[1])) < 0 || !TryParseNumber(t[2], out var frames) || frames is 0 or > int.MaxValue)
						{
							Error("expected discover <m68k|sh2m|sh2s> <frames>, frames >= 1");
							break;
						}

						if (discover[cpu] > 0)
						{
							Error($"a second discover line for {CpuNames[cpu]}");
						}

						discover[cpu] = (int)frames;
						break;
					}
					case "out":
						outDir = line.Trim().Substring(3).Trim().Trim('"');
						if (outDir.Length is 0)
						{
							Error("expected out <folder>");
						}

						break;
					default:
						Error($"unknown keyword \"{t[0]}\"; expected routine, region, frames, maxcalls, discover or out");
						break;
				}
			}

			var routines = new List<Routine>();
			foreach (var (line, cpu, addr, name, labels) in specs)
			{
				var mine = new List<Region>();
				foreach (var label in labels ?? regions.Select(static r => r.Label))
				{
					if (regions.Find(r => r.Label == label) is { } region)
					{
						mine.Add(region);
					}
					else
					{
						errors.Add($"line {line}: no region \"{label}\"");
					}
				}

				if (routines.Exists(r => r.Cpu == cpu && r.Address == addr))
				{
					errors.Add($"line {line}: a second routine at {CpuNames[cpu]} 0x{addr:X}");
				}

				if (routines.Exists(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
				{
					errors.Add($"line {line}: a second routine named \"{name}\"");
				}

				routines.Add(new(cpu, addr, name, [ .. mine ]));
			}

			if (routines.Count is 0 && discover.All(static d => d is 0))
			{
				errors.Add("nothing to do: no routine or discover line");
			}

			if (errors.Count > 0)
			{
				throw new CallRecorderConfigException($"{configPath}:\n{string.Join("\n", errors)}");
			}

			var dir = Path.Combine(Path.GetDirectoryName(configPath)!, outDir ?? "calls");
			var recorder = new CallRecorder(host, report, configPath, gameName, dir, [ .. routines ], first, last, maxCalls, discover);
			try
			{
				Directory.CreateDirectory(dir);
				foreach (var r in routines)
				{
					r.Writer = new(new FileStream(Path.Combine(dir, r.FileName), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20));
					WriteHeader(r);
				}

				recorder.WriteIndex();
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				recorder.CloseFiles();
				throw new CallRecorderConfigException($"can't write the output folder {dir}: {e.Message}");
			}

			return recorder;
		}

		/// <summary>what it does, for a message</summary>
		public string Describe()
		{
			var what = new List<string>();
			if (_routines.Length > 0)
			{
				what.Add($"{_routines.Length} routine(s)");
			}

			what.AddRange(_cpus.Where(static s => s.Discovery is not null).Select(static s => $"discover {CpuNames[s.Cpu]}"));
			var frames = _last == int.MaxValue ? $"from frame {_first}" : $"frames {_first}-{_last}";
			return $"{string.Join(", ", what)}, {frames}, into {_outDir}";
		}

		/// <summary>whether the recorder watches every instruction of <paramref name="cpu"/></summary>
		public bool WatchesAll(int cpu)
			=> _cpus[cpu].Discovery is { Active: true };

		/// <summary>the addresses the recorder watches on <paramref name="cpu"/>, unless <see cref="WatchesAll"/></summary>
		public IEnumerable<uint> WatchList(int cpu)
			=> _cpus[cpu].Entries.Keys.Concat(_cpus[cpu].ReturnWatch);

		/// <summary>before a frame: starts and stops recording and discovery at the window's edges</summary>
		public void BeginFrame(int frame)
			=> Sync(frame, beforeFrame: true);

		/// <summary>after a frame, with the next frame's number: stops what ends with it</summary>
		public void EndFrame(int next)
			=> Sync(next, beforeFrame: false);

		/// <summary>a savestate was loaded: the pending calls won't return, and are written as truncated</summary>
		public void StateLoaded()
		{
			foreach (var s in _cpus)
			{
				Truncate(s);
				if (s.Discovery is { } d)
				{
					d.Stage = 0;
				}
			}
		}

		private void Sync(int frame, bool beforeFrame)
		{
			if (_disposed)
			{
				return;
			}

			var record = !_failed && _routines.Length > 0 && frame >= _first && frame <= _last;
			if (record && !_recording && beforeFrame)
			{
				StartRecording();
			}
			else if (!record && _recording)
			{
				StopRecording();
			}

			foreach (var s in _cpus)
			{
				if (s.Discovery is not { } d)
				{
					continue;
				}

				var on = frame >= d.First && frame <= d.Last;
				if (on && beforeFrame)
				{
					d.LastSeen = frame;
				}

				if (on != d.Active && (beforeFrame || !on))
				{
					d.Active = on;
					d.Stage = 0;
					if (!on)
					{
						WriteDiscovery(s.Cpu, d);
					}

					_host.WatchListChanged(s.Cpu);
				}
			}
		}

		private void StartRecording()
		{
			_recording = true;
			foreach (var s in _cpus)
			{
				foreach (var r in _routines.Where(r => r.Cpu == s.Cpu && r.Calls < _maxCalls))
				{
					s.Entries[r.Address] = r;
				}

				_host.WatchListChanged(s.Cpu);
			}
		}

		private void StopRecording()
		{
			_recording = false;
			foreach (var s in _cpus)
			{
				Truncate(s);
				s.Entries.Clear();
				s.ReturnWatch.Clear();
				_host.WatchListChanged(s.Cpu);
			}

			Flush();
			WriteIndex();
		}

		/// <summary>writes the pending calls as truncated, with the registers and memory as they are now</summary>
		private void Truncate(CpuState s)
		{
			if (s.Pending.Count is 0)
			{
				return;
			}

			_host.GetRegisters(s.Cpu, _regs);
			while (s.Pending.Count > 0)
			{
				Finish(s, s.Pending.Count - 1, FLAG_TRUNCATED, _regs);
			}
		}

		/// <summary>runs before each instruction the core reports on <paramref name="cpu"/>, and ignores the ones that aren't its own</summary>
		public void OnExec(int cpu, uint addr, uint opcode)
		{
			var s = _cpus[cpu];
			_regsFresh = false;
			var discovering = s.Discovery is { Active: true };
			if (discovering)
			{
				Discover(s, s.Discovery!, addr, opcode);
			}

			if (!_recording)
			{
				return;
			}

			var isEntry = s.Entries.TryGetValue(addr, out var routine);
			if (!isEntry && (s.Pending.Count is 0 || !(discovering || s.ReturnWatch.Contains(addr))))
			{
				return;
			}

			var regs = Registers(cpu);
			if (s.Pending.Count > 0)
			{
				if (s.PendingReturns.ContainsKey(addr))
				{
					Return(s, addr, regs);
				}

				AbandonRisen(s, regs);
			}

			if (isEntry)
			{
				Enter(s, routine!, regs);
			}
		}

		private uint[] Registers(int cpu)
		{
			if (!_regsFresh)
			{
				_host.GetRegisters(cpu, _regs);
				_regsFresh = true;
			}

			return _regs;
		}

		private void Enter(CpuState s, Routine r, uint[] regs)
		{
			var changed = false;
			if (s.Pending.Count >= MAX_PENDING)
			{
				Finish(s, 0, FLAG_ABANDONED, regs);
			}

			var frame = _host.Frame;
			var p = new Pending(r, frame, _seq++, (uint[])regs.Clone(), Capture(r, r.TakeBuffer()));
			if (s.Cpu is (int)LibPicoDrive.Cpu.M68K)
			{
				// JSR and BSR push the return address; RTS pops it
				var a7 = regs[SP];
				p.EntrySp = a7;
				p.ReturnSp = a7 + 4;
				p.Super = (regs[M68K_SR] & 0x2000) != 0;
				p.HasReturn = TryReadLong68K(a7, out var ret);
				p.ReturnAddress = ret & 0xFFFFFF;
			}
			else
			{
				// BSR, BSRF and JSR set PR; RTS jumps to it
				p.EntrySp = p.ReturnSp = regs[SP];
				p.ReturnAddress = regs[SH2_PR];
				p.HasReturn = true;
			}

			s.Pending.Add(p);
			if (p.HasReturn)
			{
				s.PendingReturns[p.ReturnAddress] = s.PendingReturns.TryGetValue(p.ReturnAddress, out var n) ? n + 1 : 1;
				changed |= s.ReturnWatch.Add(p.ReturnAddress);
			}

			if (r.FirstFrame < 0)
			{
				r.FirstFrame = frame;
			}

			r.LastFrame = frame;
			if (++r.Calls >= _maxCalls)
			{
				changed |= s.Entries.Remove(r.Address);
			}

			if (changed)
			{
				_host.WatchListChanged(s.Cpu);
			}
		}

		private static bool Returns(Pending p, uint addr, uint sp)
			=> p.HasReturn && p.ReturnAddress == addr && p.ReturnSp == sp;

		/// <summary>the innermost pending call this is the return of returns; the ones inside it are abandoned</summary>
		private void Return(CpuState s, uint addr, uint[] regs)
		{
			var sp = regs[SP];
			var k = s.Pending.Count - 1;
			while (k >= 0 && !Returns(s.Pending[k], addr, sp))
			{
				k--;
			}

			if (k < 0)
			{
				return;
			}

			while (s.Pending.Count - 1 > k)
			{
				Finish(s, s.Pending.Count - 1, FLAG_ABANDONED, regs);
			}

			// a routine entered by a jump (a tail call) returns with the one that jumped to it
			do
			{
				Finish(s, s.Pending.Count - 1, FLAG_RETURNED, regs);
			}
			while (s.Pending.Count > 0 && Returns(s.Pending[s.Pending.Count - 1], addr, sp));
		}

		/// <summary>abandons the pending calls whose stack pointer rose past its value at entry</summary>
		private void AbandonRisen(CpuState s, uint[] regs)
		{
			for (var k = s.Pending.Count - 1; k >= 0; k--)
			{
				var p = s.Pending[k];
				// on the 68000, the stack pointer of the mode the call was made in: an interrupt switches to SSP
				var sp = s.Cpu is (int)LibPicoDrive.Cpu.M68K ? regs[p.Super ? M68K_SSP : M68K_USP] : regs[SP];
				if (sp > p.EntrySp)
				{
					Finish(s, k, FLAG_ABANDONED, regs);
				}
			}
		}

		/// <summary>writes a pending call, with the registers <paramref name="regs"/> and the memory as it is now</summary>
		private void Finish(CpuState s, int k, uint flags, uint[] regs)
		{
			var p = s.Pending[k];
			s.Pending.RemoveAt(k);
			if (p.HasReturn && s.PendingReturns.TryGetValue(p.ReturnAddress, out var n))
			{
				if (n > 1)
				{
					s.PendingReturns[p.ReturnAddress] = n - 1;
				}
				else
				{
					s.PendingReturns.Remove(p.ReturnAddress);
				}
			}

			var r = p.Routine;
			switch (flags)
			{
				case FLAG_RETURNED:
					r.Returned++;
					break;
				case FLAG_ABANDONED:
					r.Abandoned++;
					break;
				default:
					r.Truncated++;
					break;
			}

			var exit = Capture(r, _exitBuffer);
			if (!_failed && r.Writer is { } w)
			{
				try
				{
					w.Write((uint)p.Frame);
					w.Write(p.Seq);
					w.Write(flags);
					var count = RegisterCounts[r.Cpu];
					for (var i = 0; i < count; i++)
					{
						w.Write(p.EntryRegs[i]);
					}

					for (var i = 0; i < count; i++)
					{
						w.Write(regs[i]);
					}

					w.Write(p.EntryBytes, 0, r.RegionBytes);
					w.Write(exit, 0, r.RegionBytes);
				}
				catch (IOException e)
				{
					// stops at the next frame
					_failed = true;
					_report($"Call recorder stopped: can't write {r.FileName}: {e.Message}");
				}
			}

			r.GiveBuffer(p.EntryBytes);
			if (s.Pending.Count is 0 && s.Entries.Count is 0 && s.ReturnWatch.Count > 0)
			{
				// every routine of this CPU is at maxcalls: stop watching it
				s.ReturnWatch.Clear();
				_host.WatchListChanged(s.Cpu);
			}
		}

		private byte[] Capture(Routine r, byte[] buf)
		{
			var offset = 0;
			foreach (var region in r.Regions)
			{
				_host.ReadDomain(region.Domain, region.Start, buf, offset, region.Length);
				offset += region.Length;
			}

			return buf;
		}

		/// <summary>reads a long from 68000 RAM, as the 68000 sees it</summary>
		/// <returns>false if <paramref name="addr"/> isn't in RAM ($E00000-$FFFFFF)</returns>
		private bool TryReadLong68K(uint addr, out uint value)
		{
			value = 0;
			if ((addr & 0xE00000) != 0xE00000 || _ram68K < 0)
			{
				return false;
			}

			for (var i = 0; i < 4; i++)
			{
				_host.ReadDomain(_ram68K, (addr + i) & 0xFFFF, _long, i, 1);
			}

			value = (uint)(_long[0] << 24 | _long[1] << 16 | _long[2] << 8 | _long[3]);
			return true;
		}

		/// <returns>the length of a 68000 JSR or BSR, or 0 if <paramref name="op"/> is neither</returns>
		private static uint CallLength68K(uint op)
		{
			if ((op & 0xFF00) == 0x6100)
			{
				return (op & 0xFF) is 0 ? 4U : 2U; // BSR.W, BSR.S
			}

			if ((op & 0xFFC0) != 0x4E80)
			{
				return 0;
			}

			return (op >> 3 & 7) switch // JSR <ea>
			{
				2 => 2, // (An)
				5 or 6 => 4, // d16(An), d8(An,Xn)
				7 => (op & 7) switch
				{
					0 or 2 or 3 => 4, // abs.W, d16(PC), d8(PC,Xn)
					1 => 6, // abs.L
					_ => 0,
				},
				_ => 0,
			};
		}

		/// <returns>whether <paramref name="op"/> is an SH-2 BSR, BSRF or JSR</returns>
		private static bool IsCallSH2(uint op)
			=> (op & 0xF000) == 0xB000 || (op & 0xF0FF) == 0x0003 || (op & 0xF0FF) == 0x400B;

		private void Discover(CpuState s, Discovery d, uint addr, uint opcode)
		{
			d.Instructions++;
			if (s.Cpu is (int)LibPicoDrive.Cpu.M68K)
			{
				// the target is the next instruction, if A7 dropped by 4 and the long there is the address after the call
				if (d.Stage is 1)
				{
					d.Stage = 0;
					var a7 = Registers(s.Cpu)[SP];
					if (a7 == d.SpBefore - 4 && TryReadLong68K(a7, out var ret) && ((ret ^ (d.CallAddress + d.CallLength)) & 0xFFFFFF) is 0)
					{
						Count(d, addr, d.CallAddress);
					}
				}

				var length = CallLength68K(opcode);
				if (length > 0)
				{
					d.Stage = 1;
					d.CallAddress = addr;
					d.CallLength = length;
					d.SpBefore = Registers(s.Cpu)[SP];
				}
			}
			else
			{
				// the target is the instruction after the delay slot, if PR is the call's address + 4
				switch (d.Stage)
				{
					case 1:
						d.Stage = addr == d.CallAddress + 2 ? 2 : 0;
						return;
					case 2:
						d.Stage = 0;
						if (Registers(s.Cpu)[SH2_PR] == d.CallAddress + 4)
						{
							Count(d, addr, d.CallAddress);
						}

						break;
				}

				if (IsCallSH2(opcode))
				{
					d.Stage = 1;
					d.CallAddress = addr;
				}
			}
		}

		private static void Count(Discovery d, uint target, uint site)
		{
			if (!d.Targets.TryGetValue(target, out var t))
			{
				t = (0, new());
			}

			t.Sites.Add(site);
			d.Targets[target] = (t.Calls + 1, t.Sites);
		}

		private void WriteDiscovery(int cpu, Discovery d)
		{
			var sb = new StringBuilder();
			var calls = d.Targets.Values.Sum(static t => (long)t.Calls);
			sb.Append($"# PicoDrive call recorder, discover {CpuNames[cpu]}: frames {d.First}-{d.LastSeen}, ")
				.AppendLine($"{d.Instructions} instructions, {calls} calls to {d.Targets.Count} targets");
			sb.AppendLine(cpu is (int)LibPicoDrive.Cpu.M68K
				? "# a JSR or BSR counts if the next instruction runs with A7 4 lower and the long at A7 the address after the call"
				: "# a BSR, BSRF or JSR counts if the instruction after its delay slot runs with PR = the call's address + 4");
			sb.AppendLine("# target        calls   sites");
			foreach (var kv in d.Targets.OrderByDescending(static kv => kv.Value.Calls).ThenBy(static kv => kv.Key))
			{
				sb.AppendLine($"0x{kv.Key:X8} {kv.Value.Calls,9} {kv.Value.Sites.Count,7}");
			}

			try
			{
				File.WriteAllText(Path.Combine(_outDir, $"discover-{CpuNames[cpu]}.txt"), sb.ToString());
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				_report($"Call recorder: can't write discover-{CpuNames[cpu]}.txt: {e.Message}");
			}

			WriteIndex();
		}

		private static void WriteHeader(Routine r)
		{
			var w = r.Writer!;
			w.Write(Encoding.ASCII.GetBytes("PDCR"));
			w.Write((uint)FORMAT_VERSION);
			w.Write((uint)r.Cpu);
			w.Write(r.Address);
			WriteString(w, r.Name);
			w.Write((uint)RegisterCounts[r.Cpu]);
			w.Write((uint)r.Regions.Length);
			foreach (var region in r.Regions)
			{
				WriteString(w, region.DomainName);
				w.Write((uint)region.Start);
				w.Write((uint)region.Length);
				WriteString(w, region.Label);
			}
		}

		/// <summary>a UTF-8 string after its length in bytes, as a 16-bit value</summary>
		private static void WriteString(BinaryWriter w, string s)
		{
			var bytes = Encoding.UTF8.GetBytes(s);
			w.Write((ushort)bytes.Length);
			w.Write(bytes);
		}

		private void Flush()
		{
			if (_failed)
			{
				return;
			}

			try
			{
				foreach (var r in _routines)
				{
					r.Writer?.Flush();
				}
			}
			catch (IOException e)
			{
				_failed = true;
				_report($"Call recorder stopped: {e.Message}");
			}
		}

		private void WriteIndex()
		{
			var o = new JObject
			{
				["format"] = "PDCR",
				["version"] = FORMAT_VERSION,
				["config"] = _configPath,
				["game"] = _gameName,
				["frames"] = new JObject { ["first"] = _first, ["last"] = _last == int.MaxValue ? JValue.CreateNull() : _last },
				["maxcalls"] = _maxCalls,
				["recording"] = _recording,
				["failed"] = _failed,
				["routines"] = new JArray(_routines.Select(r => new JObject
				{
					["name"] = r.Name,
					["file"] = r.FileName,
					["cpu"] = CpuNames[r.Cpu],
					["address"] = $"0x{r.Address:X8}",
					["registers"] = RegisterCounts[r.Cpu],
					["regions"] = new JArray(r.Regions.Select(static g => g.Label)),
					["recordSize"] = 12 + 8 * RegisterCounts[r.Cpu] + 2 * r.RegionBytes,
					["calls"] = r.Returned + r.Abandoned + r.Truncated,
					["returned"] = r.Returned,
					["abandoned"] = r.Abandoned,
					["truncated"] = r.Truncated,
					["pending"] = r.Calls - r.Returned - r.Abandoned - r.Truncated,
					["maxcallsReached"] = r.Calls >= _maxCalls,
					["firstFrame"] = r.FirstFrame,
					["lastFrame"] = r.LastFrame,
				})),
				["discover"] = new JArray(_cpus.Where(static s => s.Discovery is not null).Select(s => new JObject
				{
					["cpu"] = CpuNames[s.Cpu],
					["file"] = $"discover-{CpuNames[s.Cpu]}.txt",
					["first"] = s.Discovery!.First,
					["last"] = s.Discovery.Last,
					["lastSeen"] = s.Discovery.LastSeen,
					["active"] = s.Discovery.Active,
					["instructions"] = s.Discovery.Instructions,
					["calls"] = s.Discovery.Targets.Values.Sum(static t => (long)t.Calls),
					["targets"] = s.Discovery.Targets.Count,
				})),
			};
			try
			{
				File.WriteAllText(Path.Combine(_outDir, "index.json"), o.ToString(Formatting.Indented));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				_report($"Call recorder: can't write index.json: {e.Message}");
			}
		}

		private void CloseFiles()
		{
			foreach (var r in _routines)
			{
				try
				{
					r.Writer?.Dispose();
				}
				catch (IOException)
				{
					// already reported, or nothing more to do
				}

				r.Writer = null;
			}
		}

		/// <summary>writes the pending calls as truncated, a discovery in progress, and index.json, and closes the files</summary>
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			if (_recording)
			{
				StopRecording();
			}

			foreach (var s in _cpus)
			{
				if (s.Discovery is { Active: true } d)
				{
					d.Active = false;
					WriteDiscovery(s.Cpu, d);
				}
			}

			CloseFiles();
			WriteIndex();
			_disposed = true;
		}

		private static int ParseCpu(string s)
			=> Array.IndexOf(CpuNames, s.ToLowerInvariant());

		/// <summary>hex as 0x... or $..., otherwise decimal</summary>
		private static bool TryParseNumber(string s, out uint value)
		{
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("$", StringComparison.Ordinal))
			{
				return uint.TryParse(s.Substring(s[0] is '$' ? 1 : 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
			}

			return uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
		}

		/// <summary>words separated by white space; "a quoted string" is one word</summary>
		private static List<string> Tokenize(string line)
		{
			var ret = new List<string>();
			var i = 0;
			while (i < line.Length)
			{
				if (char.IsWhiteSpace(line[i]))
				{
					i++;
				}
				else if (line[i] is '"')
				{
					var end = line.IndexOf('"', i + 1);
					if (end < 0)
					{
						end = line.Length;
					}

					ret.Add(line.Substring(i + 1, end - i - 1));
					i = end + 1;
				}
				else
				{
					var start = i;
					while (i < line.Length && !char.IsWhiteSpace(line[i]))
					{
						i++;
					}

					ret.Add(line.Substring(start, i - start));
				}
			}

			return ret;
		}
	}
}
