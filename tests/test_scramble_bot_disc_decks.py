import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
GENERATOR_PATH = ROOT / "scripts" / "generate_scramble_bot_disc_decks.py"
spec = importlib.util.spec_from_file_location("scramble_deck_generator", GENERATOR_PATH)
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class ScrambleBotDiscDeckTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        literals = generator.source_literals(
            ROOT / "scripts" / "build-action-asset-bundles.py",
            {"CAST_CATEGORIES", "CAST_TIMES"},
        )
        cls.categories = tuple(literals["CAST_CATEGORIES"])
        cls.cast_times = literals["CAST_TIMES"]
        cls.cards, _, _ = generator.make_card_catalog()
        cls.role_map = generator.kicker_roles()
        cls.base_ai_rows = [
            row for row in generator.read_json("config/masters_kicker_ai_disc.json")
            if row["id"] < generator.GENERATED_ID_START
        ]

    def test_every_profile_preserves_required_function_and_unique_variants(self):
        ai_rows, deck_rows, catalog = generator.generate_decks(
            self.cards, self.role_map, self.categories, self.cast_times, self.base_ai_rows
        )
        existing_decks = [
            row for row in generator.read_json("config/masters_kicker_ai_disc_deck.json")
            if row["id"] < generator.GENERATED_ID_START
        ]
        generator.validate_outputs(self.base_ai_rows + ai_rows, existing_decks + deck_rows, catalog)

        for item in catalog:
            tags = [self.cards[disc_id]["tags"] for disc_id in item["discIds"]]
            profile = item["profile"]
            if profile == "carrier":
                self.assertTrue(any("mobility" in value for value in tags))
                self.assertTrue(any("safe_escape" in value for value in tags))
            elif profile == "hunter":
                self.assertGreaterEqual(sum("attack" in value for value in tags), 2)
            elif profile == "objective_attacker":
                self.assertTrue(any("rush" in value for value in tags))
                self.assertTrue(any("mobility" in value for value in tags))
            elif profile == "healer":
                self.assertTrue(any("ally_hp_heal" in value for value in tags))
            elif profile == "speed_support":
                self.assertTrue(any("team_move_speed" in value for value in tags))
            elif profile == "trap_defender":
                self.assertGreaterEqual(sum("trap" in value for value in tags), 2)
            elif profile == "damage_support":
                self.assertTrue(any("team_attack_buff" in value for value in tags))
            elif profile == "tank_defender":
                self.assertTrue(any("team_damage_reduction" in value for value in tags))

    def test_cast_time_changes_same_profile_required_card(self):
        selected = {}
        for kicker_id, role_type in ((1, 0), (2, 1)):
            _, _, catalog = generator.generate_decks(
                self.cards, {kicker_id: role_type}, self.categories, self.cast_times, []
            )
            healer_variant = next(
                row for row in catalog
                if row["profile"] == "healer" and row["variant"] == 1
            )
            selected[kicker_id] = healer_variant["discIds"][0]

        # Tsubame reaches WARP faster (1.1 s) than HEAL (1.2 s); Ruriha's
        # HEAL cast is 0.7 s and her passive rewards that category.
        self.assertEqual(selected[1], 3010068)  # Neolumi: low-HP ally heal via WARP
        self.assertEqual(selected[2], 3010065)  # Phoenora: team HP heal via HEAL
        self.assertNotEqual(selected[1], selected[2])


if __name__ == "__main__":
    unittest.main()
