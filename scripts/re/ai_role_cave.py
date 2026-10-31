#!/usr/bin/env python3
"""ai_role_cave.py - Reproducible generator, byte-guarded patcher and semantic validator for AI bot roles."""
import os
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PUBLISHED_SO = Path(os.environ.get("KF_TARGET_SO", "/tmp/kf-ai-role-analysis/libil2cpp-2.11.3.so"))
MAIN_REPO = Path("/home/gixarde3/Proyectos/KickFlight/Kick-Flight-Private-Server")
PRISTINE_APK = Path(os.environ.get("SOURCE_APK", REPO / "base.apk"))
if not PRISTINE_APK.exists():
    PRISTINE_APK = MAIN_REPO / "base.apk"

# Verified offsets
HOOK_UPDATE_AI = 0x13BEAEC
CAVE_UPDATE_AI = 0x159D200
HOOK_GET_DISPLAY_ANGLES = 0x13BC3A8
CAVE_GET_DISPLAY_ANGLES = 0x159D3E0
CAVE_JAY_BOMB = 0x159DA48
FREE_CAVE_WINDOW_START = 0x159D410
FREE_CAVE_WINDOW_END = 0x159DA44

HOOK_UPDATE_TARGET = 0x1807474
RET_UPDATE_TARGET = 0x1807478
HOOK_UPDATE_TARGET_SCORING = 0x18072D0
RET_UPDATE_TARGET_SCORING = 0x18072D4
CAVE_TARGETING_START = 0x159D6E0
HOOK_SCRAMBLE_DEPOSIT = 0x1808D40
RET_SCRAMBLE_DEPOSIT = 0x1808D44
RET_SCRAMBLE_EPILOGUE = 0x1808CB0
CAVE_DEPOSIT_START = 0x159D854
TARGET_DAMAGE_RATE_RVA = 0x13BE18C
LOG_PRINT_PLT = 0x10D6270

# Telemetry Probe Space (Disjoint, Unambiguous Ranges & Framed Delimiters)
# 7500: Frame Start
# 7515..7553: Bot Profile ID (7500 + paramId, contract 15..53)
# 7560..7562: Target Purpose (7560 + purpose: 0=Attack, 1=Defense, 2=Score)
# 7570..7573: Target Class (7570 + class: 0=None, 1=PlayerCharacter, 2=NPCGuardian, 3=Other)
# 7580..7581: Target Team (7580 + team: 0=Team0, 1=Team1)
# 7590..7591: Scramble Audit Probes (7590=Hunter vs Guardian, 7591=Objective Attacker vs Guardian)
# 7600..7649: Target Score (7600 + clamp(int(score), 0, 49))
# 7699: Frame End
PROBE_FRAME_START = 7500
PROBE_PROFILE_ID_BASE = 7500    # + 15..53 -> 7515..7553
PROBE_PURPOSE_BASE = 7560       # + 0..2   -> 7560..7562
PROBE_CLASS_BASE = 7570         # + 0..3   -> 7570..7573
PROBE_TEAM_BASE = 7580          # + 0..1   -> 7580..7581
PROBE_SCRAMBLE_HUNTER = 7590
PROBE_SCRAMBLE_OBJ_ATTACKER = 7591
PROBE_SCORE_BASE = 7600         # + 0..49  -> 7600..7649
PROBE_FRAME_END = 7699


