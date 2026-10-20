"""Execute the actual ARM64 deposit cave, stubbing only the game's HP accessor.

Optional RE dependencies: keystone-engine, capstone, unicorn==2.1.4.
This checks machine-code behavior, not Android gameplay or native HP decoding.
"""
import struct
import unittest
import sys
import runpy
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scripts.re.ai_role_deposit_cave import generate_deposit_cave_bytes, get_deposit_patches
from scripts.re.ai_role_cave import CAVE_DEPOSIT_START, RET_SCRAMBLE_DEPOSIT, TARGET_DAMAGE_RATE_RVA, LOG_PRINT_PLT
from scripts.re.ai_role_diag_read import count_low_hp_deposit_events, parse_kfdiag_lines, decode_probe_value

try:
    from unicorn import Uc, UC_ARCH_ARM64, UC_MODE_ARM, UC_HOOK_CODE
    from unicorn import arm64_const as reg
except ImportError:
    Uc = None


@unittest.skipIf(Uc is None, 'Install optional RE dependency unicorn==2.1.4')
class DepositMachineCodeTests(unittest.TestCase):
    def run_cave(self, *, profile=15, enabled=True, crystals=1.0, damage=.7,
                 owner=True, parameter=True, threshold=5):
        machine = Uc(UC_ARCH_ARM64, UC_MODE_ARM)
        for address in (CAVE_DEPOSIT_START, RET_SCRAMBLE_DEPOSIT, TARGET_DAMAGE_RATE_RVA, LOG_PRINT_PLT):
            machine.mem_map(address & ~0xfff, 0x1000)
        machine.mem_map(0x500000, 0x2000)
        machine.mem_map(0x510000, 0x1000)
        machine.mem_map(0x600000, 0x10000)
        machine.mem_write(CAVE_DEPOSIT_START, generate_deposit_cave_bytes())
        machine.mem_write(TARGET_DAMAGE_RATE_RVA, bytes.fromhex('c0035fd6'))
        machine.mem_write(LOG_PRINT_PLT, bytes.fromhex('c0035fd6'))
        machine.mem_write(0x510018, struct.pack('<Q', 0x500000 if owner else 0))
        machine.mem_write(0x510020, struct.pack('<i', threshold))
        machine.mem_write(0x5000c4, struct.pack('<i', profile))
        machine.mem_write(0x5000e0, bytes([enabled]))
        machine.mem_write(0x500180, struct.pack('<Q', 0x520000 if parameter else 0))
        registers = [getattr(reg, f'UC_ARM64_REG_X{i}') for i in range(31)]
        vectors = [getattr(reg, f'UC_ARM64_REG_Q{i}') for i in range(32)]
        for i, register in enumerate(registers):
            machine.reg_write(register, 0x1000 + i)
        for i, register in enumerate(vectors):
            machine.reg_write(register, (0x123456789abcdef << 64) | (0x7000 + i))
        machine.reg_write(reg.UC_ARM64_REG_X19, 0x510000)
        machine.reg_write(reg.UC_ARM64_REG_Q0, struct.unpack('<I', struct.pack('<f', crystals))[0])
        machine.reg_write(reg.UC_ARM64_REG_SP, 0x60ff00)
        machine.reg_write(reg.UC_ARM64_REG_NZCV, 0xa0000000)
        original = [machine.reg_read(r) for r in registers + vectors]
        calls = []
        probes = []

        def accessor_stub(cpu, address, size, _):
            if address not in (TARGET_DAMAGE_RATE_RVA, LOG_PRINT_PLT):
                return
            if address == TARGET_DAMAGE_RATE_RVA:
                calls.append(cpu.reg_read(reg.UC_ARM64_REG_X0))
            else:
                self.assertEqual(cpu.reg_read(reg.UC_ARM64_REG_X0), 3)
                self.assertEqual(bytes(cpu.mem_read(cpu.reg_read(reg.UC_ARM64_REG_X1), 7)), b'KFDIAG\0')
                self.assertEqual(bytes(cpu.mem_read(cpu.reg_read(reg.UC_ARM64_REG_X2), 5)), b'v=%d\0')
                probes.append(cpu.reg_read(reg.UC_ARM64_REG_X3))
            for i in range(19):
                cpu.reg_write(registers[i], 0xdead0000 + i)
            for i in range(32):
                cpu.reg_write(vectors[i], 0xdeadbeef)
            cpu.reg_write(reg.UC_ARM64_REG_Q0, struct.unpack('<I', struct.pack('<f', damage))[0])
            cpu.reg_write(reg.UC_ARM64_REG_NZCV, 0x50000000)

        machine.hook_add(UC_HOOK_CODE, accessor_stub)
        machine.emu_start(CAVE_DEPOSIT_START, RET_SCRAMBLE_DEPOSIT, count=300)
        self.assertEqual(machine.reg_read(reg.UC_ARM64_REG_PC), RET_SCRAMBLE_DEPOSIT)
        final = [machine.reg_read(r) for r in registers + vectors]
        self.assertEqual(final[:31], original[:31], 'GPR/LR corruption')
        for index in range(32):
            if index != 1:
                self.assertEqual(final[31 + index], original[31 + index], f'q{index} corruption')
        self.assertEqual(machine.reg_read(reg.UC_ARM64_REG_SP), 0x60ff00)
        self.assertEqual(machine.reg_read(reg.UC_ARM64_REG_NZCV), 0xa0000000)
        self.assertEqual(probes, [7650] if machine.reg_read(reg.UC_ARM64_REG_Q1) == 1 else [])
        return machine.reg_read(reg.UC_ARM64_REG_Q1), calls

    def test_low_hp_changes_integer_threshold_and_preserves_context(self):
        self.assertEqual(self.run_cave(), (1, [0x500000]))
        self.assertEqual(self.run_cave(damage=.65), (1, [0x500000]))
        self.assertEqual(self.run_cave(damage=1), (1, [0x500000]))

    def test_healthy_zero_crystals_and_nan_preserve_threshold(self):
        self.assertEqual(self.run_cave(damage=.649)[0], 5)
        self.assertEqual(self.run_cave(damage=float('nan'))[0], 5)
        self.assertEqual(self.run_cave(crystals=0), (5, []))
        self.assertEqual(self.run_cave(crystals=float('nan')), (5, []))

    def test_other_profiles_modes_humans_and_gym_never_query_hp(self):
        for profile in (-1, 0, 1, 14, 16, 17, 23, 28, 41, 53, 99, 100, 114):
            with self.subTest(profile=profile):
                self.assertEqual(self.run_cave(profile=profile), (5, []))
        self.assertEqual(self.run_cave(enabled=False), (5, []))

    def test_missing_owner_or_parameter_preserves_threshold_without_call(self):
        self.assertEqual(self.run_cave(owner=False), (5, []))
        self.assertEqual(self.run_cave(parameter=False), (5, []))


