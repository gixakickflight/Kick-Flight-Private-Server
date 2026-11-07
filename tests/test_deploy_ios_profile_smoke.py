import re
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEPLOY = ROOT / "scripts" / "deploy-vps-ci.sh"
STAGER = ROOT / "scripts" / "stage-ios-profile.py"


def _heredoc() -> str:
    text = DEPLOY.read_text(encoding="utf-8")
    match = re.search(r"python3 - \"\$release_dir/scripts/stage-ios-profile\.py\"[^\n]*<<'PY'\n(.*?)\nPY\n", text, re.S)
    assert match, "stage-ios-profile smoke heredoc not found"
    return match.group(1)


class DeployIosProfileSmokeTests(unittest.TestCase):
    def test_heredoc_loads_stager_without_preregistered_module(self):
        # Reaching _validate_database with an invalid body proves the module loaded
        # (dataclass on Python 3.12+ fails at import if sys.modules is not set).
        import tempfile
        with tempfile.NamedTemporaryFile() as body:
            result = subprocess.run(
                [sys.executable, "-I", "-", str(STAGER), body.name, "1", "1"],
                input=_heredoc(), text=True, capture_output=True,
            )
        self.assertNotIn("AttributeError", result.stderr)
        self.assertNotIn("'NoneType' object has no attribute '__dict__'", result.stderr)
        self.assertIn("ProfileError", result.stderr + result.stdout)


if __name__ == "__main__":
    unittest.main()