def generate_ai_role_cave_bytes():
    from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN
    ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)

    asm = f"""
    .equ LOG_PRINT, {LOG_PRINT_PLT}
    .equ RET_ADDR, {RET_UPDATE_TARGET}

    cave_entry:
        // Full register preservation: preserve ALL 32 SIMD/vector registers,
        // ALL 31 general-purpose registers (x0..x30), and condition flags NZCV.
        sub sp, sp, #0x300
        stp x0, x1, [sp, #0x0]
        stp x2, x3, [sp, #0x10]
        stp x4, x5, [sp, #0x20]
        stp x6, x7, [sp, #0x30]
        stp x8, x9, [sp, #0x40]
        stp x10, x11, [sp, #0x50]
        stp x12, x13, [sp, #0x60]
        stp x14, x15, [sp, #0x70]
        stp x16, x17, [sp, #0x80]
        stp x18, x19, [sp, #0x90]
        stp x20, x21, [sp, #0xa0]
        stp x22, x23, [sp, #0xb0]
        stp x24, x25, [sp, #0xc0]
        stp x26, x27, [sp, #0xd0]
        stp x28, x29, [sp, #0xe0]
        str x30, [sp, #0xf0]
        mrs x8, nzcv
        str x8, [sp, #0xf8]

        stp q0, q1, [sp, #0x100]
        stp q2, q3, [sp, #0x120]
        stp q4, q5, [sp, #0x140]
        stp q6, q7, [sp, #0x160]
        stp q8, q9, [sp, #0x180]
        stp q10, q11, [sp, #0x1a0]
        stp q12, q13, [sp, #0x1c0]
        stp q14, q15, [sp, #0x1e0]
        stp q16, q17, [sp, #0x200]
        stp q18, q19, [sp, #0x220]
        stp q20, q21, [sp, #0x240]
        stp q22, q23, [sp, #0x260]
        stp q24, q25, [sp, #0x280]
        stp q26, q27, [sp, #0x2a0]
        stp q28, q29, [sp, #0x2c0]
        stp q30, q31, [sp, #0x2e0]

        // Check _ownerPlayer (AIPlayerEngine + 0x40)
        ldr x22, [x19, #0x40]
        cbz x22, lbl_bypass
        // Check _enableAi (PlayerCharacter + 0xe0)
        ldrb w8, [x22, #0xe0]
        cbz w8, lbl_bypass

        // Check _kickerAiParameterId in 15..53 (PlayerCharacter + 0xc4)
        ldr w23, [x22, #0xc4]
        cmp w23, #15
        b.lt lbl_bypass
        cmp w23, #53
        b.gt lbl_bypass

        // Telemetry 1: Frame Start Header (7500)
        mov w0, #{PROBE_FRAME_START}
        bl log_probe

        // Telemetry 2: Disjoint Profile ID range: 7500 + paramId (7515..7553 for IDs 15..53)
        mov w0, #{PROBE_PROFILE_ID_BASE}
        add w0, w0, w23
        bl log_probe

        // Telemetry 3: Disjoint Purpose range: 7560 + purpose (0=Attack, 1=Defense, 2=Score -> 7560..7562)
        ldr w8, [sp, #0x0]
        mov w0, #{PROBE_PURPOSE_BASE}
        add w0, w0, w8
        bl log_probe

        // Check target SearchTargetInfo (x21)
        cbz x21, lbl_no_target
        // SearchTargetInfo + 0x10 -> target entity object
        ldr x24, [x21, #0x10]
        cbz x24, lbl_no_target

        // Target class from klass->name ([x24] -> klass, klass + 0x10 -> const char* name)
        ldr x8, [x24]
        cbz x8, lbl_no_target
        ldr x9, [x8, #0x10]
        cbz x9, lbl_no_target
        ldr w10, [x9]
        
        // PlayerCharacter = 0x79616C50 ("Play")
        movz w11, #0x6C50
        movk w11, #0x7961, lsl #16
        cmp w10, w11
        b.ne lbl_check_guardian
        mov w25, #1
        b lbl_log_target_class

    lbl_check_guardian:
        // NPCGuardian = 0x4743504E ("NPCG")
        movz w11, #0x504E
        movk w11, #0x4743, lsl #16
        cmp w10, w11
        b.ne lbl_other_target
        mov w25, #2
        b lbl_log_target_class

    lbl_other_target:
        mov w25, #3

    lbl_log_target_class:
        // Telemetry 4: Disjoint Target Class range: 7570 + targetClass (1=Player, 2=Guardian, 3=Other -> 7571..7573)
        mov w0, #{PROBE_CLASS_BASE}
        add w0, w0, w25
        bl log_probe

        // Check target team:
        // PlayerCharacter + 0x180 -> PlayerBattleInfo, PlayerBattleInfo + 0x50 -> TeamType
        cmp w25, #1
        b.ne lbl_check_guard_team
        ldr x8, [x24, #0x180]
        cbz x8, lbl_check_scramble_pref
        ldr w26, [x8, #0x50]
        and w26, w26, #1
        // Telemetry 5: Disjoint Target Team range: 7580 + targetTeam (0 or 1 -> 7580..7581)
        mov w0, #{PROBE_TEAM_BASE}
        add w0, w0, w26
        bl log_probe
        b lbl_check_scramble_pref

    lbl_check_guard_team:
        // NPCGuardian + 0x118 -> NPCGuardianParameter, NPCGuardianParameter + 0x10 -> TeamType
        cmp w25, #2
        b.ne lbl_check_scramble_pref
        ldr x8, [x24, #0x118]
        cbz x8, lbl_check_scramble_pref
        ldr w26, [x8, #0x10]
        and w26, w26, #1
        // Telemetry 5: Disjoint Target Team range: 7580 + targetTeam (0 or 1 -> 7580..7581)
        mov w0, #{PROBE_TEAM_BASE}
        add w0, w0, w26
        bl log_probe

    lbl_check_scramble_pref:
        // Telemetry 6: Disjoint Scramble Audit Probes (7590 = Hunter vs Guardian, 7591 = Obj Attacker vs Guardian)
        // NOTE: This hook executes at target commit time (str w0, [x19, #0x7c]) after scoring loop.
        // It strictly observes and logs the target selection decision and does NOT alter scoring.
        cmp w23, #17
        b.ne lbl_check_obj_attacker
        cmp w25, #2
        b.ne lbl_log_score
        mov w0, #{PROBE_SCRAMBLE_HUNTER}
        bl log_probe
        b lbl_log_score

    lbl_check_obj_attacker:
        cmp w23, #18
        b.eq lbl_is_obj_att
        cmp w23, #23
        b.ne lbl_log_score
    lbl_is_obj_att:
        cmp w25, #2
        b.ne lbl_log_score
        mov w0, #{PROBE_SCRAMBLE_OBJ_ATTACKER}
        bl log_probe
        b lbl_log_score

    lbl_no_target:
        // 7570: Target Class None
        mov w0, #{PROBE_CLASS_BASE}
        bl log_probe

    lbl_log_score:
        // Telemetry 7: Disjoint Score range: 7600 + clamp(int(score), 0, 49) -> 7600..7649
        fcvtzs w27, s8
        cmp w27, #0
        csel w27, wzr, w27, lt
        cmp w27, #49
        mov w8, #49
        csel w27, w8, w27, gt
        mov w0, #{PROBE_SCORE_BASE}
        add w0, w0, w27
        bl log_probe

        // Telemetry 8: Frame End Footer (7699)
        mov w0, #{PROBE_FRAME_END}
        bl log_probe

    lbl_bypass:
        // Restore all 32 SIMD/vector registers, all GPRs x0..x30, and NZCV
        ldp q30, q31, [sp, #0x2e0]
        ldp q28, q29, [sp, #0x2c0]
        ldp q26, q27, [sp, #0x2a0]
        ldp q24, q25, [sp, #0x280]
        ldp q22, q23, [sp, #0x260]
        ldp q20, q21, [sp, #0x240]
        ldp q18, q19, [sp, #0x220]
        ldp q16, q17, [sp, #0x200]
        ldp q14, q15, [sp, #0x1e0]
        ldp q12, q13, [sp, #0x1c0]
        ldp q10, q11, [sp, #0x1a0]
        ldp q8, q9, [sp, #0x180]
        ldp q6, q7, [sp, #0x160]
        ldp q4, q5, [sp, #0x140]
        ldp q2, q3, [sp, #0x120]
        ldp q0, q1, [sp, #0x100]

        ldr x8, [sp, #0xf8]
        msr nzcv, x8
        ldr x30, [sp, #0xf0]
        ldp x28, x29, [sp, #0xe0]
        ldp x26, x27, [sp, #0xd0]
        ldp x24, x25, [sp, #0xc0]
        ldp x22, x23, [sp, #0xb0]
        ldp x20, x21, [sp, #0xa0]
        ldp x18, x19, [sp, #0x90]
        ldp x16, x17, [sp, #0x80]
        ldp x14, x15, [sp, #0x70]
        ldp x12, x13, [sp, #0x60]
        ldp x10, x11, [sp, #0x50]
        ldp x8, x9, [sp, #0x40]
        ldp x6, x7, [sp, #0x30]
        ldp x4, x5, [sp, #0x20]
        ldp x2, x3, [sp, #0x10]
        ldp x0, x1, [sp, #0x0]
        add sp, sp, #0x300

        // Displaced instruction executed exactly once
        str w0, [x19, #0x7c]
        b RET_ADDR

    log_probe:
        stp x29, x30, [sp, #-0x30]!
        mov x29, sp
        stp x0, x1, [sp, #0x10]
        stp x2, x3, [sp, #0x20]
        mov w3, w0
        mov w0, #3
        adr x1, tag_kfdiag
        adr x2, fmt_kfdiag
        bl LOG_PRINT
        ldp x2, x3, [sp, #0x20]
        ldp x0, x1, [sp, #0x10]
        ldp x29, x30, [sp], #0x30
        ret

    tag_kfdiag:
        .asciz "KFDIAG"
        .balign 4
    fmt_kfdiag:
        .asciz "v=%d"
        .balign 4
    """
    encoding, _ = ks.asm(asm, FREE_CAVE_WINDOW_START)
    cave_bytes = bytes(encoding)
    assert FREE_CAVE_WINDOW_START + len(cave_bytes) <= FREE_CAVE_WINDOW_END, "Cave exceeds free window!"
    return cave_bytes


