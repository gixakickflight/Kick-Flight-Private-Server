using System.Diagnostics;
using System.Text.Json;
using KickFlight.BootstrapApi;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KickFlight.BootstrapApi.Tests;

public sealed class ScrambleBotCompositionTests
{
    private readonly ITestOutputHelper _output;

    public ScrambleBotCompositionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Scramble_composition_preserves_humans_and_fills_missing_roles()
    {
        var humans = new[] { new ScrambleHuman(0, 1), new ScrambleHuman(1, 3) };
        var result = Select(humans, blueSlots: 3, redSlots: 3, seed: 471);

        Assert.Equal(6, result.Count);
        Assert.Equal(3, result.Count(bot => bot.TeamType == 0));
        Assert.Equal(3, result.Count(bot => bot.TeamType == 1));
        Assert.DoesNotContain(result, bot => humans.Any(h => h.TeamType == bot.TeamType && h.KickerId == bot.KickerId));
        AssertRoleCoverage(humans.Single(h => h.TeamType == 0), result.Where(b => b.TeamType == 0));
        AssertRoleCoverage(humans.Single(h => h.TeamType == 1), result.Where(b => b.TeamType == 1));
    }

    [Fact]
    public void Human_team_partition_balances_eight_solos_and_is_independent_of_entry_order()
    {
        var parties = HumanParties(("a", 1), ("b", 2), ("c", 3), ("d", 4),
            ("e", 5), ("f", 6), ("g", 7), ("h", 8));

        var result = ScrambleBotCompositionSelector.SelectHumanTeams(parties, [], seed: 37);
        var reordered = ScrambleBotCompositionSelector.SelectHumanTeams(parties.Reverse().ToArray(), [], seed: 37);

        Assert.Equal(8, result.PartyTeams.Count);
        Assert.Equal(4, result.PartyTeams.Count(pair => pair.Value == 0));
        Assert.Equal(4, result.PartyTeams.Count(pair => pair.Value == 1));
        Assert.Equal(PartyPartition(result), PartyPartition(reordered));

        // With one of each role and a unique Ruriha + Coco interaction, the high-scoring pair stays together.
        Assert.Equal("a,b,c,d|e,f,g,h", PartyPartition(result));
        Assert.Equal(result.PartyTeams["b"], result.PartyTeams["c"]);
    }

    [Fact]
    public void Human_team_partition_keeps_whole_parties_balances_when_possible_and_is_seed_repeatable()
    {
        var parties = new[]
        {
            new ScrambleHumanParty("trio", [new ScrambleHuman(0, 1), new ScrambleHuman(0, 2), new ScrambleHuman(0, 3)]),
            new ScrambleHumanParty("solo-a", [new ScrambleHuman(0, 4)]),
            new ScrambleHumanParty("solo-b", [new ScrambleHuman(0, 5)])
        };

        var first = ScrambleBotCompositionSelector.SelectHumanTeams(parties, Candidates(), seed: 44);
        var sameSeed = ScrambleBotCompositionSelector.SelectHumanTeams(parties, Candidates(), seed: 44);

        Assert.Equal(first.PartyTeams, sameSeed.PartyTeams);
        Assert.Equal(3, first.PartyTeams.Count);
        var counts = HumanCountsByTeam(parties, first.PartyTeams);
        Assert.Equal(new[] { 2, 3 }, counts.Order().ToArray());
        Assert.Equal(first.PartyTeams["solo-a"], first.PartyTeams["solo-b"]);
        Assert.NotEqual(first.PartyTeams["trio"], first.PartyTeams["solo-a"]);
    }

