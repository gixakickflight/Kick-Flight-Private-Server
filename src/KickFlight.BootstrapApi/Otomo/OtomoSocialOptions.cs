namespace KickFlight.BootstrapApi.Otomo;

// Configuration for the adapter's session (social) client: appsettings "Otomo:Social", or Otomo__Social__* environment
// variables.
//
// The adapter holds no follow edge, no friend and no follower read mark. Who follows whom, and every player's battle
// points, are otomo's; this section is only where that service lives and what it calls its ladders.
public sealed class OtomoSocialOptions
{
    public const string SectionName = "Otomo:Social";

    // Hard switch, and off by default. With it off the follow screens and the ranking screen fail closed in the
    // game's error contract, because an adapter with no social service cannot answer "who do I follow" and an empty
    // list would be a lie the client renders as fact.
    public bool Enabled { get; set; }

    // Base URL of session's player listener, e.g. http://otomo-gateway:8080. The path prefix
    // (/api/player/session/...) is kept, because the gateway forwards it intact. Trailing slashes are ignored.
    public string BaseUrl { get; set; } = "";

    // Bound on one call. At most two attempts are made (see OtomoSocial), so an unreachable session costs two
    // timeouts and then a clear answer.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    // How long to wait before the single retry of a transport failure or a 5xx. A 4xx is never retried.
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    // The client's battle rule type (1, 2, 3 - the three ladders the ranking screen has tabs for) -> the rule type session
    // keeps that ladder under: session.rank publishes rule_types crystal_scramble, flag_flight and rapid_ball. Ranked
    // rules 7, 8 and 4 are battleRuleType 1, 2 and 3 in masters_battle_rule.json (Crystal Scramble, Flag Flight, Rapid
    // Ball). The ranking screen and the battle-result award both use this one mapping. A session that does not keep a
    // name answers 400 unknown_rule_type, which is reported rather than guessed around.
    public Dictionary<int, string> RuleTypeNames { get; set; } = new()
    {
        [1] = "crystal_scramble",
        [2] = "flag_flight",
        [3] = "rapid_ball"
    };

    // The name session keeps a ladder under, or null when this client's type has no configured name - which is a
    // configuration error the caller reports, never a reason to ask for a different ladder.
    public string? RuleTypeName(int battleRuleType) =>
        RuleTypeNames.TryGetValue(battleRuleType, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
}
