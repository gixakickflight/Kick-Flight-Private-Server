# AI role runtime validation snapshot

Local Scramble, installed targeting APK57b2d07f70489aa4b3b23901f7ca7dffd39d5e05ef903e384093bd80f22f15e9. New deposit APK not installed. Capture is bounded and may finish before the game timer does.

```json
{
  "records": 166,
  "complete": 166,
  "partial": 0,
  "profiles": {
    "25": 50,
    "16": 29,
    "15": 35,
    "18": 52
  },
  "purpose_class": {
    "('Score', 'Other')": 82,
    "('Defense', 'PlayerCharacter')": 35,
    "('Attack', 'PlayerCharacter')": 45,
    "('Score', 'NPCGuardian')": 4
  },
  "attack_guardian_records": 0,
  "guardian_classification_markers": {
    "ScrambleObjAttackerVsGuardian": 1
  },
  "file_sha256": "0d466758452f4c215ff658b141651d22d12f9ea5a0cc392ed9cad9045d5d60ee",
  "first_frame": "10-19 18:15:40.554",
  "last_frame": "10-19 18:18:58.403"
}
```

Confirmed: framed real client decisions use experimental parameter IDs, so the catalog path fix reaches Android. Profile IDs are not mapped to named actors without roster evidence.

The observed objective-vs-guardian classification includes purposeScore; it does not prove an enemy-guardian attack or a weighting-induced target change. No Android low-HP threshold event7650 is expected from this older installed APK. Actual deposit completion, enemy-guardian preference and hunter preference remain unverified. ISA tests cover the new cave separately.

Private raw capture: `.local/run/logcat-sol-role-battle.txt`. Latest snapshot screenshot: `.local/run/sol-battle-state-720.png`. No cache clear, uninstall or production action.

## Installed deposit APK: real Android proof

After the first battle reached results, installed matching-cert APK with install-r and verified device hash. New run reached Title/Home/Matching/Game normally. Frozen trace `.local/run/logcat-sol-deposit-proof.txt`:

```json
{
  "complete": 156,
  "partial": 0,
  "profiles": {
    "17": 14,
    "16": 31,
    "18": 34,
    "25": 40,
    "15": 37
  },
  "low_hp_threshold_events": 2,
  "event_times": [
    "10-19 18:23:33.906",
    "10-19 18:24:15.218"
  ],
  "purpose_class": {
    "('Attack', 'PlayerCharacter')": 47,
    "('Defense', 'PlayerCharacter')": 30,
    "('Score', 'Other')": 74,
    "('Score', 'NPCGuardian')": 5
  },
  "snapshot_sha256": "da8143b696996896c9137b833daf4d90fef17e62e92568b0b54bc0e776a88b86",
  "apk_sha256": "3e50dd23d20e4e6c1ef29afd8fd2a3f4c27d9ebb199a81c0ac2a46a054c35cbb"
}
```

7650 is emitted only after the native cave stores integer1 into the saved threshold lane, with AI profile15, >=1 carried crystal and canonical damageRate>=0.65, after stock allied deposit-place validation. Thus the new threshold branch executed on Android. It does not prove which named bot, the exact crystal count/HP, a weighting-caused final target change or completed deposit. Current process8713 remained alive; full *:V buffer had0 current-process fatal markers. Screenshot `.local/run/sol-deposit-proof-720.png` shows an ongoing battle.