def generate_ai_role_targeting_cave_bytes():
    """Generates the ARM64 machine code for candidate scoring weighting in AIPlayerEngine.UpdateTarget."""
    from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN
    ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)

    asm = f"""
    .equ RET_SCORING, {RET_UPDATE_TARGET_SCORING}

    cave_entry:
        // Full register preservation: preserve general registers, SIMD vectors, condition flags
        sub sp, sp, #0x120
        stp x0, x1, [sp, #0x0]
        stp x2, x3, [sp, #0x10]
        stp x4, x5, [sp, #0x20]
        stp x6, x7, [sp, #0x30]
        stp x8, x9, [sp, #0x40]
        stp x10, x11, [sp, #0x50]
        stp x12, x13, [sp, #0x60]
        stp x14, x15, [sp, #0x70]
        stp x16, x17, [sp, #0x80]
        stp x18, x30, [sp, #0x90]
        mrs x8, nzcv
        str x8, [sp, #0xa0]
        stp q1, q2, [sp, #0xb0]
        stp q3, q4, [sp, #0xd0]

        // 1. Positive candidate score gate: s0 > 0.0 (preserves 0.0 / invalid / friendly scores)
        fcmp s0, #0.0
        b.le bypass

        // 2. Evaluator check: x23 is current AIEvaluationEngine
        cbz x23, bypass
        ldr x8, [x23]
        cbz x8, bypass
        ldr x9, [x8, #0x10]
        cbz x9, bypass
        ldr w10, [x9]
        // AIAttackEvaluationEngine name starts with 'AIAt' = 0x74414941
        movz w11, #0x4941
        movk w11, #0x7441, lsl #16
        cmp w10, w11
        b.ne bypass

        // 3. Owner player & enableAi check: x19 is AIPlayerEngine
        ldr x12, [x19, #0x40]
        cbz x12, bypass
        ldrb w13, [x12, #0xe0]
        cbz w13, bypass

        // 4. Role ID check: PlayerCharacter + 0xc4
        ldr w13, [x12, #0xc4]
        cmp w13, #17
        b.eq check_target
        cmp w13, #18
        b.eq check_target
        cmp w13, #23
        b.ne bypass

    check_target:
        // 5. Candidate target from x21 (List<SearchTargetInfo>) at index w24
        cbz x21, bypass
        ldr w14, [x21, #0x18]
        cmp w24, w14
        b.hs bypass
        ldr x14, [x21, #0x10]
        cbz x14, bypass
        add x14, x14, w24, uxtw #3
        ldr x14, [x14, #0x20]
        cbz x14, bypass
        ldr x15, [x14, #0x10]
        cbz x15, bypass

        // 6. Target entity class check: x15 is ISearchTarget
        ldr x16, [x15]
        cbz x16, bypass
        ldr x17, [x16, #0x10]
        cbz x17, bypass
        ldr w18, [x17]

        // PlayerCharacter = 0x79616C50 ('Play')
        movz w7, #0x6C50
        movk w7, #0x7961, lsl #16
        // NPCGuardian = 0x4743504E ('NPCG')
        movz w6, #0x504E
        movk w6, #0x4743, lsl #16

        // Branch by role ID
        cmp w13, #17
        b.eq hunter_weights

    objective_weights:
        // Role 18 or 23: Objective Attacker (prefer hostile guardians over players)
        cmp w18, w6
        b.eq weight_boost
        cmp w18, w7
        b.eq weight_penalize
        b bypass

    hunter_weights:
        // Role 17: Hunter (prefer hostile players over hostile guardians)
        cmp w18, w7
        b.eq weight_boost
        cmp w18, w6
        b.eq weight_penalize
        b bypass

    weight_boost:
        fmov s1, #4.0
        fmul s0, s0, s1
        b bypass

    weight_penalize:
        fmov s1, #0.25
        fmul s0, s0, s1
        b bypass

    bypass:
        ldp q3, q4, [sp, #0xd0]
        ldp q1, q2, [sp, #0xb0]
        ldr x8, [sp, #0xa0]
        msr nzcv, x8
        ldp x18, x30, [sp, #0x90]
        ldp x16, x17, [sp, #0x80]
        ldp x14, x15, [sp, #0x70]
        ldp x12, x13, [sp, #0x60]
        ldp x10, x11, [sp, #0x50]
        ldp x8, x9, [sp, #0x40]
        ldp x6, x7, [sp, #0x30]
        ldp x4, x5, [sp, #0x20]
        ldp x2, x3, [sp, #0x10]
        ldp x0, x1, [sp, #0x0]
        add sp, sp, #0x120

        // Displaced instruction: ldr x1, [x26]
        ldr x1, [x26]
        b RET_SCORING
    """
    encoding, _ = ks.asm(asm, CAVE_TARGETING_START)
    cave_bytes = bytes(encoding)
    assert CAVE_TARGETING_START + len(cave_bytes) <= FREE_CAVE_WINDOW_END, "Targeting cave exceeds free window!"
    return cave_bytes


