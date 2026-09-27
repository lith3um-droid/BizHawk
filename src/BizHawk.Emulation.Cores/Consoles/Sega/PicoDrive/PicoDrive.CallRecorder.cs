#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using BizHawk.Common;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Waterbox;

namespace BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive
{
	public partial class PicoDrive
	{
		/// <summary>null unless the environment variable <see cref="CallRecorder.ENV_VAR"/> named a good config file</summary>
		private CallRecorder? _recorder;

		/// <summary>
		/// with a recorder, which shares the watch lists: which of each CPU's hits the execute callbacks would have had
		/// without it, so that only those go to <see cref="MemoryCallbackSystem.CallMemoryCallbacks"/>
		/// </summary>
		private readonly bool[] _callbacksWatchAll = new bool[3];

		private readonly HashSet<uint>[] _callbacksWatch = [ new(), new(), new() ];

		/// <summary>starts the call recorder if <see cref="CallRecorder.ENV_VAR"/> is set; a problem turns it off, with a message</summary>
		private void InitCallRecorder(CoreComm comm, string gameName)
		{
			var config = Environment.GetEnvironmentVariable(CallRecorder.ENV_VAR);
			if (string.IsNullOrWhiteSpace(config))
			{
				return;
			}

			if (_hooks is null)
			{
				Console.WriteLine($"Call recorder off: {CallRecorder.ENV_VAR} is set, but this picodrive.wbx.zst has no execute hooks");
				comm.ShowMessage($"Call recorder off: {CallRecorder.ENV_VAR} is set, but this picodrive.wbx.zst has no execute hooks.");
				return;
			}

			try
			{
				_recorder = CallRecorder.Create(config!, new RecorderHost(this), gameName, message =>
				{
					Console.WriteLine(message);
					comm.Notify(message, 10);
				});
			}
			catch (CallRecorderConfigException e)
			{
				Console.WriteLine($"Call recorder off: {e.Message}");
				comm.ShowMessage($"Call recorder off, because of problems in its config file ({CallRecorder.ENV_VAR}):\n\n{e.Message}");
				return;
			}

			RefreshExecWatchLists();
			var what = $"Call recorder on: {_recorder.Describe()}";
			Console.WriteLine(what);
			comm.Notify(what, 10);
		}

		private void DisposeCallRecorder()
		{
			// it writes the calls still pending, so the core must still be there
			_recorder?.Dispose();
		}

		/// <summary>the core's memory domains, and its registers and watch lists, for the recorder</summary>
		private sealed unsafe class RecorderHost : ICallRecorderHost
		{
			private readonly PicoDrive _core;

			private readonly (IntPtr Data, long Mangler)[] _areas;

			private byte[] _scratch = new byte[64];

			public RecorderHost(PicoDrive core)
			{
				_core = core;
				// straight from the memory areas, since a domain with swapped bytes reads a byte at a time
				var domains = core.ServiceProvider.GetService<IMemoryDomains>()
					.OfType<WaterboxMemoryDomain>()
					.Where(static d => !d.Definition.Flags.HasFlag(LibWaterboxCore.MemoryDomainFlags.FunctionHook))
					.ToArray();
				Domains = domains.Select(static d => (d.Name, d.Size)).ToArray();
				_areas = domains.Select(static d => (d.Definition.Data,
					d.Definition.Flags.HasFlag(LibWaterboxCore.MemoryDomainFlags.Swapped) && d.EndianType is MemoryDomain.Endian.Big
						? d.WordSize - 1L
						: 0L)).ToArray();
			}

			public int Frame => _core.Frame;

			public IReadOnlyList<(string Name, long Size)> Domains { get; }

			public void GetRegisters(int cpu, uint[] regs)
				=> _core._hooks.GetRegisters(cpu, regs);

			public void ReadDomain(int domain, long start, byte[] dest, int offset, int length)
			{
				var (data, mangler) = _areas[domain];
				using (_core._exe.EnterExit())
				{
					if (mangler is 0 || (mangler is 1 && (start & 1) is 0 && (length & 1) is 0))
					{
						Marshal.Copy(data + (int)start, dest, offset, length);
						if (mangler is 1 && length > 0)
						{
							fixed (byte* p = &dest[offset])
							{
								SwapPairs(p, length);
							}
						}

						return;
					}

					// the domain's byte at a is the area's at a ^ mangler
					var alignedStart = start & ~mangler;
					var n = (int)(((start + length + mangler) & ~mangler) - alignedStart);
					if (_scratch.Length < n)
					{
						_scratch = new byte[n];
					}

					Marshal.Copy(data + (int)alignedStart, _scratch, 0, n);
					for (var i = 0; i < length; i++)
					{
						dest[offset + i] = _scratch[(int)(((start + i) ^ mangler) - alignedStart)];
					}
				}
			}

			public void WatchListChanged(int cpu)
				=> _core.RefreshExecWatchList(cpu);

			/// <summary>swaps the bytes of each 16-bit word, 8 bytes at a time</summary>
			private static void SwapPairs(byte* p, int length)
			{
				var i = 0;
				for (; i + 8 <= length; i += 8)
				{
					var x = *(ulong*)(p + i);
					*(ulong*)(p + i) = (x & 0x00FF00FF00FF00FFUL) << 8 | (x >> 8 & 0x00FF00FF00FF00FFUL);
				}

				for (; i < length; i += 2)
				{
					(p[i], p[i + 1]) = (p[i + 1], p[i]);
				}
			}
		}
	}
}
