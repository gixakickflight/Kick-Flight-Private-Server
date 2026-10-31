#!/usr/bin/env python3
"""test_ai_role_telemetry.py - Comprehensive tests for AI role telemetry, parser, cave safety, and byte guards."""
import unittest
from pathlib import Path
import sys

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO))

from scripts.re.ai_role_cave import (
    PROBE_FRAME_START,
    PROBE_PROFILE_ID_BASE,
    PROBE_PURPOSE_BASE,
    PROBE_CLASS_BASE,
    PROBE_TEAM_BASE,
    PROBE_SCRAMBLE_HUNTER,
    PROBE_SCRAMBLE_OBJ_ATTACKER,
    PROBE_SCORE_BASE,
    PROBE_FRAME_END,
    FREE_CAVE_WINDOW_START,
    FREE_CAVE_WINDOW_END,
    HOOK_UPDATE_TARGET,
    generate_ai_role_cave_bytes,
    audit_cave_safety,
    validate_negative_guards,
    get_ai_roles_patches,
)
from scripts.re.ai_role_diag_read import (
    decode_probe_value,
    parse_kfdiag_lines,
    AIRoleDecisionRecord,
)


class AIRoleTelemetryRangeTests(unittest.TestCase):
    """Verify that all probe ranges are strictly disjoint and non-overlapping."""

    def test_probe_ranges_have_zero_overlap(self):
        ranges = {
            "FRAME_START": {PROBE_FRAME_START},
            "PROFILE_ID": set(range(PROBE_PROFILE_ID_BASE + 15, PROBE_PROFILE_ID_BASE + 54)),  # 7515..7553
            "PURPOSE": set(range(PROBE_PURPOSE_BASE + 0, PROBE_PURPOSE_BASE + 3)),             # 7560..7562
            "TARGET_CLASS": set(range(PROBE_CLASS_BASE + 0, PROBE_CLASS_BASE + 4)),        # 7570..7573
            "TARGET_TEAM": set(range(PROBE_TEAM_BASE + 0, PROBE_TEAM_BASE + 2)),           # 7580..7581
            "SCRAMBLE_AUDIT": {PROBE_SCRAMBLE_HUNTER, PROBE_SCRAMBLE_OBJ_ATTACKER},       # 7590, 7591
            "SCORE": set(range(PROBE_SCORE_BASE + 0, PROBE_SCORE_BASE + 50)),             # 7600..7649
            "FRAME_END": {PROBE_FRAME_END},                                                # 7699
        }

        names = list(ranges.keys())
        for i in range(len(names)):
            for j in range(i + 1, len(names)):
                n1, n2 = names[i], names[j]
                s1, s2 = ranges[n1], ranges[n2]
                overlap = s1.intersection(s2)
                self.assertEqual(
                    len(overlap),
                    0,
                    f"Collision detected between {n1} and {n2}: {overlap}",
                )

    def test_profile_id_contract_coverage(self):
        # Server/profile contract specifies IDs 15..53
        for pid in range(15, 54):
            val = PROBE_PROFILE_ID_BASE + pid
            dec = decode_probe_value(val)
            self.assertEqual(dec["type"], "PROFILE_ID")
            self.assertEqual(dec["profile_id"], pid)


class AIRoleParserTests(unittest.TestCase):
    """Verify parser decoding and timeline formatting."""

    def test_parse_complete_frame(self):
        sample_log = [
            "10-19 12:00:01.100  1000  1000 D KFDIAG  : v=7500",
            "10-19 12:00:01.101  1000  1000 D KFDIAG  : v=7517", # Profile ID 17 (Scramble Hunter)
            "10-19 12:00:01.102  1000  1000 D KFDIAG  : v=7560", # Purpose 0 (Attack)
            "10-19 12:00:01.103  1000  1000 D KFDIAG  : v=7572", # Class 2 (NPCGuardian)
            "10-19 12:00:01.104  1000  1000 D KFDIAG  : v=7581", # Team 1
            "10-19 12:00:01.105  1000  1000 D KFDIAG  : v=7590", # Scramble Hunter vs Guardian audit
            "10-19 12:00:01.106  1000  1000 D KFDIAG  : v=7625", # Score 25
            "10-19 12:00:01.107  1000  1000 D KFDIAG  : v=7699",
        ]
        records = parse_kfdiag_lines(sample_log)
        self.assertEqual(len(records), 1)
        r = records[0]
        self.assertTrue(r.complete)
        self.assertEqual(r.profile_id, 17)
        self.assertEqual(r.purpose_name, "Attack")
        self.assertEqual(r.target_class_name, "NPCGuardian")
        self.assertEqual(r.target_team_name, "Team1")
        self.assertEqual(r.scramble_audit, "ScrambleHunterVsGuardian")
        self.assertEqual(r.score, 25)

    def test_parse_no_target_frame(self):
        sample_log = [
            "10-19 12:00:02.000  1000  1000 D KFDIAG  : v=7500",
            "10-19 12:00:02.001  1000  1000 D KFDIAG  : v=7523", # Profile ID 23
            "10-19 12:00:02.002  1000  1000 D KFDIAG  : v=7562", # Purpose 2 (Score)
            "10-19 12:00:02.003  1000  1000 D KFDIAG  : v=7570", # Target Class None
            "10-19 12:00:02.004  1000  1000 D KFDIAG  : v=7600", # Score 0
            "10-19 12:00:02.005  1000  1000 D KFDIAG  : v=7699",
        ]
        records = parse_kfdiag_lines(sample_log)
        self.assertEqual(len(records), 1)
        r = records[0]
        self.assertTrue(r.complete)
        self.assertEqual(r.profile_id, 23)
        self.assertEqual(r.purpose_name, "Score")
        self.assertEqual(r.target_class_name, "None")
        self.assertIsNone(r.target_team)
        self.assertEqual(r.score, 0)


class AIRoleCaveSafetyTests(unittest.TestCase):
    """Verify cave generation, size, window bounds, and negative guards."""

    def test_cave_fits_in_free_window(self):
        cave_bytes = generate_ai_role_cave_bytes()
        start = FREE_CAVE_WINDOW_START
        end = start + len(cave_bytes)
        self.assertLessEqual(end, FREE_CAVE_WINDOW_END)
        self.assertEqual(len(cave_bytes), 716)

    def test_displaced_instruction_once(self):
        # Must execute `str w0, [x19, #0x7c]` (hex 60 7e 00 b9) exactly once in cave
        cave_bytes = generate_ai_role_cave_bytes()
        str_w0_count = cave_bytes.count(bytes.fromhex("607e00b9"))
        self.assertEqual(str_w0_count, 1)

    def test_negative_guard_rejects_stale_or_corrupt_data(self):
        pristine = b"\x00" * 0x2000000
        published = b"\x00" * 0x2000000
        with self.assertRaises(AssertionError):
            # Identical bytes should fail the negative guard (clean separation)
            validate_negative_guards(published, pristine)


if __name__ == "__main__":
    unittest.main()
