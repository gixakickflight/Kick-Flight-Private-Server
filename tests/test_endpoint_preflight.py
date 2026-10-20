from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "patch-il2cpp-endpoints.py"
spec = importlib.util.spec_from_file_location("patch_il2cpp_endpoints", SCRIPT)
patcher = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(patcher)


class EndpointPreflightTests(unittest.TestCase):
    def test_production_config_exists_and_matches_contract(self) -> None:
        config_path = ROOT / "config" / "apk-direct-server.production.json"
        self.assertTrue(config_path.is_file(), f"Missing config: {config_path}")
        config = json.loads(config_path.read_text(encoding="utf-8"))
        self.assertEqual(config["serverBaseUrl"], "http://159.54.134.159:18080")
        self.assertEqual(config["photonHost"], "159.54.134.159")
        self.assertEqual(config["versionName"], "2.11.5")
        self.assertEqual(config["versionCode"], 60)
        self.assertEqual(
            config["expectedSourceSha256"].lower(),
            "ffa620d2e2f905e729812606024e02741124951dce908bb2e0587f0a4e16e031".lower(),
        )

    def test_valid_ip_passes_preflight(self) -> None:
        url, authority, photon = patcher.validate_endpoint_preflight(
            "http://159.54.134.159:18080",
            photon_host="159.54.134.159",
            skip_dns_check=False,
        )
        self.assertEqual(url, "http://159.54.134.159:18080")
        self.assertEqual(authority, "159.54.134.159:18080")
        self.assertEqual(photon, "159.54.134.159")

    def test_unresolvable_nxdomain_fails_preflight(self) -> None:
        with self.assertRaises(ValueError) as ctx:
            patcher.validate_endpoint_preflight(
                "http://kickflightsg.ddns.net:18080",
                skip_dns_check=False,
            )
        self.assertIn("kickflightsg.ddns.net", str(ctx.exception))
        self.assertIn("cannot be resolved", str(ctx.exception))

    def test_invalid_scheme_fails(self) -> None:
        with self.assertRaises(ValueError) as ctx:
            patcher.validate_endpoint_preflight(
                "https://159.54.134.159:18080",
                skip_dns_check=True,
            )
        self.assertIn("serverBaseUrl must use http://", str(ctx.exception))

    def test_skip_preflight_bypasses_dns_check(self) -> None:
        url, authority, photon = patcher.validate_endpoint_preflight(
            "http://kickflightsg.ddns.net:18080",
            photon_host="159.54.134.159",
            skip_dns_check=True,
        )
        self.assertEqual(url, "http://kickflightsg.ddns.net:18080")
        self.assertEqual(authority, "kickflightsg.ddns.net:18080")
        self.assertEqual(photon, "159.54.134.159")


if __name__ == "__main__":
    unittest.main()
