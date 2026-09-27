#!/usr/bin/env python3
"""Builds the execute hook test ROMs, and a .json of their labels (as the
CPUs see them at run time) for run_tests.py.

md_hooks.bin   68000 only. A loop calls sub_a by JSR in supervisor mode and
               sub_b by BSR in user mode, then returns to supervisor mode by
               TRAP #0. VBlank interrupts change the backdrop colour.
32x_hooks.32x  The 68000 enables the 32X and calls sub68 in a loop from the
               $880000 mirror of the cartridge. The master SH-2 draws a
               picture, then calls sub1 (BSR), sub2 (JSR) and sub3 (BSRF with
               a delay slot that sets R5; sub3 returns with a delay slot that
               sets R6). The slave calls ssub (BSR, delay slot sets R5).

Every caller loads known values into registers first, every routine counts
its calls in RAM and loads its own known values before returning. Each loop
starts with a delay loop, which keeps the CPUs busy but the calls few; on the
SH-2s it is DT/BF $-2, the pattern MAME's busy loop hack looks for. No BIOS
is needed: PicoDrive's BIOS-less boot (pico/32x/32x.c, p32x_reset_sh2s)
copies the SH-2 code to SDRAM using the 32X header at $3C0, and its stub
SH-2 BIOS (pico/32x/memory.c) jumps to the entry points at $3E0 and $3E4.

usage: mkroms.py OUTDIR
"""

import json
import os
import struct
import sys

from asm import M68K, SH2

VDP_DATA = 0xc00000
VDP_CTRL = 0xc00004
RAM = 0xff0000
HI = 0x880000  # the cartridge, once the 32X adapter is enabled


def md_header(a, system, title):
    assert a.pc == 0x100
    a.data(system.encode().ljust(16))
    a.data(b'(C)TEST 2026.SEP')
    a.data(title.encode().ljust(48))
    a.data(title.encode().ljust(48))
    a.data(b'GM 00000000-00')
    a.w16(0)  # checksum, filled in by finish()
    a.data(b'J'.ljust(16))
    a.w32(0)
    a.w32(0)  # ROM end, filled in by finish()
    a.w32(0xff0000)
    a.w32(0xffffff)
    a.data(b' ' * 12)  # no SRAM
    a.data(b' ' * 12)
    a.data(b' ' * 40)
    a.data(b'U'.ljust(16))
    assert a.pc == 0x200