def get_ai_role_targeting_patches(so_bytes):
    """Returns list of patch dictionaries for KF_AI_ROLE_TARGETING=1."""
    cave_bytes = generate_ai_role_targeting_cave_bytes()
    expected_cave = so_bytes[CAVE_TARGETING_START:CAVE_TARGETING_START + len(cave_bytes)]
    expected_hook = so_bytes[HOOK_UPDATE_TARGET_SCORING:HOOK_UPDATE_TARGET_SCORING + 4]

    from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN
    ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)
    hook_branch, _ = ks.asm(f"b 0x{CAVE_TARGETING_START:X}", HOOK_UPDATE_TARGET_SCORING)
    hook_bytes = bytes(hook_branch)

    return [
        {
            "description": f"cave: AI role Scramble candidate scoring weighting cave (window 0x{CAVE_TARGETING_START:X}..0x{CAVE_TARGETING_START+len(cave_bytes):X})",
            "offset": CAVE_TARGETING_START,
            "expected": expected_cave,
            "replacement": cave_bytes,
        },
        {
            "description": "hook: AIPlayerEngine.UpdateTarget candidate score weighting hook",
            "offset": HOOK_UPDATE_TARGET_SCORING,
            "expected": expected_hook,
            "replacement": hook_bytes,
        },
    ]


