"""Exercise the real patcher flag guards without opening or writing a binary."""
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROBE = """
import json, runpy
try:
    module = runpy.run_path('scripts/patch-il2cpp-endpoints.py', run_name='flag_probe')
except SystemExit as exc:
    print(json.dumps({'rejected': str(exc)}))
else:
    roles = module['AI_ROLES_PATCHES_ARM64']
    overlaps = []
    for role in roles:
        for other in module['NATIVE_PATCHES']['arm64-v8a']:
            if other is role:
                continue
            if max(role['offset'], other['offset']) < min(
                role['offset'] + len(role['replacement']),
                other['offset'] + len(other['replacement'])):
                overlaps.append([role['description'], other['description']])
    print(json.dumps({'patches': len(roles), 'overlaps': overlaps}))
"""


class AIRoleFlagSafetyTests(unittest.TestCase):
    def probe(self, **flags):
        env = {key: value for key, value in os.environ.items() if not key.startswith('KF_')}
        env.update(flags)
        result = subprocess.run([sys.executable, '-c', PROBE], cwd=ROOT,
                                env=env, capture_output=True, text=True, check=True)
        return json.loads(result.stdout)

    def test_overlapping_diagnostic_caves_are_rejected(self):
        for targeting in ('0', '1'):
            with self.subTest(targeting=targeting):
                result = self.probe(KF_AI_ROLES='1', KF_AI_ROLE_TARGETING=targeting, KF_DIAG='1')
                self.assertIn('mutually exclusive', result['rejected'])
                self.assertIn('0x159D410', result['rejected'])

    def test_targeting_requires_roles(self):
        result = self.probe(KF_AI_ROLE_TARGETING='1')
        self.assertIn('requires KF_AI_ROLES=1', result['rejected'])

    def test_deposit_requires_roles_and_rejects_full_diag(self):
        self.assertIn('requires KF_AI_ROLES=1', self.probe(KF_AI_ROLE_DEPOSIT='1')['rejected'])
        self.assertIn('mutually exclusive', self.probe(
            KF_AI_ROLE_DEPOSIT='1', KF_AI_ROLES='1', KF_DIAG='1')['rejected'])

    def test_default_and_supported_experimental_sets(self):
        self.assertEqual(self.probe()['patches'], 0)
        self.assertEqual(self.probe(KF_AI_ROLES='1')['patches'], 2)
        self.assertEqual(self.probe(KF_AI_ROLES='1', KF_AI_ROLE_TARGETING='1')['patches'], 4)
        self.assertEqual(self.probe(KF_AI_ROLES='1', KF_AI_ROLE_DEPOSIT='1')['patches'], 4)
        self.assertEqual(self.probe(KF_AI_ROLES='1', KF_AI_ROLE_TARGETING='1', KF_AI_ROLE_DEPOSIT='1')['patches'], 6)

    def test_effective_role_patch_map_has_no_overlapping_writes(self):
        for extra in ({}, {'KF_DAMAGE_DIAG': '1', 'KF_PHOTON': '0'}, {'KF_RESULT_DIAG': '1'}):
            with self.subTest(extra=extra):
                result = self.probe(KF_AI_ROLES='1', KF_AI_ROLE_TARGETING='1', KF_AI_ROLE_DEPOSIT='1', **extra)
                self.assertEqual(result['overlaps'], [])


if __name__ == '__main__':
    unittest.main()
