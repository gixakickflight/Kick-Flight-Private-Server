namespace KickFlight.BootstrapApi;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Motivation weights for bot AI behavior (attack, defense, score). Clamped to [0..100].
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BotAiMotivations(
    [property: JsonPropertyName("attack")] int Attack,
    [property: JsonPropertyName("defense")] int Defense,
    [property: JsonPropertyName("score")] int Score);

/// <summary>
/// Dash decision intervals (in seconds) per behavior mode. Must be positive finite numbers.
/// Note: Dash intervals are served in masters_kicker_ai_parameter.json, but exact client-side
/// reactivity across multiplayer network frames remains unverified.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BotAiDashIntervals(
    [property: JsonPropertyName("attack")] double Attack,
    [property: JsonPropertyName("defense")] double Defense,
    [property: JsonPropertyName("score")] double Score);

/// <summary>
/// Skill execution intervals (in seconds) per skill category. Must be positive finite numbers.
/// Note: Skill intervals are served in masters_kicker_ai_parameter.json, but exact client-side
/// reactivity across multiplayer network frames remains unverified.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BotAiSkillIntervals(
    [property: JsonPropertyName("attack")] double Attack,
    [property: JsonPropertyName("heal")] double Heal,
    [property: JsonPropertyName("buff")] double Buff,
    [property: JsonPropertyName("trap")] double Trap,
    [property: JsonPropertyName("warp")] double Warp);

/// <summary>
/// Profile configuration for a specific kicker and battle mode.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BotAiProfileConfig(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("parameterId")] int ParameterId,
    [property: JsonPropertyName("kickerId")] int KickerId,
    [property: JsonPropertyName("kickerCostumeId")] int KickerCostumeId,
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("motivations")] BotAiMotivations Motivations,
    [property: JsonPropertyName("scoreToDeposit")] int ScoreToDeposit,
    /// <summary>
    /// Validated and stored in server config ([0.0..1.0]), but NOT effective for native client tuning.
    /// The client native deposit cave (0x159D854..0x159D9F0) uses a hardcoded 35% HP threshold check
    /// and masters_kicker_ai_parameter.json has no schema column for this field.
    /// </summary>
    [property: JsonPropertyName("lowHpDepositThreshold")] double LowHpDepositThreshold,
    [property: JsonPropertyName("dashIntervals")] BotAiDashIntervals DashIntervals,
    [property: JsonPropertyName("skillIntervals")] BotAiSkillIntervals SkillIntervals,
    [property: JsonPropertyName("healDamageRate")] double HealDamageRate,
    [property: JsonPropertyName("canLockon")] bool CanLockon);

/// <summary>
/// Root document for bot AI profiles configuration.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BotAiConfigurationRoot(
    [property: JsonPropertyName("$schema")] string? Schema,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("profiles")] IReadOnlyList<BotAiProfileConfig> Profiles);