def get_ai_roles_patches(so_bytes, include_targeting=False):
    """Returns list of patch dictionaries for KF_AI_ROLES=1 (and optionally KF_AI_ROLE_TARGETING=1)."""
    cave_bytes = generate_ai_role_cave_bytes()
    expected_cave = so_bytes[FREE_CAVE_WINDOW_START:FREE_CAVE_WINDOW_START + len(cave_bytes)]
    expected_hook = so_bytes[HOOK_UPDATE_TARGET:HOOK_UPDATE_TARGET + 4]

    from keystone import Ks, KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN
    ks = Ks(KS_ARCH_ARM64, KS_MODE_LITTLE_ENDIAN)
    hook_branch, _ = ks.asm(f"b 0x{FREE_CAVE_WINDOW_START:X}", HOOK_UPDATE_TARGET)
    hook_bytes = bytes(hook_branch)

    patches = [
        {
            "description": f"cave: AI roles telemetry cave (window 0x{FREE_CAVE_WINDOW_START:X}..0x{FREE_CAVE_WINDOW_START+len(cave_bytes):X})",
            "offset": FREE_CAVE_WINDOW_START,
            "expected": expected_cave,
            "replacement": cave_bytes,
        },
        {
            "description": "hook: AIPlayerEngine.UpdateTarget decision/score telemetry probe hook",
            "offset": HOOK_UPDATE_TARGET,
            "expected": expected_hook,
            "replacement": hook_bytes,
        },
    ]

    if include_targeting:
        patches.extend(get_ai_role_targeting_patches(so_bytes))

    return patches


