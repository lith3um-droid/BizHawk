#!/usr/bin/env python3
"""Checks PicoDrive's execute hooks with the native harness and the test ROMs.

  run_tests.py                  build, then check the hits and registers
  run_tests.py --gcc            also check a gcc build (FAME/C computed gotos)
  run_tests.py --pristine DIR   also compare per-frame hashes against the core
                                in DIR, a waterbox/picodrive without the hooks
  run_tests.py --bench          also measure frames per second on the 32X ROM

Every logged hit is checked: the PC, the registers the caller and routine
set, the return address (PR, or the long at A7), and the routine's call
counter in RAM, which must equal the number of earlier hits.
"""

import argparse
import collections
import json
import os
import shutil
import statistics
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.join(HERE, 'build')
ROMS = os.path.join(BUILD, 'roms')

M68K_REGS = ['D%d' % i for i in range(8)] + ['A%d' % i for i in range(8)] + ['PC', 'SR', 'USP', 'SSP']
SH2_REGS = ['R%d' % i for i in range(16)] + ['PC', 'PR', 'SR', 'GBR', 'VBR', 'MACH', 'MACL']

Event = collections.namedtuple('Event', 'frame cpu addr opcode regs stack probes')


def make(out, **kw):
    args = ['make', '-s', '-C', HERE, '-j%d' % (os.cpu_count() or 1), 'OUT=' + out]
    args += ['%s=%s' % kv for kv in kw.items()]
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL)
    return os.path.join(out, 'harness')


def run(exe, rom, *args, prefix=()):
    args = list(prefix) + [exe] + [str(a) for a in args] + [rom]
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout


def rom_info(name):
    with open(os.path.join(ROMS, name + '.json')) as f:
        return os.path.join(ROMS, name), json.load(f)


def parse_log(path, probe_names):
    with open(path) as f:
        for line in f:
            if not line.startswith('H '):
                continue
            left, right = line.split('|')
            f = left.split()
            cpu = int(f[2])
            regs = dict(zip(M68K_REGS if cpu == 0 else SH2_REGS, (int(x, 16) for x in f[5:])))
            extra = [None if x == '-' else int(x, 16) for x in right.split()]
            stack = extra.pop(0) if cpu == 0 else None
            yield Event(int(f[1]), cpu, int(f[3], 16), int(f[4], 16), regs, stack,
                        dict(zip(probe_names, extra)))


class Checker:
    def __init__(self, name):
        self.name = name
        self.checks = 0
        self.failures = []

    def eq(self, what, got, want):
        self.checks += 1
        if got != want:
            self.failures.append('%s: got %s, want %s' % (what, fmt(got), fmt(want)))

    def ok(self, what, cond):
        self.eq(what, bool(cond), True)

    def report(self):
        status = 'ok' if not self.failures else 'FAILED (%d)' % len(self.failures)
        print('  %-28s %6d checks  %s' % (self.name, self.checks, status))
        for f in self.failures[:20]:
            print('    ' + f)
        return not self.failures


def fmt(v):
    return '%#x' % v if isinstance(v, int) and not isinstance(v, bool) else repr(v)


def watch_args(cpu, addrs):
    return ['-w', '%d:%s' % (cpu, ','.join('%x' % a for a in addrs))]


# the first word of the instruction at each label
MD_OPCODES = {'sub_a': 0x52b9, 'ret_a': 0x027c, 'sub_b': 0x52b9, 'ret_b': 0x4e40,
              'trap0': 0x52b9, 'ret_trap': 0x52b9, 'vblank': 0x52b9}


