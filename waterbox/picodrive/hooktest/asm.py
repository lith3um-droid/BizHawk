"""A tiny assembler for the hook test programs: the few 68000 and SH-2
instructions they use, labels, literal pools, and range checks. Both CPUs
are big-endian. Operands that name a label are resolved by link()."""

import struct


class Asm:
    def __init__(self, base):
        self.base = base  # address of the first byte, as the CPU sees it
        self.buf = bytearray()
        self.labels = {}
        self.fixups = []  # (offset, function(offset)), run by link()

    @property
    def pc(self):
        return self.base + len(self.buf)

    def label(self, name):
        assert name not in self.labels, name
        self.labels[name] = self.pc

    def addr(self, x):
        """A label's address, an int, or a function of the labels."""
        if isinstance(x, str):
            return self.labels[x]
        if callable(x):
            return x(self.labels)
        return x

    def w16(self, v):
        self.buf += struct.pack('>H', v & 0xffff)

    def w32(self, v):
        self.buf += struct.pack('>I', v & 0xffffffff)

    def data(self, b):
        self.buf += b

    def align(self, n, fill=0):
        while len(self.buf) % n:
            self.buf.append(fill)

    def put16(self, off, v):
        struct.pack_into('>H', self.buf, off, v & 0xffff)

    def put32(self, off, v):
        struct.pack_into('>I', self.buf, off, v & 0xffffffff)

    def link(self):
        for off, fn in self.fixups:
            fn(off)
        return bytes(self.buf)


def check(cond, msg):
    if not cond:
        raise ValueError(msg)


class M68K(Asm):
    """68000. Absolute operands are 32-bit (abs.l)."""

    def abs32(self, x):
        off = len(self.buf)
        self.w32(0)
        self.fixups.append((off, lambda o: self.put32(o, self.addr(x))))

    def op_abs(self, opcode, x):
        self.w16(opcode)
        self.abs32(x)

    def move_w_imm_sr(self, imm): self.w16(0x46fc); self.w16(imm)
    def andi_w_sr(self, imm): self.w16(0x027c); self.w16(imm)
    def move_w_imm_ccr(self, imm): self.w16(0x44fc); self.w16(imm)
    def ori_w_imm_ind_a7(self, imm): self.w16(0x0057); self.w16(imm)
    def moveq(self, imm, dn): self.w16(0x7000 | dn << 9 | imm & 0xff)
    def move_l_imm_d(self, imm, dn): self.op_abs(0x203c | dn << 9, imm)
    def move_l_abs_d(self, a, dn): self.op_abs(0x2039 | dn << 9, a)
    def move_w_d_abs(self, dn, a): self.op_abs(0x33c0 | dn, a)
    def move_w_imm_abs(self, imm, a): self.w16(0x33fc); self.w16(imm); self.abs32(a)
    def move_l_imm_abs(self, imm, a): self.w16(0x23fc); self.abs32(imm); self.abs32(a)
    def move_w_abs_abs(self, src, dst): self.w16(0x33f9); self.abs32(src); self.abs32(dst)
    def lea_abs(self, a, an): self.op_abs(0x41f9 | an << 9, a)
    def move_a_usp(self, an): self.w16(0x4e60 | an)
    def addq_l_abs(self, q, a): check(1 <= q <= 8, 'addq'); self.op_abs(0x5080 | (q & 7) << 9 | 0x39, a)
    def clr_l_abs(self, a): self.op_abs(0x42b9, a)
    def jsr_abs(self, a): self.op_abs(0x4eb9, a)
    def jmp_abs(self, a): self.op_abs(0x4ef9, a)
    def rts(self): self.w16(0x4e75)
    def rte(self): self.w16(0x4e73)
    def nop(self): self.w16(0x4e71)
    def trap(self, n): self.w16(0x4e40 | n)

    def branch_w(self, opcode, target):
        at = self.pc
        self.w16(opcode)
        off = len(self.buf)
        self.w16(0)

        def fix(o):
            d = self.addr(target) - (at + 2)
            check(-0x8000 <= d < 0x8000, 'branch out of range')
            self.put16(o, d)
        self.fixups.append((off, fix))

    def branch_s(self, opcode, target):
        at = self.pc
        off = len(self.buf)
        self.w16(opcode)

        def fix(o):
            d = self.addr(target) - (at + 2)
            check(-0x80 <= d < 0x80 and d not in (0, -1), 'short branch out of range')
            self.put16(o, opcode | d & 0xff)
        self.fixups.append((off, fix))

    def dbf(self, dn, t): self.branch_w(0x51c8 | dn, t)
    def bsr_w(self, t): self.branch_w(0x6100, t)
    def bra_w(self, t): self.branch_w(0x6000, t)
    def bra_s(self, t): self.branch_s(0x6000, t)


