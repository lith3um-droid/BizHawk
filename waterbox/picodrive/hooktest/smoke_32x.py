#!/usr/bin/env python3
"""Smoke test on a 32X cartridge, with the native harness.

Reads the master and slave SH-2 entry points from the cartridge's 32X header
($3E0 and $3E4), watches them, and prints the hits with some registers.
Then counts each CPU's instructions per frame with watch-all.

usage: smoke_32x.py [--frames N] [--harness PATH] ROM
"""

import argparse
import os
import struct
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
SH2_REGS = ['R%d' % i for i in range(16)] + ['PC', 'PR', 'SR', 'GBR', 'VBR', 'MACH', 'MACL']


def run(exe, rom, *args):
    args = [exe] + [str(a) for a in args] + [rom]
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--frames', type=int, default=300)
    ap.add_argument('--harness', default=os.path.join(HERE, 'build', 'clang', 'harness'))
    ap.add_argument('rom')
    opt = ap.parse_args()

    with open(opt.rom, 'rb') as f:
        f.seek(0x3e0)
        master, slave = struct.unpack('>II', f.read(8))
    print('32X header: master SH-2 entry %08x, slave SH-2 entry %08x' % (master, slave))

    out = run(opt.harness, opt.rom, '-32x', '-f', opt.frames,
              '-w', '1:%x' % master, '-w', '2:%x' % slave, '-log', '-')
    hits = [l.split() for l in out.splitlines() if l.startswith('H ')]
    print('%d hits at the entry points in %d frames:' % (len(hits), opt.frames))
    for h in hits[:10]:
        r = dict(zip(SH2_REGS, (int(x, 16) for x in h[5:5 + len(SH2_REGS)])))
        print('  frame %s %s: PC %08x PR %08x SR %03x GBR %08x VBR %08x R15 %08x' % (
            h[1], 'master' if h[2] == '1' else 'slave ', r['PC'], r['PR'], r['SR'],
            r['GBR'], r['VBR'], r['R15']))

    out = run(opt.harness, opt.rom, '-32x', '-f', opt.frames, '-a', 0, '-a', 1, '-a', 2, '-counts', '-')
    rows = [l.split()[1:] for l in out.splitlines() if l.startswith('C ')]
    print('instructions per frame (68000, master SH-2, slave SH-2):')
    for row in rows[:: max(1, len(rows) // 6)] + [rows[-1]]:
        print('  frame %4s: %8s %8s %8s' % tuple(row))


if __name__ == '__main__':
    main()