def check_md(exe, frames, tag):
    rom, info = rom_info('md_hooks.bin')
    L, R = info['labels'], info['ram']
    ssp, usp = info['ssp'], info['usp']
    names = ['sub_a', 'ret_a', 'sub_b', 'ret_b', 'trap0', 'ret_trap', 'vblank']
    probes = ['iter', 'count_a', 'count_b', 'count_vbl', 'count_trap']
    log = os.path.join(BUILD, 'md_%s.log' % tag)
    args = ['-f', frames, '-log', log] + watch_args(0, [L[n] for n in names])
    for p in probes:
        args += ['-p', '%x' % R[p]]
    run(exe, rom, *args)

    c = Checker('md_hooks %s' % tag)
    byaddr = {L[n]: n for n in names}
    hits = collections.Counter()
    for e in parse_log(log, probes):
        name = byaddr.get(e.addr)
        c.ok('hit at a watched address %#x' % e.addr, name)
        if not name:
            continue
        r, p = e.regs, e.probes
        w = '%s #%d' % (name, hits[name])
        c.eq(w + ' cpu', e.cpu, 0)
        c.eq(w + ' PC', r['PC'], e.addr)
        c.eq(w + ' opcode', e.opcode, MD_OPCODES[name])
        if name == 'sub_a':
            c.eq(w + ' D0', r['D0'], 0x1234a001)
            c.eq(w + ' D1', r['D1'], 0x1234a002)
            c.eq(w + ' A2', r['A2'], 0x00abcd00)
            c.eq(w + ' D7 = iteration', r['D7'], p['iter'])
            c.eq(w + ' return address', e.stack, L['ret_a'])
            c.eq(w + ' SR', r['SR'] & 0xff00, 0x2000)
            c.eq(w + ' SSP', r['SSP'], ssp - 4)
            c.eq(w + ' A7', r['A7'], ssp - 4)
            c.eq(w + ' USP', r['USP'], usp)
            c.eq(w + ' count', p['count_a'], hits[name])
        elif name == 'ret_a':
            c.eq(w + ' D2', r['D2'], 0xaaaa0001)
            c.eq(w + ' D3', r['D3'], 0xaaaa0002)
            c.eq(w + ' SR', r['SR'], 0x2008)
            c.eq(w + ' A7', r['A7'], ssp)
        elif name == 'sub_b':
            c.eq(w + ' D0', r['D0'], 0x5678b001)
            c.eq(w + ' D1', r['D1'], 0x5678b002)
            c.eq(w + ' A3', r['A3'], 0x00dcba00)
            c.eq(w + ' return address', e.stack, L['ret_b'])
            c.eq(w + ' SR (user mode)', r['SR'] & 0xff00, 0x0000)
            c.eq(w + ' USP', r['USP'], usp - 4)
            c.eq(w + ' A7', r['A7'], usp - 4)
            c.eq(w + ' SSP', r['SSP'], ssp)
            c.eq(w + ' count', p['count_b'], hits[name])
        elif name == 'ret_b':
            c.eq(w + ' D2', r['D2'], 0xbbbb0001)
            c.eq(w + ' SR', r['SR'], 0x001f)
            c.eq(w + ' USP', r['USP'], usp)
        elif name == 'trap0':
            c.eq(w + ' SR', r['SR'], 0x201f)
            c.eq(w + ' SSP', r['SSP'], ssp - 6)
            c.eq(w + ' USP', r['USP'], usp)
            c.eq(w + ' stacked SR', e.stack >> 16, 0x001f)
            c.eq(w + ' count', p['count_trap'], hits[name])
        elif name == 'ret_trap':
            c.eq(w + ' SR', r['SR'], 0x201f)
            c.eq(w + ' A7', r['A7'], ssp)
        elif name == 'vblank':
            c.eq(w + ' SR', r['SR'] & 0x2700, 0x2600)
            c.eq(w + ' count', p['count_vbl'], hits[name])
        hits[name] += 1

    for call, ret in (('sub_a', 'ret_a'), ('sub_b', 'ret_b'), ('trap0', 'ret_trap')):
        c.ok('%s hits %d, %s hits %d' % (call, hits[call], ret, hits[ret]), 0 <= hits[call] - hits[ret] <= 1)
    c.ok('sub_a hit %d times' % hits['sub_a'], hits['sub_a'] > frames * 10)
    c.ok('vblank hit %d times in %d frames' % (hits['vblank'], frames), frames - 1 <= hits['vblank'] <= frames)
    return c.report(), hits