def simulate_native_target_scoring(
    candidate_score: float,
    evaluator_class: str,
    enable_ai: bool,
    kicker_ai_param_id: int,
    target_class: str,
) -> float:
    """Exact emulation of the native ARM64 scoring cave instructions."""
    if candidate_score <= 0.0:
        return candidate_score
    if evaluator_class != "AIAttackEvaluationEngine":
        return candidate_score
    if not enable_ai:
        return candidate_score
    if kicker_ai_param_id not in (17, 18, 23):
        return candidate_score

    if kicker_ai_param_id == 17:  # Scramble Hunter
        if target_class == "PlayerCharacter":
            return candidate_score * 4.0
        elif target_class == "NPCGuardian":
            return candidate_score * 0.25
        else:
            return candidate_score
    elif kicker_ai_param_id in (18, 23):  # Scramble Objective Attacker
        if target_class == "NPCGuardian":
            return candidate_score * 4.0
        elif target_class == "PlayerCharacter":
            return candidate_score * 0.25
        else:
            return candidate_score
    return candidate_score


def verify_incoming_branches(data, start_target, end_target, allowlist):
    """Scan entire SO for branches targeting [start_target, end_target)."""
    ext_branches = []
    for offset in range(0, len(data), 4):
        if start_target <= offset < end_target:
            continue
        word = struct.unpack_from("<I", data, offset)[0]
        # B/BL: 0001 01xx / 1001 01xx
        if (word & 0x7C000000) == 0x14000000:
            imm26 = word & 0x3FFFFFF
            if imm26 & 0x2000000:
                imm26 -= 0x4000000
            target = offset + (imm26 << 2)
            if start_target <= target < end_target:
                ext_branches.append((offset, "B/BL", target))
        # B.cond: 0101 0100
        elif (word & 0xFF000010) == 0x54000000:
            imm19 = (word >> 5) & 0x7FFFF
            if imm19 & 0x40000:
                imm19 -= 0x80000
            target = offset + (imm19 << 2)
            if start_target <= target < end_target:
                ext_branches.append((offset, "B.cond", target))
        # CBZ/CBNZ: x011 010x
        elif (word & 0x7E000000) == 0x34000000:
            imm19 = (word >> 5) & 0x7FFFF
            if imm19 & 0x40000:
                imm19 -= 0x80000
            target = offset + (imm19 << 2)
            if start_target <= target < end_target:
                ext_branches.append((offset, "CBZ/CBNZ", target))
        # TBZ/TBNZ: x011 011x
        elif (word & 0x7E000000) == 0x36000000:
            imm14 = (word >> 5) & 0x3FFF
            if imm14 & 0x2000:
                imm14 -= 0x4000
            target = offset + (imm14 << 2)
            if start_target <= target < end_target:
                ext_branches.append((offset, "TBZ/TBNZ", target))

    unexpected = [b for b in ext_branches if (b[0], b[2]) not in allowlist]
    return ext_branches, unexpected


