using KickFlight.BootstrapApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

public sealed class ModeAwareCompositionTests
{
    private static readonly ScrambleBotCandidate[] Candidates =
    [
        new(1, 0, "carrier"), new(2, 1, "support"), new(3, 3, "anchor"), new(4, 2, "damage"),
        new(5, 1, "owlbert_support"), new(6, 2, "damage"), new(7, 1, "mobile_pressure"), new(8, 0, "carrier"),
        new(9, 0, "carrier"), new(10, 2, "hunter"), new(11, 3, "anchor"), new(12, 3, "anchor"),
        new(13, 2, "damage"), new(14, 1, "hunter")
    ];

    [Theory]
    [InlineData(1, "CrystalScramble")]
    [InlineData(5, "CrystalScramble")]
    [InlineData(6, "CrystalScramble")]
    [InlineData(7, "CrystalScramble")]
    [InlineData(2, "FlagFlight")]
    [InlineData(8, "FlagFlight")]
    [InlineData(3, "RapidBall")]
    [InlineData(4, "RapidBall")]
    public void Battle_rule_ids_select_the_verified_composition_policy(int ruleId, string expectedMode)
    {
        Assert.Equal(expectedMode, ScrambleBotCompositionSelector.ModeForBattleRuleId(ruleId)?.ToString());
    }

    [Fact]
    public void Mode_policy_changes_human_partition_while_preserving_parties_and_best_balance()
    {
        var partitionCandidates = Candidates.Where(candidate => candidate.KickerId is 1 or 3 or 7 or 10).ToArray();
        var parties = new[] { 1, 2, 3, 4, 5, 14 }
            .Select((kickerId, index) => new ScrambleHumanParty($"solo-{index}", [new ScrambleHuman(0, kickerId)]))
            .ToArray();
        const int seed = 37;
        var flag = ScrambleBotCompositionSelector.SelectHumanTeams(parties, partitionCandidates, seed, BattleCompositionMode.FlagFlight);
        var rapid = ScrambleBotCompositionSelector.SelectHumanTeams(parties, partitionCandidates, seed, BattleCompositionMode.RapidBall);

        Assert.Equal(parties.Length, flag.PartyTeams.Count);
        Assert.Equal(parties.Length, rapid.PartyTeams.Count);
        Assert.Equal(new[] { 3, 3 }, HumanCounts(parties, flag).Order().ToArray());
        Assert.Equal(new[] { 3, 3 }, HumanCounts(parties, rapid).Order().ToArray());
        Assert.All(parties, party => Assert.InRange(flag.PartyTeams[party.Key], 0, 1));
        Assert.All(parties, party => Assert.InRange(rapid.PartyTeams[party.Key], 0, 1));
        Assert.NotEqual(Partition(flag), Partition(rapid));
    }

    [Fact]
    public void Mode_policy_changes_bot_profiles_and_registered_deck_choices()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var decks = ScrambleBotDeckCatalog.Load(root, NullLogger.Instance);
        var humans = new[] { new ScrambleHuman(0, 1), new ScrambleHuman(1, 3) };
        var flag = ScrambleBotCompositionSelector.Select(humans, Candidates, decks, 1, 1, seed: 0, BattleCompositionMode.FlagFlight);
        var rapid = ScrambleBotCompositionSelector.Select(humans, Candidates, decks, 1, 1, seed: 0, BattleCompositionMode.RapidBall);

        Assert.Equal("0:12:tank_defender|1:8:carrier", ProfileSignature(flag));
        Assert.Equal("0:14:damage_support|1:9:carrier", ProfileSignature(rapid));
        Assert.NotEqual(DeckSignature(flag), DeckSignature(rapid));
        Assert.All(flag.Concat(rapid), bot => Assert.Contains(decks, deck =>
            deck.KickerId == bot.KickerId && deck.DeckId == bot.DeckId && deck.Profile == bot.Profile
            && deck.DiscIds.SequenceEqual(bot.DiscIds)));
    }

    [Fact]
    public void Flag_and_rapid_profiles_keep_support_specialists_and_rapid_targets_enemy_guardians()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var decks = ScrambleBotDeckCatalog.Load(root, NullLogger.Instance);
        var modes = new[] { BattleCompositionMode.FlagFlight, BattleCompositionMode.RapidBall };

        foreach (var mode in modes)
        {
            AssertProfile(decks, mode, kickerId: 2, "healer", []);
            AssertProfile(decks, mode, kickerId: 5, "trap_defender", []);
            AssertProfile(decks, mode, kickerId: 7, "speed_support", [new ScrambleHuman(0, 1)]);
            AssertProfile(decks, mode, kickerId: 14, "damage_support", [new ScrambleHuman(0, 1)]);
        }

        var guardianPressure = Assert.Single(ScrambleBotCompositionSelector.Select(
            [new ScrambleHuman(0, 1), new ScrambleHuman(1, 3)],
            [Candidates.Single(candidate => candidate.KickerId == 10)], decks,
            blueBotSlots: 1, redBotSlots: 0, seed: 37, BattleCompositionMode.RapidBall));
        Assert.Equal(10, guardianPressure.KickerId);
        Assert.Equal("hunter", guardianPressure.Profile);
    }

    [Fact]
    public void Unknown_battle_rules_keep_the_legacy_composition_path()
    {
        Assert.Null(ScrambleBotCompositionSelector.ModeForBattleRuleId(100));
        Assert.Null(ScrambleBotCompositionSelector.ModeForBattleRuleId(int.MaxValue));
    }

    private static int[] HumanCounts(IReadOnlyList<ScrambleHumanParty> parties, ScrambleHumanTeamSelection selection) =>
        Enumerable.Range(0, 2).Select(team => parties.Where(party => selection.PartyTeams[party.Key] == team)
            .Sum(party => party.Humans.Count)).ToArray();

    private static string Partition(ScrambleHumanTeamSelection selection) => string.Join('|',
        Enumerable.Range(0, 2).Select(team => string.Join(',', selection.PartyTeams
            .Where(pair => pair.Value == team).Select(pair => pair.Key).Order(StringComparer.Ordinal))));

    private static string ProfileSignature(IEnumerable<ScrambleBotAssignment> assignments) => string.Join('|', assignments
        .OrderBy(bot => bot.TeamType).ThenBy(bot => bot.KickerId).Select(bot => $"{bot.TeamType}:{bot.KickerId}:{bot.Profile}"));

    private static string DeckSignature(IEnumerable<ScrambleBotAssignment> assignments) => string.Join('|', assignments
        .OrderBy(bot => bot.TeamType).ThenBy(bot => bot.KickerId).Select(bot => $"{bot.TeamType}:{bot.KickerId}:{bot.DeckId}"));

    private static void AssertProfile(
        IReadOnlyList<ScrambleBotDeck> decks,
        BattleCompositionMode mode,
        int kickerId,
        string expectedProfile,
        IReadOnlyList<ScrambleHuman> humans)
    {
        var candidate = Candidates.Single(value => value.KickerId == kickerId);
        var selection = ScrambleBotCompositionSelector.Select(humans, [candidate], decks, 1, 0, seed: 37, mode);
        var assignment = Assert.Single(selection);
        Assert.Equal(expectedProfile, assignment.Profile);
        Assert.Contains(decks, deck => deck.KickerId == kickerId && deck.Profile == expectedProfile
            && deck.DeckId == assignment.DeckId && deck.DiscIds.SequenceEqual(assignment.DiscIds));
    }

}
