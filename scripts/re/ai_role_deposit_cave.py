"""Opt-in Scramble carrier low-HP deposit threshold; verified ARM64 client only.

The stock evaluator already rejected non-deposit places and enemy teams before
this hook. Preserve its return/selection logic, changing only the integer threshold.
Native evidence: docs/re-notes/ai-role-low-hp-deposit.md.
"""
from scripts.re.ai_role_cave import (
    CAVE_DEPOSIT_START, FREE_CAVE_WINDOW_END, HOOK_SCRAMBLE_DEPOSIT,
    RET_SCRAMBLE_DEPOSIT, TARGET_DAMAGE_RATE_RVA,
    LOG_PRINT_PLT,
)

PROBE_LOW_HP_DEPOSIT_THRESHOLD = 7650

# Immutable bytes from the verified pristine 2.11.0 base; never derive guards
# from the input being patched. The corresponding published window is unchanged.
PRISTINE_WINDOW = bytes.fromhex(
    "690100b40b5940f9ea031faa6b2100916c815ff89f0101eb200100544a0500915f0109eb6b41009143ffff54e2031f32e00316aa9f14f19705000014690140b9290900110851298b00a10491080440a9e00316aa00013fd6f60300aa770000b5e0031faa4ddaf197e00317aae1031faae3d60294081ca04e760000b5e0031faa46daf19748e400d0003942bde00316aae1031faa0009201ec1e1fe97400340f9a20c4e94f60300aa560000b53bdaf197e00316aae1031faa2bf10794f60300aa560000b535daf197620340f9e00316aae103152a514c1294f60300aa560000b52edaf197e00316aae1031faa87d60294287101d0081143f9f603002a080140f9e00308aa890c4e94f70300aa570000b522daf197e5030032e00317aae103152ae203162ae3031faae4031f2ae6031faa64dbfa97771640f9f60300aa762600f9770000b5e0031faa14daf197e00317aae1031faaa0043994f70300aa760000b5e0031faa0ddaf197c80240f9e00316aae103152ae203142a099157a9e30317aa20013fd6742640f9740000b5e0031faa02daf197e00314aae1031faa137c1394601e00f9a8760190081941f9000140f93519f297e1031faaf40300aac9c70294742200f9200340f9752640f9089c44398800083608d840b948000035cc65f197e00315aae1031faae2031faad4483994"
)


def generate_deposit_cave_bytes():
    from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN
    code = ['ldr s1, [x19, #0x20]', 'sub sp, sp, #0x300']
    code += [f'stp x{i}, x{i+1}, [sp, #{i*8}]' for i in range(0, 30, 2)]
    code += ['str x30, [sp, #0xf0]', 'mrs x9, nzcv', 'str x9, [sp, #0xf8]']
    code += [f'stp q{i}, q{i+1}, [sp, #{0x100+i*16}]' for i in range(0, 32, 2)]
    code += [
        'ldr x0, [x19, #0x18]', 'cbz x0, restore',
        'ldrb w9, [x0, #0xe0]', 'cbz w9, restore',
        'ldr w9, [x0, #0xc4]', 'cmp w9, #15', 'b.ne restore',
        'ldr x9, [x0, #0x180]', 'cbz x9, restore',
        'fmov s1, #1.0', 'fcmp s0, s1', 'b.vs restore', 'b.lt restore',
        'mov x1, xzr', f'bl 0x{TARGET_DAMAGE_RATE_RVA:x}',
        'mov w9, #0x6666', 'movk w9, #0x3f26, lsl #16',
        'fmov s1, w9', 'fcmp s0, s1', 'b.vs restore', 'b.lt restore',
        # s1 is subsequently converted from integer bits by stock SCVTF.
        'mov w9, #1', 'str w9, [sp, #0x110]',
        'mov w0, #3', 'adr x1, log_tag', 'adr x2, log_format',
        f'mov w3, #{PROBE_LOW_HP_DEPOSIT_THRESHOLD}', f'bl 0x{LOG_PRINT_PLT:x}',
        'restore:',
    ]
    code += [f'ldp q{i}, q{i+1}, [sp, #{0x100+i*16}]' for i in range(0, 32, 2)]
    code += ['ldr x9, [sp, #0xf8]', 'msr nzcv, x9', 'ldr x30, [sp, #0xf0]']
    code += [f'ldp x{i}, x{i+1}, [sp, #{i*8}]' for i in range(0, 30, 2)]
    code += ['add sp, sp, #0x300', f'b 0x{RET_SCRAMBLE_DEPOSIT:x}']
    code += ['log_tag:', '.asciz "KFDIAG"', 'log_format:', '.asciz "v=%d"']
    encoded, _ = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN).asm('\n'.join(code), CAVE_DEPOSIT_START)
    result = bytes(encoded)
    if CAVE_DEPOSIT_START + len(result) > FREE_CAVE_WINDOW_END:
        raise ValueError('Low-HP deposit cave exceeds verified free window')
    return result


def get_deposit_patches():
    from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN
    cave = generate_deposit_cave_bytes()
    branch, _ = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN).asm(
        f'b 0x{CAVE_DEPOSIT_START:x}', HOOK_SCRAMBLE_DEPOSIT)
    return [
        {'description': 'cave: experimental Scramble carrier low-HP deposit',
         'offset': CAVE_DEPOSIT_START, 'expected': PRISTINE_WINDOW[:len(cave)], 'replacement': cave},
        {'description': 'hook: Scramble carrier deposit threshold',
         'offset': HOOK_SCRAMBLE_DEPOSIT, 'expected': bytes.fromhex('612240bd'), 'replacement': bytes(branch)},
    ]
