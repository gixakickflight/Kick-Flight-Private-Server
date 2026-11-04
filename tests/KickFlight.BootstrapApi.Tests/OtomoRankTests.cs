using System.Text.Json;
using KickFlight.BootstrapApi.Otomo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

// The rank client against the fake session service (real loopback HTTP), the unit translation, and the published
// ladder: what the adapter knows about battle points is exactly this much and no more.
public sealed class OtomoRankTests
{
    // A bearer the fake session reads the subject from (its `sub` claim), like the tokens the auth fake mints.
    private static string TokenFor(string account) =>
        "h." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = account })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";

    private static string NewAccount() => "rank-" + Guid.NewGuid().ToString("N");

    private static OtomoRank NewClient(Action<OtomoRankOptions>? configure = null)
    {
        var options = new OtomoRankOptions
        {
            Enabled = true,
            BaseUrl = OtomoTestEnvironment.Social.BaseUrl,
            InternalBaseUrl = OtomoTestEnvironment.Social.BaseUrl,
            ServiceKey = FakeOtomoSocial.ServiceKey,
            Timeout = TimeSpan.FromSeconds(10),
            RetryDelay = TimeSpan.Zero
        };
        configure?.Invoke(options);
        return OtomoRank.Create(Options.Create(options), Options.Create(new OtomoSocialOptions()),
            NullLogger<OtomoRank>.Instance);
    }

    [Fact]
    public async Task An_award_by_outcome_is_idempotent_by_key_and_session_clamps_at_the_floor_and_ceiling()
    {
        // Stale: it awarded raw deltas (+50, -20) from 0 and floored at 0. An award now names a declared outcome, a
        // new player starts at start_points (200), and session clamps to the ladder's floor (200) and ceiling (7500).
        using var client = NewClient();
        var player = NewAccount();

        var first = await client.AwardAsync(player, "flag_flight", "win", "battle:e1:" + player, CancellationToken.None);
        Assert.True(first.Applied);
        Assert.Equal("win", first.Outcome);
        Assert.Equal(250, first.Points);
        Assert.Equal("rank_1", first.Tier);

        var repeat = await client.AwardAsync(player, "flag_flight", "win", "battle:e1:" + player, CancellationToken.None);
        Assert.False(repeat.Applied);
        Assert.Equal(250, repeat.Points);

        // The key is per player and rule type: the same key on another rule type is a different award.
        var other = await client.AwardAsync(player, "rapid_ball", "win", "battle:e1:" + player, CancellationToken.None);
        Assert.True(other.Applied);

        var down = await client.AwardAsync(player, "flag_flight", "loss", "battle:e2:" + player, CancellationToken.None);
        Assert.Equal(230, down.Points);
        var floor = await client.AwardAsync(player, "flag_flight", "loss", "battle:e3:" + player, CancellationToken.None);
        Assert.Equal(210, floor.Points);
        var floored = await client.AwardAsync(player, "flag_flight", "loss", "battle:e4:" + player, CancellationToken.None);
        Assert.Equal(200, floored.Points);
        var stillAtFloor = await client.AwardAsync(player, "flag_flight", "loss", "battle:e5:" + player, CancellationToken.None);
        Assert.Equal(200, stillAtFloor.Points);

        // The ceiling clamps the other way.
        var rich = NewAccount();
        OtomoTestEnvironment.Social.SeedRank(rich, "flag_flight", 7480);
        var capped = await client.AwardAsync(rich, "flag_flight", "win", "battle:e1:" + rich, CancellationToken.None);
        Assert.Equal(7500, capped.Points);
        Assert.Equal("rank_16", capped.Tier);
    }

    [Fact]
    public async Task Another_players_standing_lists_every_rule_type_and_the_own_read_uses_the_players_token()
    {
        using var client = NewClient();
        var player = NewAccount();
        OtomoTestEnvironment.Social.SeedRank(player, "crystal_scramble", 900);

        var ranks = await client.GetPlayerAsync(player, CancellationToken.None);
        Assert.Equal(["crystal_scramble", "flag_flight", "rapid_ball"], ranks.Select(rank => rank.RuleType).Order().ToArray());
        Assert.Equal(900, ranks.Single(rank => rank.RuleType == "crystal_scramble").Points);
        Assert.Equal("rank_3", ranks.Single(rank => rank.RuleType == "crystal_scramble").Tier);
        Assert.All(ranks.Where(rank => rank.RuleType != "crystal_scramble"), rank =>
        {
            Assert.Equal(200, rank.Points); // no row reads as start_points
            Assert.Equal("rank_1", rank.Tier);
        });

        // The player plane carries no id: the subject is the token's, and only played rule types are listed.
        var token = TokenFor(player);
        var own = await client.GetOwnAsync(token, CancellationToken.None);
        Assert.Equal("crystal_scramble", Assert.Single(own).RuleType);
    }

    [Fact]
    public async Task Failures_are_the_three_way_split_the_screens_answer_503_for()
    {
        using var client = NewClient();

        var unknown = await Assert.ThrowsAsync<OtomoRankException>(() =>
            client.GetPlayerAsync("ghost-" + Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Refused, unknown.Failure);
        Assert.Equal(404, unknown.StatusCode);
        Assert.Equal("player_not_found", unknown.Code);

        var rule = await Assert.ThrowsAsync<OtomoRankException>(() =>
            client.AwardAsync(NewAccount(), "no_such_rule", "win", "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Refused, rule.Failure);
        Assert.Equal("unknown_rule_type", rule.Code);

        var outcome = await Assert.ThrowsAsync<OtomoRankException>(() =>
            client.AwardAsync(NewAccount(), "flag_flight", "draw", "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Refused, outcome.Failure);
        Assert.Equal(400, outcome.StatusCode);
        Assert.Equal("unknown_outcome", outcome.Code);

        var player = NewAccount();
        OtomoTestEnvironment.Social.SetAwardsUnavailable(player, true);
        var down = await Assert.ThrowsAsync<OtomoRankException>(() =>
            client.AwardAsync(player, "flag_flight", "win", "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Unavailable, down.Failure);

        using var wrongKey = NewClient(options => options.ServiceKey = "not-the-key");
        var refused = await Assert.ThrowsAsync<OtomoRankException>(() =>
            wrongKey.AwardAsync(NewAccount(), "flag_flight", "win", "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Refused, refused.Failure);
        Assert.Equal(401, refused.StatusCode);

        using var noKey = NewClient(options => options.ServiceKey = "");
        var disabled = await Assert.ThrowsAsync<OtomoRankException>(() =>
            noKey.AwardAsync(NewAccount(), "flag_flight", "win", "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Disabled, disabled.Failure);

        using var off = NewClient(options => options.Enabled = false);
        var switchedOff = await Assert.ThrowsAsync<OtomoRankException>(() => off.GetOwnAsync("t", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Disabled, switchedOff.Failure);

        using var unreachable = NewClient(options => options.InternalBaseUrl = "http://127.0.0.1:1");
        var gone = await Assert.ThrowsAsync<OtomoRankException>(() =>
            unreachable.AwardAsync(NewAccount(), "flag_flight", "win", "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Unavailable, gone.Failure);
    }

    [Fact]
    public void A_tier_key_is_rank_N_and_nothing_else_is_translated()
    {
        // Stale: it asserted the +200 offset and the 50 / -20 deltas, which are no longer the adapter's.
        Assert.Equal(1, RankTranslation.RankNumber("rank_1"));
        Assert.Equal(16, RankTranslation.RankNumber("rank_16"));
        Assert.Null(RankTranslation.RankNumber("7"));
        Assert.Null(RankTranslation.RankNumber("rank_"));
        Assert.Null(RankTranslation.RankNumber("rank_-1"));
        Assert.Null(RankTranslation.RankNumber("Rank_1"));
        Assert.Null(RankTranslation.RankNumber(null));
    }

    [Fact]
    public void The_published_ladder_gives_the_battle_rank_master_its_retail_thresholds()
    {
        var ladder = RankLadder.Parse(FakeOtomoSocial.DefaultLadderDocument());
        Assert.Equal(["crystal_scramble", "flag_flight", "rapid_ball"], ladder.RuleTypes);
        Assert.Equal(Enumerable.Range(1, 16), ladder.Ranks);

        // Each tier's min_points as published, with no offset, is the table the adapter used to carry by hand.
        int[] retail = [200, 500, 900, 1400, 1900, 2400, 2900, 3400, 3900, 4400, 4900, 5400, 5900, 6400, 6900, 7500];
        Assert.Equal(retail, ladder.Ranks.Select(ladder.MinPoints).ToArray());
        Assert.Equal(200, ladder.StartPoints);

        using var master = JsonDocument.Parse(DemoSessionApi.BuildBattleRankMasterJson(ladder));
        var rows = master.RootElement.EnumerateArray().ToArray();
        Assert.Equal(17, rows.Length);
        Assert.Equal(0, rows[0].GetProperty("totalBattlePoint").GetInt32());
        Assert.Equal(retail, rows.Skip(1).Select(row => row.GetProperty("totalBattlePoint").GetInt32()).ToArray());

        // The thresholds drive the progress read as well.
        Assert.Equal(300, ladder.RemainingToNextTier(1100)); // 900 -> 1400 is rank 3 -> 4
        Assert.Equal(1, ladder.RemainingToNextTier(499));
        Assert.Equal(0, ladder.RemainingToNextTier(7500));
        Assert.Equal(0, ladder.RemainingToNextTier(9000));
        Assert.Equal(3, ladder.RankFor(900));
        Assert.Equal(2, ladder.RankFor(899));
        Assert.Equal(16, ladder.RankFor(100_000));
        Assert.Equal(1, ladder.RankFor(0)); // below the first tier is still the lowest tier
    }

    [Fact]
    public void A_ladder_the_adapter_cannot_fully_read_is_refused_not_repaired()
    {
        static void Rejects(string json) => Assert.ThrowsAny<Exception>(() => RankLadder.Parse(json));
        Rejects("[]");
        Rejects("""{"tiers":[]}""");
        Rejects("""{"tiers":[{"key":"gold","min_points":0}]}""");
        Rejects("""{"tiers":[{"key":"rank_1","min_points":0},{"key":"rank_1","min_points":10}]}""");
        Rejects("""{"tiers":[{"key":"rank_1","min_points":100},{"key":"rank_2","min_points":50}]}""");
        Rejects("""{"tiers":[{"key":"rank_1"}]}""");

        // A ladder with a rank the client has no badge for cannot become a BattleRank master.
        var tooMany = RankLadder.Parse(JsonSerializer.Serialize(new
        {
            tiers = Enumerable.Range(1, 17).Select(rank => new { key = "rank_" + rank, min_points = rank * 100 })
        }));
        Assert.Throws<InvalidDataException>(() => DemoSessionApi.BuildBattleRankMasterJson(tooMany));
    }

    [Fact]
    public async Task The_host_refuses_to_start_when_the_release_does_not_carry_the_ladder()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.UseSetting("Otomo:Masters:Documents:0", "session.not-published");
            builder.UseSetting("Otomo:Masters:CacheDir",
                Path.Combine(Path.GetTempPath(), "kf-test-masters-empty-" + Guid.NewGuid().ToString("N")));
        });

        // Nothing serves a ladder of its own: with the document missing and no cached bundle, the host does not boot.
        var failure = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));
        Assert.NotNull(failure);
        Assert.Contains("session.not-published", failure!.ToString());
    }
}
