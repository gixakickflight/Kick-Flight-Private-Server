# Carry slowdown (client 2.11.0)

- n = GetItemCountForSpeedCorrection(player) (crystals; BallShootRuleController counts BallList). If
  BattleRuleParameter.itemDecelerationMaxCount != -1, n = Clamp(n, 0, max).
- per = BattleRuleRoleParameter.crystalCountSpeedCorrection (rule + role row), else client role default
  (Speed 1.0, Support 1.5, Attack 1.5, Tank 2.0). factor = Clamp01((100 - per * n) / 100); speed *= factor.
- Client RoleType enum is 0..3 (Speed, Support, Attack, Tank). The server used to send 1..4.
- Served from config/masters_battle_rule_parameter.json and config/masters_battle_rule_role_parameter.json
  (overridable from /balance, tab "Ralentizacion"). Shipped: every % = 0, max = -1 (no slowdown).
- Rule -> carried object (battleRuleType in masters_battle_rule.json): crystal 1, 5, 6, 7; flag 2, 8; ball 3, 4.
  Ranked rotation uses 7 (crystal), 8 (flag), 4 (ball).