    [Fact]
    public void Human_team_partition_preserves_three_plus_one_party_and_fills_mixed_roster_capacities()
    {
        var parties = new[]
        {
            new ScrambleHumanParty("trio", [new ScrambleHuman(0, 1), new ScrambleHuman(0, 2), new ScrambleHuman(0, 3)]),
            new ScrambleHumanParty("solo", [new ScrambleHuman(0, 4)])
        };
        var candidates = Candidates();
        var selection = ScrambleBotCompositionSelector.SelectHumanTeams(parties, candidates, seed: 15);
        var humanCounts = HumanCountsByTeam(parties, selection.PartyTeams);

        Assert.Equal(new[] { 1, 3 }, humanCounts.Order().ToArray());
        var humans = parties.SelectMany(party => party.Humans.Select(human =>
            new ScrambleHuman(selection.PartyTeams[party.Key], human.KickerId))).ToArray();
        var bots = ScrambleBotCompositionSelector.Select(humans, candidates, TestDecks(),
            4 - humanCounts[0], 4 - humanCounts[1], seed: 15);
        Assert.Equal(4, bots.Count);
        Assert.Equal(4, humans.Count(human => human.TeamType == 0) + bots.Count(bot => bot.TeamType == 0));
        Assert.Equal(4, humans.Count(human => human.TeamType == 1) + bots.Count(bot => bot.TeamType == 1));
    }

    [Fact]
    public void Four_solo_humans_split_evenly_and_fill_a_mixed_four_v_four_roster()
    {
        var parties = HumanParties(("a", 999), ("b", 998), ("c", 997), ("d", 996));
        var candidates = Candidates();
        var selection = ScrambleBotCompositionSelector.SelectHumanTeams(parties, candidates, seed: 29);
        var humanCounts = HumanCountsByTeam(parties, selection.PartyTeams);

        Assert.Equal(new[] { 2, 2 }, humanCounts.Order().ToArray());
        var humans = parties.SelectMany(party => party.Humans.Select(human =>
            new ScrambleHuman(selection.PartyTeams[party.Key], human.KickerId))).ToArray();
        var bots = ScrambleBotCompositionSelector.Select(humans, candidates, TestDecks(), 2, 2, seed: 29);
        Assert.Equal(4, bots.Count);
        Assert.Equal(4, humans.Count(human => human.TeamType == 0) + bots.Count(bot => bot.TeamType == 0));
        Assert.Equal(4, humans.Count(human => human.TeamType == 1) + bots.Count(bot => bot.TeamType == 1));
    }

    [Fact]
    public void Human_team_partition_accepts_unknown_kickers_and_fewer_parties_within_capacity()
    {
        var parties = new[]
        {
            new ScrambleHumanParty("unknown-party", [new ScrambleHuman(0, 999), new ScrambleHuman(0, 998)]),
            new ScrambleHumanParty("unknown-solo", [new ScrambleHuman(0, 997)])
        };

        var result = ScrambleBotCompositionSelector.SelectHumanTeams(parties, Candidates(), seed: 8);

        Assert.Equal(2, result.PartyTeams.Count);
        Assert.Contains("unknown-party", result.PartyTeams.Keys);
        Assert.Contains("unknown-solo", result.PartyTeams.Keys);
        Assert.All(result.PartyTeams.Values, team => Assert.InRange(team, 0, 1));
        Assert.InRange(result.PartyTeams.Count(pair => pair.Value == 0), 0, 4);
        Assert.InRange(result.PartyTeams.Count(pair => pair.Value == 1), 0, 4);
    }

