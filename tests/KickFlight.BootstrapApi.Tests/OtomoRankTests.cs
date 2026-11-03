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
    public async Task An_award_is_idempotent_by_key_and_session_floors_points_at_zero()
    {
        using var client = NewClient();
        var player = NewAccount();

        var first = await client.AwardAsync(player, "flag_flight", 50, "battle:e1:" + player, CancellationToken.None);
        Assert.True(first.Applied);
        Assert.Equal(50, first.Points);
        Assert.Equal("rank_1", first.Tier);

        var repeat = await client.AwardAsync(player, "flag_flight", 50, "battle:e1:" + player, CancellationToken.None);
        Assert.False(repeat.Applied);
        Assert.Equal(50, repeat.Points);

        // The key is per player and rule type: the same key on another rule type is a different award.
        var other = await client.AwardAsync(player, "rapid_ball", 50, "battle:e1:" + player, CancellationToken.None);
        Assert.True(other.Applied);

        var down = await client.AwardAsync(player, "flag_flight", -20, "battle:e2:" + player, CancellationToken.None);
        Assert.Equal(30, down.Points);
        var floor = await client.AwardAsync(player, "flag_flight", -20, "battle:e3:" + player, CancellationToken.None);
        Assert.Equal(10, floor.Points);
        var floored = await client.AwardAsync(player, "flag_flight", -20, "battle:e4:" + player, CancellationToken.None);
        Assert.Equal(0, floored.Points);
        var stillZero = await client.AwardAsync(player, "flag_flight", -20, "battle:e5:" + player, CancellationToken.None);
        Assert.Equal(0, stillZero.Points);
    }

    [Fact]
    public async Task Another_players_standing_lists_every_rule_type_and_the_own_read_uses_the_players_token()
    {
        using var client = NewClient();
        var player = NewAccount();
        await client.AwardAsync(player, "crystal_scramble", 700, "k1", CancellationToken.None);

        var ranks = await client.GetPlayerAsync(player, CancellationToken.None);
        Assert.Equal(["crystal_scramble", "flag_flight", "rapid_ball"], ranks.Select(rank => rank.RuleType).Order().ToArray());
        Assert.Equal(700, ranks.Single(rank => rank.RuleType == "crystal_scramble").Points);
        Assert.Equal("rank_3", ranks.Single(rank => rank.RuleType == "crystal_scramble").Tier);
        Assert.All(ranks.Where(rank => rank.RuleType != "crystal_scramble"), rank =>
        {
            Assert.Equal(0, rank.Points);
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
            client.AwardAsync(NewAccount(), "no_such_rule", 50, "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Refused, rule.Failure);
        Assert.Equal("unknown_rule_type", rule.Code);

        var player = NewAccount();
        OtomoTestEnvironment.Social.SetAwardsUnavailable(player, true);
        var down = await Assert.ThrowsAsync<OtomoRankException>(() =>
            client.AwardAsync(player, "flag_flight", 50, "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Unavailable, down.Failure);

        using var wrongKey = NewClient(options => options.ServiceKey = "not-the-key");
        var refused = await Assert.ThrowsAsync<OtomoRankException>(() =>
            wrongKey.AwardAsync(NewAccount(), "flag_flight", 50, "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Refused, refused.Failure);
        Assert.Equal(401, refused.StatusCode);

        using var noKey = NewClient(options => options.ServiceKey = "");
        var disabled = await Assert.ThrowsAsync<OtomoRankException>(() =>
            noKey.AwardAsync(NewAccount(), "flag_flight", 50, "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Disabled, disabled.Failure);

        using var off = NewClient(options => options.Enabled = false);
        var switchedOff = await Assert.ThrowsAsync<OtomoRankException>(() => off.GetOwnAsync("t", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Disabled, switchedOff.Failure);

        using var unreachable = NewClient(options => options.InternalBaseUrl = "http://127.0.0.1:1");
        var gone = await Assert.ThrowsAsync<OtomoRankException>(() =>
            unreachable.AwardAsync(NewAccount(), "flag_flight", 50, "k", CancellationToken.None));
        Assert.Equal(OtomoRankFailure.Unavailable, gone.Failure);
    }

    [Fact]
    public void Units_translate_in_one_place_and_a_tier_key_is_rank_N()
    {
        Assert.Equal(200, RankTranslation.ClientPoints(0));
        Assert.Equal(7500, RankTranslation.ClientPoints(7300));
        Assert.Equal(50, RankTranslation.WinDelta);
        Assert.Equal(-20, RankTranslation.LossDelta);
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

        // min_points + 200 for every tier, identical to the table the adapter used to carry by hand.
        int[] retail = [200, 500, 900, 1400, 1900, 2400, 2900, 3400, 3900, 4400, 4900, 5400, 5900, 6400, 6900, 7500];
        Assert.Equal(retail, ladder.Ranks.Select(ladder.ClientTotalBattlePoint).ToArray());

        using var master = JsonDocument.Parse(DemoSessionApi.BuildBattleRankMasterJson(ladder));
        var rows = master.RootElement.EnumerateArray().ToArray();
        Assert.Equal(17, rows.Length);
        Assert.Equal(0, rows[0].GetProperty("totalBattlePoint").GetInt32());
        Assert.Equal(retail, rows.Skip(1).Select(row => row.GetProperty("totalBattlePoint").GetInt32()).ToArray());

        // The thresholds drive the progress read as well.
        Assert.Equal(300, ladder.RemainingToNextTier(1100)); // 900 -> 1400 is rank 3 -> 4; 1100 client = 900 session
        Assert.Equal(1, ladder.RemainingToNextTier(499));
        Assert.Equal(0, ladder.RemainingToNextTier(7500));
        Assert.Equal(0, ladder.RemainingToNextTier(9000));
        Assert.Equal(3, ladder.RankFor(900));
        Assert.Equal(16, ladder.RankFor(100_000));
        Assert.Equal(1, ladder.RankFor(0));
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