def finish(rom, size):
    rom = bytearray(rom.ljust(size, b'\xff'))
    struct.pack_into('>I', rom, 0x1a4, size - 1)
    words = struct.unpack('>%dH' % ((size - 0x200) // 2), rom[0x200:])
    struct.pack_into('>H', rom, 0x18e, sum(words) & 0xffff)
    return bytes(rom)


def build_md():
    ITER, COUNT_A, COUNT_B, COUNT_VBL, COUNT_TRAP = (RAM + 4 * i for i in range(5))
    a = M68K(0)
    a.w32(0x00fffe00)
    a.abs32('entry')
    for v in range(2, 64):
        a.abs32({30: 'vblank', 32: 'trap0'}.get(v, 'spin'))
    md_header(a, 'SEGA MEGA DRIVE', 'PICODRIVE HOOK TEST MD')

    a.label('entry')
    a.move_w_imm_sr(0x2700)
    # mode 5, display and VBlank interrupt on, H40, autoincrement 2, backdrop 1
    for reg in (0x8004, 0x8164, 0x8c81, 0x8f02, 0x8701):
        a.move_w_imm_abs(reg, VDP_CTRL)
    for addr in (ITER, COUNT_A, COUNT_B, COUNT_VBL, COUNT_TRAP):
        a.clr_l_abs(addr)
    a.lea_abs(0x00fffc00, 0)
    a.move_a_usp(0)
    a.move_w_imm_sr(0x2000)

    a.label('loop')
    a.moveq(100, 6)
    a.label('delay')
    a.dbf(6, 'delay')
    a.move_l_abs_d(ITER, 7)
    a.move_l_imm_d(0x1234a001, 0)
    a.move_l_imm_d(0x1234a002, 1)
    a.lea_abs(0x00abcd00, 2)
    a.jsr_abs('sub_a')
    a.label('ret_a')
    a.andi_w_sr(0xdfff)  # to user mode
    a.move_l_imm_d(0x5678b001, 0)
    a.move_l_imm_d(0x5678b002, 1)
    a.lea_abs(0x00dcba00, 3)
    a.bsr_w('sub_b')
    a.label('ret_b')
    a.trap(0)  # back to supervisor mode
    a.label('ret_trap')
    a.addq_l_abs(1, ITER)
    a.bra_w('loop')

    a.label('sub_a')
    a.addq_l_abs(1, COUNT_A)
    a.move_l_imm_d(0xaaaa0001, 2)
    a.move_l_imm_d(0xaaaa0002, 3)
    a.rts()

    a.label('sub_b')
    a.addq_l_abs(1, COUNT_B)
    a.move_l_imm_d(0xbbbb0001, 2)
    a.move_w_imm_ccr(0x1f)
    a.rts()

    a.label('trap0')
    a.addq_l_abs(1, COUNT_TRAP)
    a.ori_w_imm_ind_a7(0x2000)  # return in supervisor mode
    a.rte()

    a.label('vblank')
    a.addq_l_abs(1, COUNT_VBL)
    a.move_l_imm_abs(0xc0020000, VDP_CTRL)  # CRAM write, colour 1
    a.move_w_abs_abs(COUNT_VBL + 2, VDP_DATA)
    a.rte()

    a.label('spin')
    a.bra_s('spin')

    rom = finish(a.link(), 0x10000)
    info = {
        'labels': dict(a.labels),
        'ram': {'iter': ITER, 'count_a': COUNT_A, 'count_b': COUNT_B,
                'count_vbl': COUNT_VBL, 'count_trap': COUNT_TRAP},
        'ssp': 0x00fffe00, 'usp': 0x00fffc00,
    }
    return rom, info


SH2_ROM = 0x1000        # where the SH-2 code sits in the ROM
SDRAM = 0x06000000      # where it runs (cached alias)
VARS = 0x26030000       # SH-2 variables, cache-through alias of SDRAM


def build_sh2():
    MITER, CNT1, CNT2, CNT3, SITER, SCNT = (VARS + 4 * i for i in range(6))
    s = SH2(SDRAM)

    # master
    s.label('master_start')
    # line table and packed pixels, into the framebuffer the SH-2 can access
    s.mov_l_lit(0x24000000, 1)
    s.mov_l_lit(0x100, 3)
    s.mov_l_lit(160, 4)
    s.mov_l_lit(256, 2)
    s.label('lt')
    s.mov_w_st(3, 1)
    s.add_imm(2, 1)
    s.add(4, 3)
    s.dt(2)
    s.bf('lt')
    s.mov_l_lit(224 * 320 // 4, 2)
    s.mov_l_lit(0x01020304, 3)
    s.mov_l_lit(0x04040404, 5)
    s.label('px')
    s.mov_l_st(3, 1)
    s.add_imm(4, 1)
    s.add(5, 3)
    s.dt(2)
    s.bf('px')
    s.mov_l_lit(0x20004200, 1)  # palette
    s.mov_l_lit(256, 2)
    s.mov_imm(0, 3)
    s.label('pl')
    s.mov_w_st(3, 1)
    s.add_imm(2, 1)
    s.add_imm(0x25, 3)
    s.dt(2)
    s.bf('pl')
    # show it: swap the framebuffers while the 32X is blanked, then packed pixel mode
    s.mov_l_lit(0x20004100, 1)
    s.mov_imm(1, 0)
    s.mov_b_r0_disp(0xb, 1)
    s.mov_b_r0_disp(0x1, 1)
    s.mov_l_lit(MITER, 8)

    s.label('m_loop')
    s.mov_l_lit(1000, 3)
    s.label('m_delay')
    s.dt(3)
    s.bf('m_delay')
    s.mov_l_ld(8, 7)  # R7 = iteration
    s.mov_l_lit(0x11110001, 4)
    s.mov_l_lit(0x11110002, 5)
    s.label('m_call1')
    s.bsr('sub1')
    s.nop()
    s.label('m_ret1')
    s.mov_l_lit(0x22220001, 4)
    s.mov_l_lit(0x22220002, 5)
    s.mov_l_lit('sub2', 1)
    s.label('m_call2')
    s.jsr(1)
    s.nop()
    s.label('m_ret2')
    s.mov_l_lit(0x33330001, 4)
    s.mov_l_lit(lambda L: L['sub3'] - (L['m_call3'] + 4), 2)
    s.label('m_call3')
    s.bsrf(2)
    s.label('m_slot3')
    s.mov_imm(0x33, 5)  # delay slot: sub3 sees R5 = 0x33
    s.label('m_ret3')
    s.mov_l_ld(8, 0)
    s.add_imm(1, 0)
    s.mov_l_st(0, 8)
    s.mov_l_lit(0x20004200, 1)
    s.mov_w_r0_disp(2, 1)  # palette colour 1 = iteration
    s.bra('m_loop')
    s.nop()

    for name, count, r9, mac in (('sub1', CNT1, 0xa1a1a1a1, 'mul'),
                                 ('sub2', CNT2, 0xb2b2b2b1, 'dmuls'),
                                 ('sub3', CNT3, 0xc3c3c3c1, None)):
        s.label(name)
        s.mov_l_lit(count, 1)
        s.mov_l_ld(1, 0)
        s.add_imm(1, 0)
        s.mov_l_st(0, 1)
        s.mov_l_lit(r9, 9)
        if mac == 'mul':
            s.mul_l(4, 5)  # MACL = R4 * R5
            s.mov_l_lit(0xa1a1a1a2, 10)
        elif mac == 'dmuls':
            s.dmuls_l(4, 5)  # MACH:MACL = R4 * R5, signed
        s.rts()
        if name == 'sub3':
            s.mov_imm(0x44, 6)  # delay slot: the caller sees R6 = 0x44
        else:
            s.nop()
    s.pool()

    # slave
    s.label('slave_start')
    s.mov_l_lit(SITER, 8)
    s.label('s_loop')
    s.mov_l_lit(700, 3)
    s.label('s_delay')
    s.dt(3)
    s.bf('s_delay')
    s.clrt()  # DT left T set; the slave calls with T clear
    s.mov_l_ld(8, 7)
    s.mov_l_lit(0x55550001, 4)
    s.label('s_call')
    s.bsr('ssub')
    s.mov_imm(0x5a, 5)  # delay slot: ssub sees R5 = 0x5a
    s.label('s_ret')
    s.mov_l_ld(8, 0)
    s.add_imm(1, 0)
    s.mov_l_st(0, 8)
    s.bra('s_loop')
    s.nop()
    s.label('ssub')
    s.mov_l_lit(SCNT, 1)
    s.mov_l_ld(1, 0)
    s.add_imm(1, 0)
    s.mov_l_st(0, 1)
    s.mov_l_lit(0x5555aaaa, 9)
    s.rts()
    s.nop()
    s.pool()

    code = s.link()
    vars_ = {'m_iter': MITER, 'cnt1': CNT1, 'cnt2': CNT2, 'cnt3': CNT3,
             's_iter': SITER, 's_cnt': SCNT}
    return code, s.labels, vars_


def build_32x():
    ITER, COUNT68 = RAM, RAM + 4
    code, sh2_labels, sh2_vars = build_sh2()
    size = (len(code) + 3) & ~3

    a = M68K(0)
    a.w32(0x00fffe00)
    a.abs32('entry')
    for v in range(2, 64):
        a.abs32('spin')
    md_header(a, 'SEGA 32X', 'PICODRIVE HOOK TEST 32X')
    # with the adapter on, vector v points to $880200 + 6 * (v - 1)
    a.jmp_abs(lambda L: L['entry'] + HI)
    for v in range(2, 48):
        a.jmp_abs(lambda L: L['spin'] + HI)
    a.data(bytes(0x3c0 - len(a.buf)))

    # 32X header, as read by p32x_reset_sh2s() and the stub SH-2 BIOS
    a.data(b'MARS CHECK MODE ')
    a.w32(0)                                # version
    a.w32(SH2_ROM)                          # initial data: ROM offset
    a.w32(0)                                # ... SDRAM offset
    a.w32(size)                             # ... size
    a.w32(sh2_labels['master_start'])       # master entry
    a.w32(sh2_labels['slave_start'])        # slave entry
    a.w32(SDRAM)                            # master VBR
    a.w32(SDRAM)                            # slave VBR
    assert a.pc == 0x3f0

    a.label('entry')
    a.move_w_imm_sr(0x2700)
    # MD VDP: mode 5, display on, H40 (the 32X layer is drawn in H40 only)
    for reg in (0x8004, 0x8144, 0x8c81, 0x8f02):
        a.move_w_imm_abs(reg, VDP_CTRL)
    a.clr_l_abs(ITER)
    a.clr_l_abs(COUNT68)
    a.move_w_imm_abs(0x8003, 0xa15100)  # FM, nRES, ADEN: 32X on, SH-2s run
    a.jmp_abs(lambda L: L['loop'] + HI)
    a.label('loop')
    a.moveq(100, 6)
    a.label('delay68')
    a.dbf(6, 'delay68')
    a.move_l_abs_d(ITER, 7)
    a.move_l_imm_d(0x6800a001, 0)
    a.jsr_abs(lambda L: L['sub68'] + HI)
    a.label('ret68')
    a.move_w_d_abs(7, 0xa15128)  # a comm port: the SH-2s catch up here
    a.addq_l_abs(1, ITER)
    a.bra_s('loop')
    a.label('sub68')
    a.addq_l_abs(1, COUNT68)
    a.move_l_imm_d(0x68000042, 1)
    a.rts()
    a.label('spin')
    a.bra_s('spin')

    rom = a.link()
    assert len(rom) <= SH2_ROM
    rom = rom.ljust(SH2_ROM, b'\0') + code
    rom = finish(rom, 0x10000)

    labels = {k: v + HI for k, v in a.labels.items() if k in ('loop', 'ret68', 'sub68', 'spin')}
    labels.update(sh2_labels)
    info = {
        'labels': labels,
        'ram': {'iter': ITER, 'count68': COUNT68},
        'sh2vars': sh2_vars,
        'vbr': SDRAM,
    }
    return rom, info


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    os.makedirs(out, exist_ok=True)
    for name, build in (('md_hooks.bin', build_md), ('32x_hooks.32x', build_32x)):
        rom, info = build()
        with open(os.path.join(out, name), 'wb') as f:
            f.write(rom)
        info['labels'] = {k: v for k, v in info['labels'].items() if not k.startswith('_')}
        with open(os.path.join(out, name + '.json'), 'w') as f:
            json.dump(info, f, indent=1, sort_keys=True)


if __name__ == '__main__':
    main()