def check_32x(exe, frames, tag):
    rom, info = rom_info('32x_hooks.32x')
    L, R, V = info['labels'], info['ram'], info['sh2vars']
    m68k = ['sub68', 'ret68']
    master = ['sub1', 'm_ret1', 'sub2', 'm_ret2', 'm_call3', 'm_slot3', 'sub3', 'm_ret3']
    slave = ['ssub', 's_ret']
    probes = ['iter', 'count68', 'm_iter', 'cnt1', 'cnt2', 'cnt3', 's_iter', 's_cnt']
    paddr = dict(R, **V)
    log = os.path.join(BUILD, '32x_%s.log' % tag)
    # also watch addresses that must never be reported: the cache-through
    # alias of sub1 on the master, and the master's sub1 on the slave
    args = ['-32x', '-f', frames, '-log', log]
    args += watch_args(0, [L[n] for n in m68k])
    args += watch_args(1, [L[n] for n in master] + [L['sub1'] | 0x20000000])
    args += watch_args(2, [L[n] for n in slave] + [L['sub1']])
    for p in probes:
        args += ['-p', '%x' % paddr[p]]
    run(exe, rom, *args)

    c = Checker('32x_hooks %s' % tag)
    byaddr = {(0, L[n]): n for n in m68k}
    byaddr.update({(1, L[n]): n for n in master})
    byaddr.update({(2, L[n]): n for n in slave})
    hits = collections.Counter()
    mul = (0x11110001 * 0x11110002) & 0xffffffff
    dmul = (0x22220001 * 0x22220002) & 0xffffffffffffffff
    for e in parse_log(log, probes):
        name = byaddr.get((e.cpu, e.addr))
        c.ok('cpu %d hit at a watched address %#x' % (e.cpu, e.addr), name)
        if not name:
            continue
        r, p = e.regs, e.probes
        w = '%s #%d' % (name, hits[name])
        c.eq(w + ' PC', r['PC'], e.addr)
        if e.cpu:
            c.eq(w + ' GBR', r['GBR'], 0x20004000)
            c.eq(w + ' VBR', r['VBR'], info['vbr'])
            c.eq(w + ' SR', r['SR'], 0xf1 if e.cpu == 1 else 0xf0)
            c.eq(w + ' R15', r['R15'], 0x06040000 if e.cpu == 1 else 0x0603f800)
        if name == 'sub68':
            c.eq(w + ' D0', r['D0'], 0x6800a001)
            c.eq(w + ' D7 = iteration', r['D7'], p['iter'])
            c.eq(w + ' return address', e.stack, L['ret68'])
            c.eq(w + ' SR', r['SR'] & 0xff00, 0x2700)
            c.eq(w + ' count', p['count68'], hits[name])
        elif name == 'ret68':
            c.eq(w + ' D1', r['D1'], 0x68000042)
        elif name == 'sub1':
            c.eq(w + ' PR', r['PR'], L['m_ret1'])
            c.eq(w + ' R4', r['R4'], 0x11110001)
            c.eq(w + ' R5', r['R5'], 0x11110002)
            c.eq(w + ' R7 = iteration', r['R7'], p['m_iter'])
            c.eq(w + ' count', p['cnt1'], hits[name])
        elif name == 'm_ret1':
            c.eq(w + ' R9', r['R9'], 0xa1a1a1a1)
            c.eq(w + ' R10', r['R10'], 0xa1a1a1a2)
            c.eq(w + ' MACL', r['MACL'], mul)
        elif name == 'sub2':
            c.eq(w + ' PR', r['PR'], L['m_ret2'])
            c.eq(w + ' R1 = JSR target', r['R1'], L['sub2'])
            c.eq(w + ' R4', r['R4'], 0x22220001)
            c.eq(w + ' R5', r['R5'], 0x22220002)
            c.eq(w + ' R7 = iteration', r['R7'], p['m_iter'])
            c.eq(w + ' count', p['cnt2'], hits[name])
        elif name == 'm_ret2':
            c.eq(w + ' R9', r['R9'], 0xb2b2b2b1)
            c.eq(w + ' MACH:MACL', r['MACH'] << 32 | r['MACL'], dmul)
        elif name == 'm_call3':
            c.eq(w + ' opcode (BSRF R2)', e.opcode, 0x0203)
            c.eq(w + ' R2', r['R2'], L['sub3'] - (L['m_call3'] + 4))
        elif name == 'm_slot3':
            # the delay slot runs after BSRF set PR, before it jumps
            c.eq(w + ' opcode (MOV #0x33,R5)', e.opcode, 0xe533)
            c.eq(w + ' PR', r['PR'], L['m_ret3'])
            c.eq(w + ' R5 not yet set', r['R5'], 0x22220002)
            c.eq(w + ' follows BSRF', hits['m_call3'], hits[name] + 1)
        elif name == 'sub3':
            c.eq(w + ' PR', r['PR'], L['m_ret3'])
            c.eq(w + ' R4', r['R4'], 0x33330001)
            c.eq(w + ' R5 from the delay slot', r['R5'], 0x33)
            c.eq(w + ' count', p['cnt3'], hits[name])
        elif name == 'm_ret3':
            c.eq(w + ' R9', r['R9'], 0xc3c3c3c1)
            c.eq(w + ' R6 from the RTS delay slot', r['R6'], 0x44)
        elif name == 'ssub':
            c.eq(w + ' PR', r['PR'], L['s_ret'])
            c.eq(w + ' R4', r['R4'], 0x55550001)
            c.eq(w + ' R5 from the delay slot', r['R5'], 0x5a)
            c.eq(w + ' R7 = iteration', r['R7'], p['s_iter'])
            c.eq(w + ' count', p['s_cnt'], hits[name])
        elif name == 's_ret':
            c.eq(w + ' R9', r['R9'], 0x5555aaaa)
        hits[name] += 1

    for call, ret in (('sub68', 'ret68'), ('sub1', 'm_ret1'), ('sub2', 'm_ret2'),
                      ('sub3', 'm_ret3'), ('ssub', 's_ret')):
        c.ok('%s hits %d, %s hits %d' % (call, hits[call], ret, hits[ret]), 0 <= hits[call] - hits[ret] <= 1)
    for n in ('sub68', 'sub1', 'ssub'):
        c.ok('%s hit %d times' % (n, hits[n]), hits[n] > frames * 10)
    return c.report(), hits


