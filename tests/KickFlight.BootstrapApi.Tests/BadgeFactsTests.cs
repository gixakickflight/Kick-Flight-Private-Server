using System.Text.Json;
using KickFlight.BootstrapApi;
using KickFlight.BootstrapApi.Otomo;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The adapter's half of the badge contract, without a host: what it says to economy about a battle. The other half - what
// a fact is worth - is the tenant's documents (kf-otomo), and is not this repository's to assert.
public sealed class BadgeFactsTests
{
    private static BadgeBattleStatReport Credited(bool? won = true, BadgeBattleOutcome outcome = BadgeBattleOutcome.Win,
        bool flagged = false, bool party = false)
    {
        var report = new BadgeBattleStatReport
        {
            CreditStats = true, Reason = "ok", BattleRuleType = 2, KickerId = 11, QueuedAsParty = party, Won = won,
            Outcome = outcome, CallerFlagged = flagged
        };
        report.Stats["kills"] = 4;
        report.Stats["flag_goals"] = 2;
        report.Stats["mvp"] = 1;
        return report;
    }

    [Fact]
    public void A_battle_fact_carries_exactly_the_fields_the_tenant_declares_with_the_declared_types()
    {
        var fields = DemoSessionApi.BattleFields(Credited(), 2, 2, 9);
        var declared = FakeOtomoEconomy.FactSchemas["battle.result"];

        // Closed both ways: an extra field is refused by economy and so is a missing one.
        Assert.Equal(declared.Keys.Order(StringComparer.Ordinal), fields.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, type) in declared)
        {
            var element = JsonSerializer.SerializeToElement(fields[name]);
            Assert.True(type switch
            {
                "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "text" => element.ValueKind == JsonValueKind.String,
                "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
                _ => false
            }, $"{name} is not {type}");
        }
    }

    [Fact]
    public void A_credited_win_is_reported_with_its_bounded_stats_mode_kicker_queue_and_rank()
    {
        var fields = DemoSessionApi.BattleFields(Credited(party: true), 2, 2, 9);

        Assert.Equal(2, fields["rule_type"]);
        Assert.Equal(11, fields["kicker_id"]);
        Assert.Equal("team", fields["queue"]);
        Assert.Equal(true, fields["credited"]);
        Assert.Equal("win", fields["outcome"]);
        Assert.Equal(true, fields["won"]);
        Assert.Equal(true, fields["mvp"]);
        Assert.Equal(4L, fields["kills"]);
        Assert.Equal(2L, fields["flag_goals"]);
        Assert.Equal(0L, fields["deaths"]);
        Assert.Equal(2, fields["rank_rule"]);
        Assert.Equal(9, fields["rank_after"]);
    }

    [Fact]
    public void A_void_battle_is_still_reported_with_no_stat_no_win_and_the_reason()
    {
        var voided = BadgeBattleStatReport.Rejected("cap:kills");
        voided.Outcome = BadgeBattleOutcome.Win;
        var fields = DemoSessionApi.BattleFields(voided, 1, 0, -1);

        Assert.Equal(false, fields["credited"]);
        Assert.Equal("cap:kills", fields["credit_reason"]);
        Assert.Equal(false, fields["won"]);
        Assert.Equal(false, fields["mvp"]);
        Assert.Equal(0L, fields["kills"]);
        Assert.Equal(1, fields["rule_type"]);
        Assert.Equal(0, fields["rank_rule"]);
        Assert.Equal(-1, fields["rank_after"]);
        // The outcome is still the team's: the cap voids the stat set, not the score.
        Assert.Equal("win", fields["outcome"]);
    }

    [Theory]
    [InlineData(BadgeBattleOutcome.Loss)]
    [InlineData(BadgeBattleOutcome.Tie)]
    public void A_loss_or_a_tie_is_not_a_win(BadgeBattleOutcome outcome)
    {
        var fields = DemoSessionApi.BattleFields(Credited(won: false, outcome: outcome), 2, 0, -1);

        Assert.Equal(false, fields["won"]);
        Assert.Equal(outcome.ToString().ToLowerInvariant(), fields["outcome"]);
        Assert.Equal(true, fields["credited"]);
    }

    [Fact]
    public void A_flagged_caller_keeps_the_bounded_stats_but_is_not_credited_a_win()
    {
        var fields = DemoSessionApi.BattleFields(Credited(won: null, flagged: true), 2, 0, -1);

        Assert.Equal(true, fields["flagged"]);
        Assert.Equal(false, fields["won"]);
        Assert.Equal(4L, fields["kills"]);
    }

    [Fact]
    public void A_fact_key_names_the_thing_that_happened_within_economys_key_alphabet_and_length()
    {
        var key = DemoSessionApi.FactKey("badge-battle", "entry id/with spaces & symbols");

        Assert.Equal("badge-battle:entry-id-with-spaces---symbols", key);
        Assert.Equal(key, DemoSessionApi.FactKey("badge-battle", "entry id/with spaces & symbols"));
        var longKey = DemoSessionApi.FactKey("badge-like", new string('x', 500));
        Assert.Equal(128, longKey.Length);
        Assert.All(longKey, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-'));
    }

    [Fact]
    public void Owned_honors_are_the_default_and_the_honor_items_of_the_inventory()
    {
        EconomyItem Item(string id, long qty) => new(id, qty, false, null, null);
        var snapshot = new EconomySnapshot([], [Item("honor:6010501", 1), Item("honor:6010101", 1), Item("disc:3010001", 1),
            Item("honor:6019999", 0), Item("honor:bogus", 1), Item("skin:2010101", 1)], [], []);

        Assert.Equal([6010000, 6010101, 6010501], DemoSessionApi.OwnedHonorIds(snapshot));
        Assert.Equal([BadgeCatalog.DefaultHonorId], DemoSessionApi.OwnedHonorIds(EconomySnapshot.None));
        Assert.True(DemoSessionApi.TryHonorId("honor:6010101", out var id));
        Assert.Equal(6010101, id);
        Assert.False(DemoSessionApi.TryHonorId("disc:3010001", out _));
        Assert.Equal("honor:6010101", DemoSessionApi.HonorItem(6010101));
    }

    [Fact]
    public void The_honor_master_still_lists_every_catalog_entry_for_the_client()
    {
        var path = Path.Combine(RepositoryPaths.FindRoot(AppContext.BaseDirectory), BadgeCatalog.CatalogPath);
        using var rows = JsonDocument.Parse(BadgeCatalog.BuildHonorMasterJson(File.ReadAllText(path)));

        Assert.Equal(211, rows.RootElement.GetArrayLength());
        Assert.Contains(rows.RootElement.EnumerateArray(), row => row.GetProperty("id").GetInt32() == BadgeCatalog.DefaultHonorId);
        Assert.Single(BadgeCatalog.Measure("{\"entries\":[{\"id\":1,\"requirements\":{\"kind\":\"win_count\"}}]}").Unported);
    }
}