    [Fact]
    public void Human_partition_search_has_a_bounded_full_room_smoke()
    {
        var parties = HumanParties(("a", 1), ("b", 2), ("c", 3), ("d", 4),
            ("e", 5), ("f", 6), ("g", 7), ("h", 8));
        var timer = Stopwatch.StartNew();

        var result = ScrambleBotCompositionSelector.SelectHumanTeams(parties, Candidates(), seed: 2718);

        timer.Stop();
        Assert.Equal(8, result.PartyTeams.Count);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10),
            $"Eight-party human partition search took {timer.Elapsed.TotalMilliseconds:F1} ms.");
        _output.WriteLine($"Eight-party human partition search: {timer.Elapsed.TotalMilliseconds:F1} ms; 14 candidate bots.");
    }

    [Fact]
    public void Same_team_avoids_duplicate_kickers_when_enough_candidates_exist_but_opposing_sides_may_mirror()
    {
        var candidate = new ScrambleBotCandidate(2, 1, "support");
        var deck = new ScrambleBotDeck(2, 92002, "healer", 1, 1,
            new Dictionary<string, double> { ["HEAL"] = 0.7, ["WARP"] = 0.8 }, [920021, 920022, 920023, 920024]);
        var result = ScrambleBotCompositionSelector.Select(
            [new ScrambleHuman(0, 2)], [candidate], [deck], blueBotSlots: 1, redBotSlots: 1, seed: 5);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, Assert.Single(result, bot => bot.TeamType == 0).KickerId);
        Assert.Equal(2, Assert.Single(result, bot => bot.TeamType == 1).KickerId);
    }

    [Fact]
    public void Seed_repeats_a_roster_and_changes_picks_across_seeds()
    {
        var humans = new[] { new ScrambleHuman(0, 1), new ScrambleHuman(1, 12) };
        var first = Select(humans, blueSlots: 3, redSlots: 3, seed: 102);
        var repeated = Select(humans, blueSlots: 3, redSlots: 3, seed: 102);
        Assert.Equal(Signature(first), Signature(repeated));

        var signatures = Enumerable.Range(0, 16)
            .Select(seed => Signature(Select(humans, blueSlots: 3, redSlots: 3, seed)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.True(signatures > 1, $"Expected seeded variety, observed {signatures} roster(s).");
    }

    [Fact]
    public void Same_kicker_and_profile_can_receive_different_near_best_deck_variants_by_seed()
    {
        var candidate = new ScrambleBotCandidate(8, 0, "carrier");
        var decks = Enumerable.Range(1, 4).Select(variant => new ScrambleBotDeck(
            8, 92200 + variant, "carrier", variant, 0,
            new Dictionary<string, double> { ["MOVE"] = 0.6 },
            [922000 + variant * 10 + 1, 922000 + variant * 10 + 2,
                922000 + variant * 10 + 3, 922000 + variant * 10 + 4])).ToArray();

        var first = Assert.Single(ScrambleBotCompositionSelector.Select([], [candidate], decks, 1, 0, seed: 55));
        var sameSeed = Assert.Single(ScrambleBotCompositionSelector.Select([], [candidate], decks, 1, 0, seed: 55));
        Assert.Equal((first.Profile, first.Variant, first.DeckId), (sameSeed.Profile, sameSeed.Variant, sameSeed.DeckId));

        var variants = Enumerable.Range(0, 24)
            .Select(seed => Assert.Single(ScrambleBotCompositionSelector.Select([], [candidate], decks, 1, 0, seed)).Variant)
            .Distinct()
            .Count();
        Assert.True(variants > 1, $"Expected profile variants to vary across seeds, observed {variants}.");
    }

    [Fact]
    public void Constrained_or_unknown_human_rosters_still_fill_requested_slots_without_mutating_humans()
    {
        var humans = new[]
        {
            new ScrambleHuman(0, 999), new ScrambleHuman(0, 998),
            new ScrambleHuman(1, 997), new ScrambleHuman(1, 996)
        };
        var snapshot = humans.ToArray();

        var result = Select(humans, blueSlots: 2, redSlots: 2, seed: 67);

        Assert.Equal(4, result.Count);
        Assert.Equal(2, result.Count(bot => bot.TeamType == 0));
        Assert.Equal(2, result.Count(bot => bot.TeamType == 1));
        Assert.Equal(snapshot, humans);
    }

    [Fact]
    public void Selected_assignment_uses_a_registered_four_disc_deck_without_changing_its_ids()
    {
        var humans = new[] { new ScrambleHuman(0, 1), new ScrambleHuman(1, 12) };
        var decks = TestDecks();
        var result = Select(humans, blueSlots: 1, redSlots: 1, seed: 4, decks: decks);

        Assert.Equal(2, result.Count);
        foreach (var assignment in result)
        {
            var deck = Assert.Single(decks, d => d.KickerId == assignment.KickerId
                                                && d.DeckId == assignment.DeckId
                                                && d.Profile == assignment.Profile);
            Assert.Equal(deck.DiscIds, assignment.DiscIds);
            Assert.Equal(deck.Variant, assignment.Variant);
            Assert.Equal(deck.RoleType, assignment.RoleType);
            Assert.Equal(4, assignment.DiscIds.Length);
            Assert.Equal(4, assignment.DiscIds.Distinct().Count());
        }
    }

    [Fact]
    public void Deck_choice_prefers_a_role_compatible_profile_with_faster_relevant_cast_timing()
    {
        var candidate = new ScrambleBotCandidate(8, 0, "carrier");
        var slow = new ScrambleBotDeck(8, 92100, "carrier", 1, 0,
            new Dictionary<string, double> { ["MOVE"] = 4.0 }, [921001, 921002, 921003, 921004]);
        var fast = new ScrambleBotDeck(8, 92101, "carrier", 2, 0,
            new Dictionary<string, double> { ["MOVE"] = 0.2 }, [921011, 921012, 921013, 921014]);

        var result = ScrambleBotCompositionSelector.Select([], [candidate], [slow, fast], 1, 0, seed: 19);

        var selected = Assert.Single(result);
        Assert.Equal("carrier", selected.Profile);
        Assert.Equal(fast.DeckId, selected.DeckId);
        Assert.Equal(fast.DiscIds, selected.DiscIds);
    }

    [Fact]
    public void Generated_catalog_decks_resolve_to_the_same_four_runtime_disc_ids_and_have_four_variants()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/scramble_bot_disc_decks.json")));
        using var aiDecks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/masters_kicker_ai_disc_deck.json")));
        using var aiDiscs = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/masters_kicker_ai_disc.json")));
        var aiDiscById = aiDiscs.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetProperty("discId").GetInt32());
        var runtimeDecks = aiDecks.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32());
        var entries = catalog.RootElement.GetProperty("decks").EnumerateArray().ToArray();

        Assert.Equal(448, entries.Length);
        foreach (var entry in entries)
        {
            var deckId = entry.GetProperty("deckId").GetInt32();
            var discIds = entry.GetProperty("discIds").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            Assert.Equal(4, discIds.Length);
            Assert.Equal(4, discIds.Distinct().Count());
            Assert.True(runtimeDecks.TryGetValue(deckId, out var runtimeDeck), $"missing runtime AI deck {deckId}");
            var runtimeDiscIds = Enumerable.Range(1, 4)
                .Select(slot => aiDiscById[runtimeDeck!.GetProperty($"kickerAiDiscId{slot}").GetInt32()])
                .ToArray();
            Assert.Equal(discIds, runtimeDiscIds);

            var castTimes = entry.GetProperty("castTimes");
            Assert.True(castTimes.ValueKind == JsonValueKind.Object && castTimes.EnumerateObject().Any());
            Assert.All(castTimes.EnumerateObject(), field => Assert.True(field.Value.GetDouble() >= 0));
        }

        var variantsPerProfile = entries.GroupBy(entry =>
                (entry.GetProperty("kickerId").GetInt32(), entry.GetProperty("profile").GetString()))
            .Select(group => group.Select(entry => entry.GetProperty("variant").GetInt32()).Order().ToArray());
        Assert.All(variantsPerProfile, variants => Assert.Equal(new[] { 1, 2, 3, 4 }, variants));
        Assert.True(entries.Select(entry => entry.GetProperty("castTimes").GetRawText())
            .Distinct(StringComparer.Ordinal).Count() > 1, "expected per-kicker action timing variation in the generated catalog");
    }

    [Fact]
    public void Full_rosters_select_quickly_with_the_full_generated_catalog()
    {
        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        var catalog = ScrambleBotDeckCatalog.Load(root, NullLogger.Instance);
        Assert.Equal(448, catalog.Count);
        var candidates = Candidates();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();

        IReadOnlyList<ScrambleBotAssignment> roster = [];
        for (var run = 0; run < 5; run++)
            roster = ScrambleBotCompositionSelector.Select([], candidates, catalog, 4, 4, seed: run + 100);

        timer.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(8, roster.Count);
        Assert.Equal(4, roster.Count(bot => bot.TeamType == 0));
        Assert.Equal(4, roster.Count(bot => bot.TeamType == 1));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"Five full-roster selections took {timer.Elapsed}.");
        Assert.True(allocatedBytes < 128 * 1024 * 1024, $"Five full-roster selections allocated {allocatedBytes:N0} bytes on the selecting thread.");
        _output.WriteLine($"Five full-roster selections: {timer.Elapsed.TotalMilliseconds:F1} ms, {allocatedBytes:N0} bytes allocated; 448 catalog variants; 4 bots per side.");
    }

    private static void AssertRoleCoverage(ScrambleHuman human, IEnumerable<ScrambleBotAssignment> bots)
    {
        var roles = bots.Select(bot => RoleFor(bot.KickerId)).Append(RoleFor(human.KickerId)).ToArray();
        Assert.True(roles.Distinct().Count() >= 3, $"Expected complementary roles for team with kicker {human.KickerId}; got {string.Join(',', roles)}.");
    }

    private static int[] HumanCountsByTeam(
        IReadOnlyList<ScrambleHumanParty> parties, IReadOnlyDictionary<string, int> partyTeams) =>
        Enumerable.Range(0, 2).Select(team => parties
            .Where(party => partyTeams[party.Key] == team)
            .Sum(party => party.Humans.Count)).ToArray();

    private static ScrambleHumanParty[] HumanParties(params (string Key, int KickerId)[] entries) =>
        entries.Select(entry => new ScrambleHumanParty(entry.Key, [new ScrambleHuman(0, entry.KickerId)])).ToArray();

    private static string PartyPartition(ScrambleHumanTeamSelection selection)
    {
        var blue = string.Join(',', selection.PartyTeams.Where(pair => pair.Value == 0)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var red = string.Join(',', selection.PartyTeams.Where(pair => pair.Value == 1)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal));
        return string.CompareOrdinal(blue, red) <= 0 ? $"{blue}|{red}" : $"{red}|{blue}";
    }

    private static int RoleFor(int kickerId) => kickerId switch
    {
        1 or 8 or 9 => 0,
        2 or 5 or 7 or 14 => 1,
        4 or 6 or 10 or 13 => 2,
        3 or 11 or 12 => 3,
        _ => -1
    };

    private static IReadOnlyList<ScrambleBotAssignment> Select(
        IReadOnlyList<ScrambleHuman> humans, int blueSlots, int redSlots, int seed,
        IReadOnlyList<ScrambleBotDeck>? decks = null) =>
        ScrambleBotCompositionSelector.Select(humans, Candidates(), decks ?? TestDecks(), blueSlots, redSlots, seed);

    private static ScrambleBotCandidate[] Candidates() =>
    [
        new(1, 0, "carrier"), new(2, 1, "support"), new(3, 3, "anchor"), new(4, 2, "damage"),
        new(5, 1, "owlbert_support"), new(6, 2, "damage"), new(7, 1, "mobile_pressure"), new(8, 0, "carrier"),
        new(9, 0, "carrier"), new(10, 2, "hunter"), new(11, 3, "anchor"), new(12, 3, "anchor"),
        new(13, 2, "damage"), new(14, 1, "hunter")
    ];

    private static ScrambleBotDeck[] TestDecks() => Candidates()
        .Select((candidate, index) => new ScrambleBotDeck(candidate.KickerId, 92000 + candidate.KickerId,
            index % 2 == 0 ? "carrier" : "hunter", 1, candidate.RoleType,
            new Dictionary<string, double> { ["ATK(Short Distance)"] = 0.65 + index / 100d }, [930000 + candidate.KickerId * 10 + 1,
                930000 + candidate.KickerId * 10 + 2, 930000 + candidate.KickerId * 10 + 3,
                930000 + candidate.KickerId * 10 + 4]))
        .ToArray();

    private static string Signature(IEnumerable<ScrambleBotAssignment> assignments) => string.Join('|', assignments
        .OrderBy(assignment => assignment.TeamType)
        .ThenBy(assignment => assignment.KickerId)
        .Select(assignment => $"{assignment.TeamType}:{assignment.KickerId}:{assignment.DeckId}:{assignment.Profile}"));
}