class DepositPatchGuardTests(unittest.TestCase):
    def test_threshold_event_is_not_reported_as_a_selected_target(self):
        lines = ['D KFDIAG : v=7650', 'D KFDIAG : v=76501', 'D Other : v=7650']
        self.assertEqual(count_low_hp_deposit_events(lines), 1)
        self.assertEqual(parse_kfdiag_lines(lines), [])
        self.assertEqual(decode_probe_value(7650)['type'], 'LOW_HP_DEPOSIT_THRESHOLD')

    def test_displaced_instruction_and_cave_bounds(self):
        cave, hook = get_deposit_patches()
        self.assertEqual(len(cave['expected']), len(cave['replacement']))
        self.assertEqual(cave['replacement'][:4], hook['expected'])
        self.assertEqual(hook['expected'], bytes.fromhex('612240bd'))
        self.assertLessEqual(cave['offset'] + len(cave['replacement']), 0x159da44)
        self.assertGreaterEqual(cave['offset'], 0x159d854)

    def test_wrong_hook_rejects_without_partial_file_writes(self):
        root = Path(__file__).resolve().parents[1]
        patcher = runpy.run_path(str(root / 'scripts/patch-il2cpp-endpoints.py'), run_name='guard_probe')
        patches = get_deposit_patches()
        patcher['NATIVE_PATCHES']['arm64-v8a'] = patches
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'libil2cpp.so'
            with target.open('wb') as stream:
                for patch in patches:
                    stream.seek(patch['offset'])
                    stream.write(patch['expected'])
                stream.seek(patches[-1]['offset'])
                stream.write(b'\xff\xff\xff\xff')
            before = target.read_bytes()
            with self.assertRaisesRegex(ValueError, 'guard failed at 0x1808d40'):
                patcher['patch_native'](target, 'arm64-v8a')
            self.assertEqual(target.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
