using System.Collections.Generic;
using System.Linq;

using BizHawk.BizInvoke;
using BizHawk.Common;
using BizHawk.Emulation.Common;

namespace BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive
{
	public partial class PicoDrive : IDebuggable
	{
		// indexed by LibPicoDrive.Cpu
		private static readonly string[] CpuScopes = [ "M68K BUS", "SH2 Master", "SH2 Slave" ];

		private static readonly string[][] CpuRegisterNames =
		[
			[
				.. Enumerable.Range(0, 8).Select(static i => $"M68K D{i}"),
				.. Enumerable.Range(0, 8).Select(static i => $"M68K A{i}"),
				"M68K PC", "M68K SR", "M68K USP", "M68K SSP",
			],
			SH2RegisterNames("SH2M"),
			SH2RegisterNames("SH2S"),
		];

		private static string[] SH2RegisterNames(string cpu)
			=> [
				.. Enumerable.Range(0, 16).Select(i => $"{cpu} R{i}"),
				.. new[] { "PC", "PR", "SR", "GBR", "VBR", "MACH", "MACL" }.Select(r => $"{cpu} {r}"),
			];

		private readonly MemoryCallbackSystem _memoryCallbacks = new([ .. CpuScopes ]);

		private readonly LibPicoDrive.ExecCallback _execCallback;

		/// <summary>null if the core file predates the hooks: then there are no memory callbacks or registers</summary>
		private readonly LibPicoDrive.Hooks _hooks;

		/// <summary>
		/// the execute callbacks of each CPU's scope, kept from <see cref="MemoryCallbackSystem.CallbackAdded"/> and
		/// <see cref="MemoryCallbackSystem.CallbackRemoved"/>, which can't enumerate the callback system while it changes
		/// </summary>
		private readonly List<IMemoryCallback>[] _execCallbacks = [ new(), new(), new() ];

		public IDictionary<string, RegisterValue> GetCpuFlagsAndRegisters()
		{
			if (_hooks is null)
			{
				throw new NotImplementedException();
			}

			// all three CPUs, even without the 32X (the SH-2s are then all zeros), so the names are always the same
			var ret = new Dictionary<string, RegisterValue>();
			var regs = new uint[LibPicoDrive.MAX_REGISTERS];
			for (var cpu = 0; cpu < CpuRegisterNames.Length; cpu++)
			{
				var names = CpuRegisterNames[cpu];
				var n = _hooks.GetRegisters(cpu, regs);
				if (n != names.Length)
				{
					throw new InvalidOperationException($"The core returned {n} registers for {CpuScopes[cpu]}, not {names.Length}");
				}

				for (var i = 0; i < n; i++)
				{
					ret[names[i]] = new RegisterValue(regs[i], names[i] is "M68K SR" ? (byte)16 : (byte)32);
				}
			}

			return ret;
		}

		[FeatureNotImplemented]
		public void SetCpuRegister(string register, int value)
			=> throw new NotImplementedException();

#pragma warning disable CA1065 // like GPGX's, a conditional [FeatureNotImplemented], for which the convention is to throw NIE
		public IMemoryCallbackSystem MemoryCallbacks => _hooks is null ? throw new NotImplementedException() : _memoryCallbacks;
#pragma warning restore CA1065

		public bool CanStep(StepType type) => false;

		[FeatureNotImplemented]
		public void Step(StepType type) => throw new NotImplementedException();

		[FeatureNotImplemented]
#pragma warning disable CA1065 // convention for [FeatureNotImplemented] is to throw NIE
		public long TotalExecutedCycles => throw new NotImplementedException();
#pragma warning restore CA1065

		private void ExecHook(int cpu, uint addr, uint opcode)
		{
			if (_memoryCallbacks.HasExecutes)
			{
				const uint flags = (uint)MemoryCallbackFlags.AccessExecute;
				_memoryCallbacks.CallMemoryCallbacks(addr, opcode, flags, CpuScopes[cpu]);
			}
		}

		/// <returns>the hook exports, or null if the core file predates them</returns>
		private LibPicoDrive.Hooks BindHooks()
		{
			using (_exe.EnterExit())
			{
				return _exe.GetProcAddrOrZero(nameof(LibPicoDrive.Hooks.GetRegisters)) == IntPtr.Zero
					? null
					: BizInvoker.GetInvoker<LibPicoDrive.Hooks>(_exe, _exe, _adapter);
			}
		}

		/// <remarks>
		/// The callback and the watch lists are invisible to savestates, so they are set once, here and when callbacks change,
		/// and loading a state keeps them.
		/// </remarks>
		private void InitExecHooks()
		{
			if (_hooks is null)
			{
				return;
			}

			_memoryCallbacks.CallbackAdded += OnMemoryCallbackAdded;
			_memoryCallbacks.CallbackRemoved += OnMemoryCallbackRemoved;
			_memoryCallbacks.ActiveChanged += RefreshExecWatchLists;
			_hooks.SetExecCallback(_execCallback);
			RefreshExecWatchLists();
		}

		private void OnMemoryCallbackAdded(IMemoryCallback callback)
		{
			var cpu = Array.IndexOf(CpuScopes, callback.Scope);
			if (callback.Type is MemoryCallbackType.Execute && cpu >= 0)
			{
				_execCallbacks[cpu].Add(callback);
				RefreshExecWatchList(cpu);
			}
		}

		private void OnMemoryCallbackRemoved(IMemoryCallback callback)
		{
			var cpu = Array.IndexOf(CpuScopes, callback.Scope);
			if (callback.Type is MemoryCallbackType.Execute && cpu >= 0 && _execCallbacks[cpu].Remove(callback))
			{
				RefreshExecWatchList(cpu);
			}
		}

		private void RefreshExecWatchLists()
		{
			for (var cpu = 0; cpu < CpuScopes.Length; cpu++)
			{
				RefreshExecWatchList(cpu);
			}
		}

		/// <summary>
		/// Sends one CPU's execute callbacks to the core: their exact addresses if every one has an address and a full mask,
		/// or else every instruction, which leaves the matching to <see cref="MemoryCallbackSystem.CallMemoryCallbacks"/>.
		/// </summary>
		private void RefreshExecWatchList(int cpu)
		{
			// the core reports the 68000's addresses in 24 bits
			var fullMask = cpu == (int)LibPicoDrive.Cpu.M68K ? 0xFFFFFFU : 0xFFFFFFFFU;
			var callbacks = _execCallbacks[cpu];
			var addrs = new uint[callbacks.Count];
			for (var i = 0; i < addrs.Length; i++)
			{
				var callback = callbacks[i];
				if (callback.Address is not uint addr || ((callback.AddressMask ?? uint.MaxValue) & fullMask) != fullMask)
				{
					_hooks.SetExecWatchList(cpu, null, 0, watchAll: true);
					return;
				}

				addrs[i] = addr & fullMask;
			}

			_hooks.SetExecWatchList(cpu, addrs, addrs.Length, watchAll: false);
		}
	}
}