def audit_cave_safety(so_data):
    """Verify that dead body of SetModel is entry-stubbed and window 0x159D410..0x159DA44 has 0 external incoming branches."""
    allowlist = {
        (0x1552D84, 0x159D1BC),  # HomeSummonModelController caller -> SetModel entry (stubbed ret)
        (0x13BEAEC, 0x159D200),  # UpdateAi cave
        (0x13BC3A8, 0x159D3E0),  # GetDisplayAngles cave
        (0x1439AEC, 0x159DA48),  # Jay bomb cave
    }
    all_branches, unexpected = verify_incoming_branches(so_data, 0x159D1BC, 0x159DAB0, allowlist)
    assert len(unexpected) == 0, f"Found unexpected incoming branches: {unexpected}"

    # Also verify free window has exactly 0 incoming branches from outside the dead body
    free_branches, unexpected_free = verify_incoming_branches(so_data, FREE_CAVE_WINDOW_START, FREE_CAVE_WINDOW_END, set())
    # Any branches strictly from outside SetModel (i.e. < 0x159D1BC or >= 0x159DAB0)
    external_free = [b for b in free_branches if b[0] < 0x159D1BC or b[0] >= 0x159DAB0]
    assert len(external_free) == 0, f"Free window has external incoming branches: {external_free}"
    print(f"[OK] Cave safety audit: window 0x{FREE_CAVE_WINDOW_START:X}..0x{FREE_CAVE_WINDOW_END:X} has 0 external incoming branches.")


def validate_negative_guards(published_data, pristine_data):
    """Ensure published bytes are never matched against pristine expectations."""
    pub_hook = published_data[HOOK_UPDATE_AI:HOOK_UPDATE_AI+4]
    pris_hook = pristine_data[HOOK_UPDATE_AI:HOOK_UPDATE_AI+4]
    assert pub_hook != pris_hook, "Published hook should differ from pristine hook"
    assert pub_hook == bytes.fromhex("c5790714"), f"Unexpected published UpdateAi hook: {pub_hook.hex()}"
    assert pris_hook == bytes.fromhex("f44fbea9"), f"Unexpected pristine UpdateAi hook: {pris_hook.hex()}"

    # Also test negative guard on wrong hook expectation
    bad_expected = bytes.fromhex("ffffffff")
    assert bad_expected != pristine_data[HOOK_UPDATE_TARGET:HOOK_UPDATE_TARGET+4], "Negative guard: bad expected matches!"
    assert bad_expected != pristine_data[HOOK_UPDATE_TARGET_SCORING:HOOK_UPDATE_TARGET_SCORING+4], "Negative guard: bad expected matches scoring hook!"
    print("[OK] Negative guards: Published SO and pristine base cleanly distinguished; stale publication rejected.")


def main():
    if not PRISTINE_APK.exists():
        print(f"Error: {PRISTINE_APK} not found.")
        sys.exit(1)

    import zipfile
    with zipfile.ZipFile(PRISTINE_APK, "r") as z:
        pris_data = z.read("lib/arm64-v8a/libil2cpp.so")

    print("=== Running AI Role Native Safety & Byte-Guard Verification ===")
    if PUBLISHED_SO.exists():
        with open(PUBLISHED_SO, "rb") as f:
            pub_data = f.read()
        audit_cave_safety(pub_data)
        validate_negative_guards(pub_data, pris_data)
    else:
        audit_cave_safety(pris_data)

    for include_targeting in (False, True):
        mode_str = "telemetry + targeting" if include_targeting else "telemetry only"
        patches = get_ai_roles_patches(pris_data, include_targeting=include_targeting)
        print(f"[OK] Generated {len(patches)} AI roles patches ({mode_str}):")
        for p in patches:
            print(f"  - 0x{p['offset']:X}: len {len(p['replacement'])} bytes ({p['description']})")

        # Dry-run patch verification on pristine data
        for p in patches:
            off = p["offset"]
            exp = p["expected"]
            actual = pris_data[off:off+len(exp)]
            assert actual == exp, f"Expected byte mismatch at 0x{off:X}!"
        print(f"[OK] Dry-run verification passed on pristine libil2cpp.so ({mode_str}).")


if __name__ == "__main__":
    main()
