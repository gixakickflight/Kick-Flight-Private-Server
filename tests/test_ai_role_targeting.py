#!/usr/bin/env python3
"""test_ai_role_targeting.py - Focused native policy and assembly tests for AI role candidate targeting."""
import unittest
from pathlib import Path
import sys

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO))

from scripts.re.ai_role_cave import (
    FREE_CAVE_WINDOW_START,
    FREE_CAVE_WINDOW_END,
    CAVE_TARGETING_START,
    HOOK_UPDATE_TARGET,
    HOOK_UPDATE_TARGET_SCORING,
    RET_UPDATE_TARGET_SCORING,
    generate_ai_role_cave_bytes,
    generate_ai_role_targeting_cave_bytes,
    get_ai_role_targeting_patches,
    get_ai_roles_patches,
    simulate_native_target_scoring,
)


class AIRoleScoringPolicyTests(unittest.TestCase):
    """Test the exact mathematical effect of role weighting on candidate scores."""

    def test_hunter_id17_prefers_hostile_player_over_guardian(self):
        # Equal input score 10.0 for player and guardian
        player_score = simulate_native_target_scoring(
            candidate_score=10.0,
            evaluator_class="AIAttackEvaluationEngine",
            enable_ai=True,
            kicker_ai_param_id=17,
            target_class="PlayerCharacter",
        )
        guardian_score = simulate_native_target_scoring(
            candidate_score=10.0,
            evaluator_class="AIAttackEvaluationEngine",
            enable_ai=True,
            kicker_ai_param_id=17,
            target_class="NPCGuardian",
        )
        self.assertEqual(player_score, 40.0)
        self.assertEqual(guardian_score, 2.5)
        # Decisive 16:1 preference ratio for hostile player over guardian
        self.assertEqual(player_score / guardian_score, 16.0)

    def test_objective_id18_and_23_prefer_hostile_guardian_over_player(self):
        for role_id in (18, 23):
            guardian_score = simulate_native_target_scoring(
                candidate_score=10.0,
                evaluator_class="AIAttackEvaluationEngine",
                enable_ai=True,
                kicker_ai_param_id=role_id,
                target_class="NPCGuardian",
            )
            player_score = simulate_native_target_scoring(
                candidate_score=10.0,
                evaluator_class="AIAttackEvaluationEngine",
                enable_ai=True,
                kicker_ai_param_id=role_id,
                target_class="PlayerCharacter",
            )
            self.assertEqual(guardian_score, 40.0)
            self.assertEqual(player_score, 2.5)
            # Decisive 16:1 preference ratio for hostile guardian over player
            self.assertEqual(guardian_score / player_score, 16.0)

    def test_preserves_zero_and_invalid_candidate_scores(self):
        # 0.0 represents friendly target, dead entity, out-of-range, or infeasible
        for role_id in (17, 18, 23):
            for target in ("PlayerCharacter", "NPCGuardian"):
                zero_score = simulate_native_target_scoring(
                    candidate_score=0.0,
                    evaluator_class="AIAttackEvaluationEngine",
                    enable_ai=True,
                    kicker_ai_param_id=role_id,
                    target_class=target,
                )
                self.assertEqual(zero_score, 0.0)

                negative_score = simulate_native_target_scoring(
                    candidate_score=-5.0,
                    evaluator_class="AIAttackEvaluationEngine",
                    enable_ai=True,
                    kicker_ai_param_id=role_id,
                    target_class=target,
                )
                self.assertEqual(negative_score, -5.0)

    def test_preserves_non_attack_evaluators(self):
        # AIScrambleScoreEvaluationEngine and AIDefensekEvaluationEngine must not be weighted
        for evaluator in ("AIScrambleScoreEvaluationEngine", "AIDefensekEvaluationEngine"):
            for role_id in (17, 18, 23):
                score = simulate_native_target_scoring(
                    candidate_score=15.0,
                    evaluator_class=evaluator,
                    enable_ai=True,
                    kicker_ai_param_id=role_id,
                    target_class="PlayerCharacter",
                )
                self.assertEqual(score, 15.0)

    def test_preserves_unrelated_roles_and_modes(self):
        # Mode 1 (Flag Flight IDs 28..40), Mode 2 (Rapid Ball IDs 41..53), Carrier (15), Tank (16)
        unrelated_ids = [1, 2, 8, 15, 16, 19, 28, 30, 31, 41, 43, 44, 100, 101]
        for role_id in unrelated_ids:
            score = simulate_native_target_scoring(
                candidate_score=10.0,
                evaluator_class="AIAttackEvaluationEngine",
                enable_ai=True,
                kicker_ai_param_id=role_id,
                target_class="PlayerCharacter",
            )
            self.assertEqual(score, 10.0)

    def test_preserves_human_players(self):
        score = simulate_native_target_scoring(
            candidate_score=10.0,
            evaluator_class="AIAttackEvaluationEngine",
            enable_ai=False,
            kicker_ai_param_id=17,
            target_class="PlayerCharacter",
        )
        self.assertEqual(score, 10.0)


class AIRoleTargetingAssemblyTests(unittest.TestCase):
    """Test assembly size, boundary containment, displaced instructions, and patch maps."""

    def test_targeting_cave_bounds_and_alignment(self):
        telemetry_bytes = generate_ai_role_cave_bytes()
        targeting_bytes = generate_ai_role_targeting_cave_bytes()

        self.assertEqual(len(telemetry_bytes), 716)
        self.assertEqual(len(targeting_bytes), 372)

        telemetry_end = FREE_CAVE_WINDOW_START + len(telemetry_bytes)
        self.assertEqual(telemetry_end, 0x159D6DC)

        # Targeting cave starts at aligned boundary 0x159D6E0
        self.assertEqual(CAVE_TARGETING_START, 0x159D6E0)
        self.assertGreaterEqual(CAVE_TARGETING_START, telemetry_end)

        targeting_end = CAVE_TARGETING_START + len(targeting_bytes)
        self.assertEqual(targeting_end, 0x159D854)
        self.assertLessEqual(targeting_end, FREE_CAVE_WINDOW_END)

        remaining_free_bytes = FREE_CAVE_WINDOW_END - targeting_end
        self.assertEqual(remaining_free_bytes, 496)

    def test_displaced_instruction_execution(self):
        targeting_bytes = generate_ai_role_targeting_cave_bytes()
        # Displaced instruction at 0x18072D0: ldr x1, [x26] -> hex 41 03 40 f9
        ldr_count = targeting_bytes.count(bytes.fromhex("410340f9"))
        self.assertEqual(ldr_count, 1)

    def test_patch_map_default_off_and_enabled_composition(self):
        dummy_so = b"\x00" * 0x3000000
        # Telemetry only
        p_telem = get_ai_roles_patches(dummy_so, include_targeting=False)
        self.assertEqual(len(p_telem), 2)
        offsets_telem = [p["offset"] for p in p_telem]
        self.assertEqual(offsets_telem, [FREE_CAVE_WINDOW_START, HOOK_UPDATE_TARGET])

        # Telemetry + Targeting
        p_target = get_ai_roles_patches(dummy_so, include_targeting=True)
        self.assertEqual(len(p_target), 4)
        offsets_target = [p["offset"] for p in p_target]
        self.assertEqual(
            offsets_target,
            [
                FREE_CAVE_WINDOW_START,
                HOOK_UPDATE_TARGET,
                CAVE_TARGETING_START,
                HOOK_UPDATE_TARGET_SCORING,
            ],
        )


if __name__ == "__main__":
    unittest.main()