def hashes(exe, rom, frames, *args):
    out = run(exe, rom, '-f', frames, '-hash', '-', *args)
    return [l for l in out.splitlines() if l.startswith('F ')]


def check_same(name, a, b):
    same = a == b and len(a) > 0
    first = next((i for i, (x, y) in enumerate(zip(a, b)) if x != y), None)
    print('  %-44s %s' % (name, 'identical, %d frames' % len(a) if same else
                           'DIFFERENT from frame %s' % first))
    return same


def instruction_counts(exe, frames):
    rom, _ = rom_info('32x_hooks.32x')
    out = run(exe, rom, '-32x', '-f', frames, '-a', 0, '-a', 1, '-a', 2, '-counts', '-')
    rows = [list(map(int, l.split()[2:])) for l in out.splitlines() if l.startswith('C ')]
    steady = rows[10:]
    print('  instructions per frame (frames 10-%d): 68000 %d, master %d, slave %d' %
          tuple([frames - 1] + [round(statistics.mean(r[i] for r in steady)) for i in range(3)]))


def bench(exes, frames, reps):
    rom, info = rom_info('32x_hooks.32x')
    L = info['labels']
    cold = watch_args(0, [L['spin']]) + watch_args(1, [L['master_start'], L['lt']]) + \
        watch_args(2, [L['slave_start']])
    hot = watch_args(0, [L['sub68'], L['ret68']]) + \
        watch_args(1, [L['sub1'], L['sub2'], L['sub3'], L['m_slot3']]) + watch_args(2, [L['ssub'], L['s_ret']])
    configs = [
        ('unmodified core', exes['pristine'], []),
        ('hooks, nothing watched', exes['clang'], []),
        ('4 cold watches (never hit)', exes['clang'], ['-q'] + cold),
        ('8 hot watches, counting', exes['clang'], ['-q'] + hot),
        ('watch-all x3, counting', exes['clang'], ['-a', 0, '-a', 1, '-a', 2]),
    ]
    if 'gcc' in exes and 'pristine-gcc' in exes:
        configs += [('gcc: unmodified core', exes['pristine-gcc'], []),
                    ('gcc: hooks, nothing watched', exes['gcc'], [])]
    # one CPU, all configurations interleaved, so drift hits them alike
    pin = ['taskset', '-c', str((os.cpu_count() or 1) - 1)] if shutil.which('taskset') else []
    results = {name: [] for name, _, _ in configs}
    for _ in range(reps):
        for name, exe, args in configs:
            out = run(exe, rom, '-32x', '-f', frames, '-bench', *args, prefix=pin)
            line = [l for l in out.splitlines() if l.startswith('frames ')][0]
            results[name].append(float(line.split('fps')[1].split()[0]))
    print('  %d frames x %d runs%s, fps median (min-max):' % (frames, reps, ', pinned' if pin else ''))
    for name, _, _ in configs:
        v = results[name]
        print('    %-28s %7.1f  (%.1f-%.1f)' % (name, statistics.median(v), min(v), max(v)))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--pristine', metavar='DIR', help='waterbox/picodrive of an unmodified core')
    ap.add_argument('--gcc', action='store_true', help='also build and check with gcc')
    ap.add_argument('--bench', action='store_true', help='measure frames per second')
    ap.add_argument('--lto', action='store_true', help='build with link time optimization')
    ap.add_argument('--frames', type=int, default=90)
    ap.add_argument('--hash-frames', type=int, default=600)
    ap.add_argument('--bench-frames', type=int, default=600)
    ap.add_argument('--reps', type=int, default=8)
    opt = ap.parse_args()

    subprocess.run([sys.executable, os.path.join(HERE, 'mkroms.py'), ROMS], check=True)
    extra = {'LTO': 1} if opt.lto else {}
    suffix = '-lto' if opt.lto else ''
    exes = {'clang': make(os.path.join(BUILD, 'clang' + suffix), **extra)}
    if opt.gcc:
        exes['gcc'] = make(os.path.join(BUILD, 'gcc' + suffix), CC='gcc', **extra)
    if opt.pristine:
        exes['pristine'] = make(os.path.join(BUILD, 'pristine' + suffix), CORE=opt.pristine, **extra)
        if opt.gcc:
            exes['pristine-gcc'] = make(os.path.join(BUILD, 'pristine-gcc' + suffix), CC='gcc',
                                        CORE=opt.pristine, **extra)

    ok = True
    print('hits and registers:')
    for tag in [t for t in ('clang', 'gcc') if t in exes]:
        good, hits = check_md(exes[tag], opt.frames, tag)
        ok &= good
        print('    ' + ' '.join('%s=%d' % kv for kv in sorted(hits.items())))
        good, hits = check_32x(exes[tag], opt.frames, tag)
        ok &= good
        print('    ' + ' '.join('%s=%d' % kv for kv in sorted(hits.items())))

    print('emulation unchanged, per-frame hashes of video, audio and RAM:')
    rom32, info = rom_info('32x_hooks.32x')
    rommd, infomd = rom_info('md_hooks.bin')
    n = opt.hash_frames
    for tag in [t for t in ('clang', 'gcc') if t in exes]:
        exe = exes[tag]
        base32 = hashes(exe, rom32, n, '-32x')
        basemd = hashes(exe, rommd, n)
        ok &= check_same('32x %s: watch-all x3 vs no hooks' % tag, base32,
                         hashes(exe, rom32, n, '-32x', '-a', 0, '-a', 1, '-a', 2))
        ok &= check_same('md %s: watch-all vs no hooks' % tag, basemd, hashes(exe, rommd, n, '-a', 0))
        L = info['labels']
        ok &= check_same('32x %s: logged watches vs no hooks' % tag, base32,
                         hashes(exe, rom32, n, '-32x', '-log', os.devnull,
                                *(watch_args(1, [L['sub1'], L['m_slot3']]) + watch_args(2, [L['ssub']]) +
                                  watch_args(0, [L['sub68']]))))
        ptag = 'pristine' if tag == 'clang' else 'pristine-gcc'
        if ptag in exes:
            ok &= check_same('32x %s: hooked core vs unmodified core' % tag, base32,
                             hashes(exes[ptag], rom32, n, '-32x'))
            ok &= check_same('md %s: hooked core vs unmodified core' % tag, basemd,
                             hashes(exes[ptag], rommd, n))

    print('watch-all counts:')
    instruction_counts(exes['clang'], 60)

    if opt.bench:
        if 'pristine' not in exes:
            sys.exit('--bench needs --pristine')
        print('performance, 32X test ROM:')
        bench(exes, opt.bench_frames, opt.reps)

    print('ALL OK' if ok else 'FAILURES')
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