class SH2(Asm):
    """SH-2. mov.l/mov.w literals come from pools placed with pool()."""

    def __init__(self, base):
        super().__init__(base)
        self.lits = []  # (label, value) waiting for the next pool
        self.nlits = 0

    def op(self, v): self.w16(v)
    def nop(self): self.op(0x0009)
    def rts(self): self.op(0x000b)
    def sett(self): self.op(0x0018)
    def clrt(self): self.op(0x0008)
    def mov_imm(self, imm, n): check(-128 <= imm < 256, 'imm8'); self.op(0xe000 | n << 8 | imm & 0xff)
    def mov(self, m, n): self.op(0x6003 | n << 8 | m << 4)
    def add_imm(self, imm, n): check(-128 <= imm < 128, 'imm8'); self.op(0x7000 | n << 8 | imm & 0xff)
    def add(self, m, n): self.op(0x300c | n << 8 | m << 4)
    def mov_l_st(self, m, n): self.op(0x2002 | n << 8 | m << 4)   # mov.l Rm,@Rn
    def mov_w_st(self, m, n): self.op(0x2001 | n << 8 | m << 4)   # mov.w Rm,@Rn
    def mov_l_ld(self, m, n): self.op(0x6002 | n << 8 | m << 4)   # mov.l @Rm,Rn

    def mov_b_r0_disp(self, disp, n):   # mov.b R0,@(disp,Rn)
        check(0 <= disp < 16, 'disp4')
        self.op(0x8000 | n << 4 | disp)

    def mov_w_r0_disp(self, disp, n):   # mov.w R0,@(disp,Rn)
        check(0 <= disp < 32 and not disp & 1, 'disp4*2')
        self.op(0x8100 | n << 4 | disp >> 1)

    def dt(self, n): self.op(0x4010 | n << 8)
    def jsr(self, m): self.op(0x400b | m << 8)
    def bsrf(self, m): self.op(0x0003 | m << 8)
    def mul_l(self, m, n): self.op(0x0007 | n << 8 | m << 4)
    def dmuls_l(self, m, n): self.op(0x300d | n << 8 | m << 4)

    def branch(self, opcode, bits, target):
        at = self.pc
        off = len(self.buf)
        self.op(opcode)
        lim = 1 << (bits - 1)

        def fix(o):
            d = self.addr(target) - (at + 4)
            check(not d & 1 and -lim <= d // 2 < lim, 'branch out of range')
            self.put16(o, opcode | (d // 2) & ((1 << bits) - 1))
        self.fixups.append((off, fix))

    def bf(self, t): self.branch(0x8b00, 8, t)
    def bt(self, t): self.branch(0x8900, 8, t)
    def bra(self, t): self.branch(0xa000, 12, t)
    def bsr(self, t): self.branch(0xb000, 12, t)

    def mov_l_lit(self, value, n):
        """mov.l @(disp,PC),Rn loading value (an int, label or function)."""
        name = '_lit%d' % self.nlits
        self.nlits += 1
        self.lits.append((name, value))
        at = self.pc
        off = len(self.buf)
        self.op(0xd000 | n << 8)

        def fix(o):
            d = self.labels[name] - ((at & ~3) + 4)
            check(d >= 0 and not d & 3 and d // 4 < 256, 'literal out of range')
            self.put16(o, 0xd000 | n << 8 | d // 4)
        self.fixups.append((off, fix))

    def pool(self):
        self.align(4, 0)
        for name, value in self.lits:
            self.label(name)
            off = len(self.buf)
            self.w32(0)
            self.fixups.append((off, lambda o, v=value: self.put32(o, self.addr(v))))
        self.lits = []
