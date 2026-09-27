#!/usr/bin/env python3
"""Reads what PicoDrive's call recorder writes (PICODRIVE_CALL_RECORDER, see
docs/32x-hooks.md, "Call recorder").

    pdcr.py summary FOLDER            index.json, every .pdcr in FOLDER, and the discover files
    pdcr.py calls FILE.pdcr [-n N]    the first N calls of a routine (default 20), one line each
    pdcr.py show FILE.pdcr CALL       one call: its registers at entry and exit, and which region
                                      bytes changed between entry and exit; CALL is the call's
                                      index in the file (negative counts from the end), or
                                      sSEQ for its sequence number

As a module: read(path) returns a Routine, whose calls() yields Call objects.

File format (all numbers little-endian):
  header   "PDCR", u32 version (1), u32 cpu (0 = 68000, 1 = master SH-2, 2 = slave SH-2),
           u32 address, str name, u32 register count, u32 region count, then per region:
           str domain, u32 start, u32 length, str label.  (str = u16 byte count + UTF-8)
  records  u32 frame, u32 sequence number, u32 flags (1 returned, 2 abandoned, 4 truncated),
           the registers at entry and at exit (u32 each), then the regions' bytes at entry
           and at exit, each region in turn, in the domain's byte order.
Records have one size, so the count is (file size - header size) / record size.
"""

import argparse
import json
import os
import struct
import sys

REGISTERS = {
    0: ['D%d' % i for i in range(8)] + ['A%d' % i for i in range(8)] + ['PC', 'SR', 'USP', 'SSP'],
    1: ['R%d' % i for i in range(16)] + ['PC', 'PR', 'SR', 'GBR', 'VBR', 'MACH', 'MACL'],
}
REGISTERS[2] = REGISTERS[1]
CPUS = {0: 'm68k', 1: 'sh2m', 2: 'sh2s'}
FLAGS = {1: 'returned', 2: 'abandoned', 4: 'truncated'}
# where the CPUs see the start of a domain, to show addresses as they do
CPU_BASE = {'68K RAM': 0xFF0000, '32X RAM': 0x06000000, '32X FB': 0x04000000}


def flag_names(flags):
    return '+'.join(name for bit, name in FLAGS.items() if flags & bit) or 'none'


class Call:
    def __init__(self, routine, index, raw):
        n = routine.register_count
        self.routine = routine
        self.index = index
        self.frame, self.seq, self.flags = struct.unpack_from('<3I', raw, 0)
        self.entry = struct.unpack_from('<%dI' % n, raw, 12)
        self.exit = struct.unpack_from('<%dI' % n, raw, 12 + 4 * n)
        start = 12 + 8 * n
        size = routine.region_bytes
        self.entry_bytes = raw[start:start + size]
        self.exit_bytes = raw[start + size:start + 2 * size]

    @property
    def flag_names(self):
        return flag_names(self.flags)

    def register(self, name, exit=False):
        return (self.exit if exit else self.entry)[self.routine.register_names.index(name)]

    def region(self, label, exit=False):
        """the bytes of one region, at entry or at exit"""
        offset = 0
        for domain, start, length, lab in self.routine.regions:
            if lab == label:
                data = self.exit_bytes if exit else self.entry_bytes
                return data[offset:offset + length]
            offset += length
        raise KeyError(label)

    def changes(self):
        """(region, first offset in the domain, bytes at entry, bytes at exit) for each run of changed bytes"""
        offset = 0
        for domain, start, length, label in self.routine.regions:
            a = self.entry_bytes[offset:offset + length]
            b = self.exit_bytes[offset:offset + length]
            i = 0
            while i < length:
                if a[i] == b[i]:
                    i += 1
                    continue
                j = i
                while j < length and a[j] != b[j]:
                    j += 1
                yield (domain, start, label), start + i, a[i:j], b[i:j]
                i = j
            offset += length


class Routine:
    def __init__(self, path):
        self.path = path
        with open(path, 'rb') as f:
            head = f.read(4096)
            if head[:4] != b'PDCR':
                raise ValueError('%s is not a PDCR file' % path)
            self.version, self.cpu, self.address = struct.unpack_from('<3I', head, 4)
            if self.version != 1:
                raise ValueError('%s: version %d, this reads version 1' % (path, self.version))
            pos = 16

            def string():
                nonlocal pos
                (n,) = struct.unpack_from('<H', head, pos)
                s = head[pos + 2:pos + 2 + n].decode('utf-8')
                pos += 2 + n
                return s

            self.name = string()
            self.register_count, nregions = struct.unpack_from('<2I', head, pos)
            pos += 8
            self.regions = []
            for _ in range(nregions):
                domain = string()
                start, length = struct.unpack_from('<2I', head, pos)
                pos += 8
                self.regions.append((domain, start, length, string()))
        self.header_size = pos
        self.region_bytes = sum(r[2] for r in self.regions)
        self.record_size = 12 + 8 * self.register_count + 2 * self.region_bytes
        self.count = (os.path.getsize(path) - self.header_size) // self.record_size
        names = REGISTERS[self.cpu]
        self.register_names = names if len(names) == self.register_count else ['r%d' % i for i in range(self.register_count)]

    @property
    def cpu_name(self):
        return CPUS.get(self.cpu, str(self.cpu))

    def call(self, index):
        if index < 0:
            index += self.count
        if not 0 <= index < self.count:
            raise IndexError('%s has %d calls' % (self.path, self.count))
        with open(self.path, 'rb') as f:
            f.seek(self.header_size + index * self.record_size)
            return Call(self, index, f.read(self.record_size))

    def calls(self):
        with open(self.path, 'rb') as f:
            f.seek(self.header_size)
            for index in range(self.count):
                yield Call(self, index, f.read(self.record_size))

    def heads(self):
        """(frame, seq, flags) of every call, reading only those"""
        with open(self.path, 'rb') as f:
            for index in range(self.count):
                f.seek(self.header_size + index * self.record_size)
                yield struct.unpack('<3I', f.read(12))


