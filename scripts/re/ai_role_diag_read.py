#!/usr/bin/env python3
"""ai_role_diag_read.py - Unambiguous decoder & timeline reporter for AI role telemetry probes.

Delimited records & disjoint probe schema:
  - 7500: Record Frame Start
  - 7515..7553: Bot Profile ID (7500 + kickerAiParameterId in 15..53)
  - 7560..7562: Purpose (0=Attack, 1=Defense, 2=Score)
  - 7570..7573: Target Class (0=None, 1=PlayerCharacter, 2=NPCGuardian, 3=Other)
  - 7580..7581: Target Team (0=Team0, 1=Team1)
  - 7590: Scramble Hunter vs Guardian audit
  - 7591: Scramble Objective Attacker vs Guardian audit
  - 7600..7649: Clamped Target Score (0..49)
  - 7650: Carrier low-HP deposit threshold lowered (candidate evaluation only)
  - 7699: Record Frame End
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional

PURPOSE_NAMES = {
    0: "Attack",
    1: "Defense",
    2: "Score",
}

CLASS_NAMES = {
    0: "None",
    1: "PlayerCharacter",
    2: "NPCGuardian",
    3: "Other",
}

TEAM_NAMES = {
    0: "Team0",
    1: "Team1",
}

SCRAMBLE_AUDIT_NAMES = {
    7590: "ScrambleHunterVsGuardian",
    7591: "ScrambleObjAttackerVsGuardian",
}


@dataclass
class AIRoleDecisionRecord:
    timestamp: str
    profile_id: Optional[int] = None
    purpose: Optional[int] = None
    purpose_name: Optional[str] = None
    target_class: Optional[int] = None
    target_class_name: Optional[str] = None
    target_team: Optional[int] = None
    target_team_name: Optional[str] = None
    score: Optional[int] = None
    scramble_audit: Optional[str] = None
    complete: bool = False


def decode_probe_value(val: int) -> dict:
    """Decodes a single disjoint probe integer into its semantic field."""
    if val == 7500:
        return {"type": "FRAME_START"}
    if val == 7699:
        return {"type": "FRAME_END"}
    if 7515 <= val <= 7553:
        pid = val - 7500
        return {"type": "PROFILE_ID", "profile_id": pid}
    if 7560 <= val <= 7562:
        purp = val - 7560
        return {"type": "PURPOSE", "purpose": purp, "purpose_name": PURPOSE_NAMES.get(purp, "Unknown")}
    if 7570 <= val <= 7573:
        cls = val - 7570
        return {"type": "CLASS", "target_class": cls, "target_class_name": CLASS_NAMES.get(cls, "Unknown")}
    if 7580 <= val <= 7581:
        tm = val - 7580
        return {"type": "TEAM", "target_team": tm, "target_team_name": TEAM_NAMES.get(tm, "Unknown")}
    if val in SCRAMBLE_AUDIT_NAMES:
        return {"type": "SCRAMBLE_AUDIT", "scramble_audit": SCRAMBLE_AUDIT_NAMES[val]}
    if val == 7650:
        return {"type": "LOW_HP_DEPOSIT_THRESHOLD", "profile_id": 15}
    if 7600 <= val <= 7649:
        sc = val - 7600
        return {"type": "SCORE", "score": sc}
    return {"type": "UNKNOWN", "val": val}


def parse_kfdiag_lines(lines: List[str]) -> List[AIRoleDecisionRecord]:
    """Parse logcat text containing KFDIAG probes into structured records."""
    records: List[AIRoleDecisionRecord] = []
    current_rec: Optional[AIRoleDecisionRecord] = None

    line_pattern = re.compile(r"(\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+)?.*?\bKFDIAG\b.*?v=(-?\d+)")

    for line in lines:
        m = line_pattern.search(line)
        if not m:
            continue
        ts = m.group(1) or ""
        try:
            val = int(m.group(2))
        except ValueError:
            continue

        decoded = decode_probe_value(val)
        dtype = decoded["type"]

        if dtype == "FRAME_START":
            if current_rec is not None:
                # Prior record unclosed; emit it
                records.append(current_rec)
            current_rec = AIRoleDecisionRecord(timestamp=ts)
        elif dtype == "FRAME_END":
            if current_rec is not None:
                current_rec.complete = True
                records.append(current_rec)
                current_rec = None
        elif current_rec is not None:
            if dtype == "PROFILE_ID":
                current_rec.profile_id = decoded["profile_id"]
            elif dtype == "PURPOSE":
                current_rec.purpose = decoded["purpose"]
                current_rec.purpose_name = decoded["purpose_name"]
            elif dtype == "CLASS":
                current_rec.target_class = decoded["target_class"]
                current_rec.target_class_name = decoded["target_class_name"]
            elif dtype == "TEAM":
                current_rec.target_team = decoded["target_team"]
                current_rec.target_team_name = decoded["target_team_name"]
            elif dtype == "SCRAMBLE_AUDIT":
                current_rec.scramble_audit = decoded["scramble_audit"]
            elif dtype == "SCORE":
                current_rec.score = decoded["score"]
        else:
            # Standalone probe outside frame (disjoint self-describing recovery)
            rec = AIRoleDecisionRecord(timestamp=ts)
            if dtype == "PROFILE_ID":
                rec.profile_id = decoded["profile_id"]
                records.append(rec)
            elif dtype == "SCRAMBLE_AUDIT":
                rec.scramble_audit = decoded["scramble_audit"]
                records.append(rec)

    if current_rec is not None:
        records.append(current_rec)

    return records


def format_records(records: List[AIRoleDecisionRecord]) -> str:
    lines = []
    for r in records:
        status = "COMPLETE" if r.complete else "PARTIAL"
        lines.append(
            f"[{r.timestamp or 'NO_TS'}] [{status}] RoleID={r.profile_id} Purpose={r.purpose_name} "
            f"Target={r.target_class_name} Team={r.target_team_name} Score={r.score} "
            f"Audit={r.scramble_audit or 'None'}"
        )
    return "\n".join(lines)


def count_low_hp_deposit_events(lines: List[str]) -> int:
    """Separate candidate-evaluation events from final target decision records."""
    pattern = re.compile(r'\bKFDIAG\b.*?\bv=7650(?:\D|$)')
    return sum(bool(pattern.search(line)) for line in lines)


def main():
    parser = argparse.ArgumentParser(description="Decode AI role telemetry from logcat.")
    parser.add_argument("file", nargs="?", default=None, help="Logcat file (defaults to stdin)")
    args = parser.parse_args()

    if args.file:
        text = Path(args.file).read_text(encoding="utf-8", errors="replace")
    else:
        text = sys.stdin.read()

    records = parse_kfdiag_lines(text.splitlines())
    print(format_records(records))
    events = count_low_hp_deposit_events(text.splitlines())
    if events:
        print(f'LowHPDepositThresholdEvents={events} (profile=15; candidate evaluation)')


if __name__ == "__main__":
    main()
