using System.Runtime.InteropServices;

using BizHawk.BizInvoke;
using BizHawk.Emulation.Cores.Waterbox;

namespace BizHawk.Emulation.Cores.Consoles.Sega.PicoDrive
{
	public abstract class LibPicoDrive : LibWaterboxCore
	{
		[StructLayout(LayoutKind.Sequential)]
		public new class FrameInfo : LibWaterboxCore.FrameInfo
		{
			public int Buttons;
		}

		[UnmanagedFunctionPointer(CC)]
		public delegate void CDReadCallback(int lba, IntPtr dest, bool audio);

		public enum Region : int
		{
			Auto = 0,
			JapanNTSC = 1,
			JapanPAL = 2,
			US = 4,
			Europe = 8
		}

		/// <param name="cd">If TRUE, load a CD and not a cart.</param>
		/// <param name="_32xPreinit">If TRUE, preallocate 32X data structures.  When set to false,
		///		32X games will still run, but will not have memory domains</param>
		[BizImport(CC)]
		public abstract bool Init(bool cd, bool _32xPreinit, Region regionAutoOrder, Region regionOverride);

		public const int CD_MAX_TRACKS = 100;

		[StructLayout(LayoutKind.Sequential)]
		public struct Track
		{
			public int start;
			public int end;
		}

		[StructLayout(LayoutKind.Sequential)]
		public class TOC
		{
			public int end;
			public int last;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = CD_MAX_TRACKS)]
			public readonly Track[] tracks = new Track[CD_MAX_TRACKS];
		}

		[BizImport(CC)]
		public abstract void SetCDReadCallback(CDReadCallback callback);

		[BizImport(CC)]
		public abstract bool IsPal();

		[BizImport(CC)]
		public abstract bool Is32xActive();

		/// <summary>CPU ids of <see cref="ExecCallback"/>, <see cref="LibPicoDriveHooks.SetExecWatchList"/> and <see cref="LibPicoDriveHooks.GetRegisters"/></summary>
		public enum Cpu : int
		{
			M68K = 0,
			MasterSH2 = 1,
			SlaveSH2 = 2,
		}

		/// <summary>room <see cref="LibPicoDriveHooks.GetRegisters"/> needs: 20 values for the 68000, 23 for an SH-2</summary>
		public const int MAX_REGISTERS = 23;

		/// <summary>runs before each watched instruction, which must not change emulation state</summary>
		/// <param name="cpu">a <see cref="Cpu"/></param>
		/// <param name="addr">
		/// the instruction's address: masked to 24 bits on the 68000, the full 32 bits as executed on an SH-2
		/// (cached and cache-through aliases differ, and a delay slot reports its own address)
		/// </param>
		/// <param name="opcode">the instruction's first word</param>
		[UnmanagedFunctionPointer(CC)]
		public delegate void ExecCallback(int cpu, uint addr, uint opcode);
	}

	/// <summary>
	/// PicoDrive's execute hooks and registers, bound apart from <see cref="LibPicoDrive"/>: a picodrive.wbx from
	/// before them doesn't export them, and still loads, without them
	/// </summary>
	public abstract class LibPicoDriveHooks
	{
		/// <summary>sets the execute callback; null turns every hook off but keeps the watch lists</summary>
		/// <remarks>the callback and the watch lists are left out of savestates, so loading one keeps them</remarks>
		[BizImport(LibWaterboxCore.CC)]
		public abstract void SetExecCallback(LibPicoDrive.ExecCallback callback);

		/// <summary>
		/// replaces one CPU's watch list: the callback runs before instructions at one of the first <paramref name="count"/>
		/// addresses in <paramref name="addrs"/>, or before every instruction if <paramref name="watchAll"/> is set
		/// </summary>
		/// <returns>0 if nothing is watched, 1 for a list, 2 for watch-all (also used for more than 4096 addresses), -1 for a bad cpu</returns>
		[BizImport(LibWaterboxCore.CC)]
		public abstract int SetExecWatchList(int cpu, uint[] addrs, int count, bool watchAll);

		/// <summary>
		/// writes one CPU's registers to <paramref name="regs"/>, which needs room for <see cref="LibPicoDrive.MAX_REGISTERS"/> values:
		/// D0-D7, A0-A7, PC, SR, USP, SSP for the 68000; R0-R15, PC, PR, SR, GBR, VBR, MACH, MACL for an SH-2
		/// </summary>
		/// <remarks>inside an execute callback for that CPU, PC is the reported instruction's address</remarks>
		/// <returns>how many values were written, 0 for a bad cpu</returns>
		[BizImport(LibWaterboxCore.CC)]
		public abstract int GetRegisters(int cpu, uint[] regs);
	}
}