def read(path):
    return Routine(path)


def cpu_address(domain, offset):
    base = CPU_BASE.get(domain)
    return '' if base is None else ' ($%X)' % (base + offset)


def summary(folder):
    index_path = os.path.join(folder, 'index.json')
    if os.path.exists(index_path):
        with open(index_path) as f:
            index = json.load(f)
        frames = index.get('frames', {})
        print('%s: game %s, frames %s-%s, maxcalls %s%s' % (
            index_path, index.get('game'), frames.get('first'), frames.get('last') if frames.get('last') is not None else '...',
            index.get('maxcalls'), ', FAILED (see the message EmuHawk showed)' if index.get('failed') else ''))
        for r in index.get('routines', []):
            note = ', stopped at maxcalls' if r.get('maxcallsReached') else ''
            print('  %-24s %s %s: %d calls (%d returned, %d abandoned, %d truncated), frames %d-%d%s' % (
                r['name'], r['cpu'], r['address'], r['calls'], r['returned'], r['abandoned'], r['truncated'],
                r['firstFrame'], r['lastFrame'], note))
    for name in sorted(os.listdir(folder)):
        path = os.path.join(folder, name)
        if name.endswith('.pdcr'):
            r = Routine(path)
            heads = list(r.heads())
            counts = {}
            for _, _, flags in heads:
                counts[flag_names(flags)] = counts.get(flag_names(flags), 0) + 1
            frames = '%d-%d' % (min(h[0] for h in heads), max(h[0] for h in heads)) if heads else '-'
            print('%s: %s %s $%X, %d registers, regions %s' % (
                name, r.name, r.cpu_name, r.address, r.register_count,
                ', '.join('%s=%s+0x%X:0x%X' % (lab, dom, start, length) for dom, start, length, lab in r.regions) or 'none'))
            print('  %d calls of %d bytes (%.1f MB), frames %s, %s' % (
                r.count, r.record_size, os.path.getsize(path) / 1e6, frames,
                ', '.join('%s %d' % kv for kv in sorted(counts.items())) or 'no calls'))
        elif name.startswith('discover-') and name.endswith('.txt'):
            with open(path) as f:
                lines = f.read().splitlines()
            print('%s: %s' % (name, lines[0].lstrip('# ') if lines else 'empty'))
            for line in [l for l in lines if not l.startswith('#')][:10]:
                print('  ' + line)


def list_calls(path, limit):
    r = Routine(path)
    sp = 'A7' if r.cpu == 0 else 'R15'
    print('%s: %s %s $%X, %d calls' % (path, r.name, r.cpu_name, r.address, r.count))
    print('  index  frame        seq  flags       entry %-4s exit PC    exit %-4s changed bytes' % (sp, sp))
    for call in r.calls():
        if call.index >= limit:
            break
        changed = sum(len(a) for _, _, a, _ in call.changes())
        print('  %5d %6d %10d  %-10s  %08X  %08X  %08X  %d' % (
            call.index, call.frame, call.seq, call.flag_names, call.register(sp), call.register('PC', exit=True),
            call.register(sp, exit=True), changed))


def show(path, which):
    r = Routine(path)
    if which.startswith('s'):
        seq = int(which[1:], 0)
        call = next((c for c in r.calls() if c.seq == seq), None)
        if call is None:
            sys.exit('%s has no call with sequence number %d' % (path, seq))
    else:
        call = r.call(int(which, 0))
    print('%s: %s %s $%X, call %d of %d: frame %d, seq %d, %s' % (
        path, r.name, r.cpu_name, r.address, call.index, r.count, call.frame, call.seq, call.flag_names))
    print('  register  entry     exit')
    for i, name in enumerate(r.register_names):
        a, b = call.entry[i], call.exit[i]
        print('  %-8s  %08X  %08X%s' % (name, a, b, '' if a == b else '  *'))
    runs = list(call.changes())
    print('  %d bytes changed in %d runs' % (sum(len(a) for _, _, a, _ in runs), len(runs)))
    for (domain, start, label), offset, a, b in runs[:200]:
        shown = 32
        print('  %-10s %s+0x%X%s, %d bytes: %s -> %s%s' % (
            label, domain, offset, cpu_address(domain, offset), len(a), a[:shown].hex(), b[:shown].hex(),
            ' ...' if len(a) > shown else ''))
    if len(runs) > 200:
        print('  ... %d more runs' % (len(runs) - 200))


def main():
    p = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    sub = p.add_subparsers(dest='command')
    s = sub.add_parser('summary', help='index.json, the routines and the discover files of an output folder')
    s.add_argument('folder')
    s = sub.add_parser('calls', help="one line per call of a routine")
    s.add_argument('file')
    s.add_argument('-n', type=int, default=20, help='how many calls (default 20)')
    s = sub.add_parser('show', help="one call's registers and changed bytes")
    s.add_argument('file')
    s.add_argument('call', help='index in the file (negative from the end), or sSEQ for a sequence number')
    args = p.parse_args()
    if args.command == 'summary':
        summary(args.folder)
    elif args.command == 'calls':
        list_calls(args.file, args.n)
    elif args.command == 'show':
        show(args.file, args.call)
    else:
        p.print_help()


if __name__ == '__main__':
    main()
