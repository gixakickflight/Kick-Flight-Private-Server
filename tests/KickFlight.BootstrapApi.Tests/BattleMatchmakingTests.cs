using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Grpc.Core;
using KickFlight.BootstrapApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenMatch;
using Xunit;

namespace KickFlight.BootstrapApi.Tests;

[Collection("Matchmaking static state")]
public sealed class BattleMatchmakingTests
{
    [Fact]
    public async Task Streaming_match_is_canonical_and_completed_assignment_is_replayable()
    {
        var service = CreateService();
        var (_, ticket4) = service.RegisterEntry("1000004", "Player 0004", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (_, ticket3) = service.RegisterEntry("1000003", "Player 0003", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);

        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        // The first stream gets Stage 1, the interim roster that the join broadcasts to it, Stage 2 and Stage 3.
        var firstWriter = new CollectingWriter(expectedCount: 4, firstCancellation);
        var secondWriter = new CollectingWriter(expectedCount: 3, secondCancellation);

        var firstStream = service.StreamAssignmentsAsync(ticket4, firstWriter, firstCancellation.Token);
        await firstWriter.FirstWrite;
        var secondStream = service.StreamAssignmentsAsync(ticket3, secondWriter, secondCancellation.Token);

        await Task.WhenAll(firstWriter.WaitForExpectedWritesAsync(), secondWriter.WaitForExpectedWritesAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondStream);

        // The join does not start the match: both clients wait out the window before the bots fill in.
        Assert.Equal(0, Bots(secondWriter.Responses[0].Assignment));

        var firstFinal = firstWriter.Responses[^1].Assignment;
        var secondFinal = secondWriter.Responses[^1].Assignment;
        Assert.NotEmpty(firstFinal.Connection);
        Assert.Equal(firstFinal.Connection, secondFinal.Connection);
        Assert.Equal("1000003", ReadFirstRosterUser(firstFinal));
        Assert.Equal("1000003", ReadFirstRosterUser(secondFinal));

        using var replayCancellation = new CancellationTokenSource();
        var replayWriter = new CollectingWriter(expectedCount: 1, replayCancellation);
        var replayStream = service.StreamAssignmentsAsync(ticket4, replayWriter, replayCancellation.Token);
        await replayWriter.WaitForExpectedWritesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replayStream);

        Assert.Single(replayWriter.Responses);
        Assert.Equal(firstFinal.Connection, replayWriter.Responses[0].Assignment.Connection);
    }

    [Fact]
    public async Task A_second_room_can_reach_stage_three_while_the_first_room_stream_is_still_active()
    {
        var service = CreateService();
        service.MatchWindow = TimeSpan.FromMilliseconds(50);

        var (_, firstTicket) = service.RegisterEntry("1000001", "First room", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (_, secondTicket) = service.RegisterEntry("1000002", "Second room", 2, 1, 1, [3010001, 3010002, 3010003, 3010004]);

        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        // A single client receives Stage 1, Stage 2, and Stage 3. Keep each stream alive until the test cancels it.
        var firstWriter = new CollectingWriter(expectedCount: 4, firstCancellation);
        var secondWriter = new CollectingWriter(expectedCount: 4, secondCancellation);
        var firstStream = service.StreamAssignmentsAsync(firstTicket, firstWriter, firstCancellation.Token);
        Task? secondStream = null;

        try
        {
            var firstFinal = await firstWriter.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(firstFinal.Assignment.Connection);
            Assert.False(firstStream.IsCompleted);

            // Room one has finalized and left _pendingRoom, but its Stage 3 acknowledgement stream is still active.
            secondStream = service.StreamAssignmentsAsync(secondTicket, secondWriter, secondCancellation.Token);
            var secondFinal = await secondWriter.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotEmpty(secondFinal.Assignment.Connection);
            Assert.NotEqual(firstFinal.Assignment.Connection, secondFinal.Assignment.Connection);
            Assert.False(firstStream.IsCompleted);
            Assert.False(secondStream.IsCompleted);
        }
        finally
        {
            firstCancellation.Cancel();
            secondCancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStream);
        if (secondStream is not null)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondStream);
        }
    }

    [Fact]
    public async Task Three_humans_share_the_first_room_instead_of_opening_a_second_one()
    {
        var service = CreateService();

        var (_, ticket1) = service.RegisterEntry("1000001", "Player 0001", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (_, ticket2) = service.RegisterEntry("1000002", "Player 0002", 2, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (_, ticket3) = service.RegisterEntry("1000003", "Player 0003", 3, 1, 1, [3010001, 3010002, 3010003, 3010004]);

        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        using var thirdCancellation = new CancellationTokenSource();
        // Stage 1 + one interim roster per later join + Stage 2 + Stage 3.
        var firstWriter = new CollectingWriter(expectedCount: 5, firstCancellation);
        var secondWriter = new CollectingWriter(expectedCount: 4, secondCancellation);
        var thirdWriter = new CollectingWriter(expectedCount: 3, thirdCancellation);

        var firstStream = service.StreamAssignmentsAsync(ticket1, firstWriter, firstCancellation.Token);
        await firstWriter.FirstWrite;
        var secondStream = service.StreamAssignmentsAsync(ticket2, secondWriter, secondCancellation.Token);
        await secondWriter.FirstWrite;
        var thirdStream = service.StreamAssignmentsAsync(ticket3, thirdWriter, thirdCancellation.Token);

        await Task.WhenAll(
            firstWriter.WaitForExpectedWritesAsync(),
            secondWriter.WaitForExpectedWritesAsync(),
            thirdWriter.WaitForExpectedWritesAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondStream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => thirdStream);

        // One room, three humans, and the waiting clients watched each other arrive: no second room, no early bots.
        var connection = firstWriter.Responses[^1].Assignment.Connection;
        Assert.NotEmpty(connection);
        Assert.Equal(connection, secondWriter.Responses[^1].Assignment.Connection);
        Assert.Equal(connection, thirdWriter.Responses[^1].Assignment.Connection);
        Assert.Equal(2, Roster(firstWriter.Responses[1].Assignment).Count); // Player 0001 + the join
        Assert.Equal(3, Roster(firstWriter.Responses[2].Assignment).Count); // + Player 0003, still no bots
        Assert.Equal(0, Bots(firstWriter.Responses[2].Assignment));

        var roster = Roster(firstWriter.Responses[^1].Assignment);
        Assert.Equal(8, roster.Count);
        Assert.Equal(3, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0));
        Assert.Equal(4, roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 0));
        Assert.Equal(4, roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 1));
    }

    [Fact]
    public async Task Solo_player_still_gets_a_full_roster_of_bots_when_the_window_closes()
    {
        var service = CreateService();

        var (_, ticket) = service.RegisterEntry("1000001", "Solo", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);

        using var cancellation = new CancellationTokenSource();
        var writer = new CollectingWriter(expectedCount: 3, cancellation);

        var stream = service.StreamAssignmentsAsync(ticket, writer, cancellation.Token);
        await writer.WaitForExpectedWritesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);

        Assert.Empty(writer.Responses[0].Assignment.Connection);
        Assert.Empty(writer.Responses[1].Assignment.Connection);
        Assert.NotEmpty(writer.Responses[^1].Assignment.Connection);

        var roster = Roster(writer.Responses[^1].Assignment);
        Assert.Equal(8, roster.Count);
        Assert.Equal("1000001", roster[0].GetProperty("userId").GetString());
        Assert.Equal(7, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));

        var costumeRows = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RepositoryPaths.FindRoot(AppContext.BaseDirectory), "config/masters_kicker_costume.json")));
        var costumesById = costumeRows.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetProperty("kickerId").GetInt32());
        var kickerRows = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RepositoryPaths.FindRoot(AppContext.BaseDirectory), "config/masters_kicker_parameter.json")));
        var kickerIds = kickerRows.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("id").GetInt32())
            .ToHashSet();
        foreach (var bot in roster.Where(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0))
        {
            var kickerId = bot.GetProperty("kickerId").GetInt32();
            var costumeRowId = bot.GetProperty("kickerCostumeId").GetInt32();
            Assert.True(costumesById.TryGetValue(costumeRowId, out var costumeOwner), $"missing costume row {costumeRowId}");
            Assert.Equal(kickerId, costumeOwner);
            Assert.Contains(kickerId, kickerIds);
        }

        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var aiDecks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/masters_kicker_ai_disc_deck.json")));
        using var aiDiscs = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/masters_kicker_ai_disc.json")));
        var deck3 = Assert.Single(aiDecks.RootElement.EnumerateArray(), row => row.GetProperty("id").GetInt32() == 3);
        var aiDiscById = aiDiscs.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetProperty("discId").GetInt32());
        var deck3DiscIds = Enumerable.Range(1, 4)
            .Select(slot => aiDiscById[deck3.GetProperty($"kickerAiDiscId{slot}").GetInt32()])
            .ToArray();
        Assert.Equal(new[] { 3010020, 3010022, 3010134, 3010082 }, deck3DiscIds);

        if (Environment.GetEnvironmentVariable("KF_TEST_BOT_DISCS") == "1")
        {
            foreach (var bot in roster.Where(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0))
            {
                Assert.Equal(3, bot.GetProperty("kickerAiDiscDeckId").GetInt32());
                Assert.Equal(deck3DiscIds, Enumerable.Range(1, 4)
                    .Select(slot => bot.GetProperty($"discId{slot}").GetInt32()).ToArray());
            }
        }
    }

    [Fact]
    public async Task Scramble_roster_serializes_the_selected_catalog_deck_and_keeps_existing_ai_parameters()
    {
        var service = CreateService(withCatalog: true);
        service.MatchWindow = TimeSpan.FromMilliseconds(30);
        var (_, ticket) = service.RegisterEntry("1000001", "Scramble human", 1, 2010101, 1,
            [3010001, 3010002, 3010003, 3010004]);
        using var cancellation = new CancellationTokenSource();
        var writer = new CollectingWriter(expectedCount: 3, cancellation);
        var stream = service.StreamAssignmentsAsync(ticket, writer, cancellation.Token);

        await writer.WaitForExpectedWritesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);

        var root = RepositoryPaths.FindRoot(AppContext.BaseDirectory);
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/scramble_bot_disc_decks.json")));
        using var aiDecks = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/masters_kicker_ai_disc_deck.json")));
        using var aiDiscs = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/masters_kicker_ai_disc.json")));
        var aiDiscById = aiDiscs.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetProperty("discId").GetInt32());
        var deckLinks = aiDecks.RootElement.EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32());
        var generatedDeckRows = catalog.RootElement.GetProperty("decks").EnumerateArray()
            .ToDictionary(row => row.GetProperty("deckId").GetInt32());
        var roster = Roster(writer.Responses[^1].Assignment);
        Assert.Equal(8, roster.Count);
        var human = Assert.Single(roster, row => row.GetProperty("kickerAiParameterId").GetInt32() == 0);
        Assert.Equal("1000001", human.GetProperty("userId").GetString());
        Assert.Equal(1, human.GetProperty("kickerId").GetInt32());
        Assert.Equal(2010101, human.GetProperty("kickerCostumeId").GetInt32());
        Assert.Equal(0, human.GetProperty("kickerAiDiscDeckId").GetInt32());
        Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 }, Enumerable.Range(1, 4)
            .Select(slot => human.GetProperty($"discId{slot}").GetInt32()).ToArray());

        var bots = roster.Where(row => row.GetProperty("kickerAiParameterId").GetInt32() > 0).ToArray();
        Assert.Equal(7, bots.Length);
        foreach (var bot in bots)
        {
            var kickerId = bot.GetProperty("kickerId").GetInt32();
            Assert.Equal(kickerId, bot.GetProperty("kickerAiParameterId").GetInt32());
            var deckId = bot.GetProperty("kickerAiDiscDeckId").GetInt32();
            Assert.True(generatedDeckRows.TryGetValue(deckId, out var catalogDeck), $"missing catalog deck {deckId}");
            Assert.Equal(kickerId, catalogDeck!.GetProperty("kickerId").GetInt32());
            var selectedDiscIds = Enumerable.Range(1, 4)
                .Select(slot => bot.GetProperty($"discId{slot}").GetInt32()).ToArray();
            Assert.Equal(catalogDeck.GetProperty("discIds").EnumerateArray().Select(value => value.GetInt32()), selectedDiscIds);
            Assert.Equal(selectedDiscIds, Enumerable.Range(1, 4)
                .Select(slot => aiDiscById[deckLinks[deckId].GetProperty($"kickerAiDiscId{slot}").GetInt32()]).ToArray());
        }
    }

    [Fact]
    public async Task Scramble_rebalances_solo_humans_without_changing_their_account_kicker_costume_or_discs()
    {
        var service = CreateService(withCatalog: true);
        service.MatchWindow = TimeSpan.FromSeconds(2);
        var expected = new Dictionary<string, (int KickerId, int CostumeId, int[] DiscIds)>(StringComparer.Ordinal);
        var tickets = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var userId = $"100000{index + 1}";
            var kickerId = index + 1;
            var costumeId = 2010101 + index;
            var discIds = Enumerable.Range(0, 4).Select(slot => 3010001 + index * 10 + slot).ToArray();
            expected.Add(userId, (kickerId, costumeId, discIds));
            var (_, ticket) = service.RegisterEntry(userId, $"Scramble human {index + 1}", index + 1,
                costumeId, 1, discIds.ToList());
            tickets.Add(ticket);
        }

        var cancellations = tickets.Select(_ => new CancellationTokenSource()).ToArray();
        var writers = cancellations.Select(cancellation => new CollectingWriter(100, cancellation)).ToArray();
        var streams = new List<Task>();
        try
        {
            for (var index = 0; index < tickets.Count; index++)
                streams.Add(service.StreamAssignmentsAsync(tickets[index], writers[index], cancellations[index].Token));
            await Task.WhenAll(writers.Select(writer => writer.FirstWrite));

            var finals = await Task.WhenAll(writers.Select(writer =>
                writer.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8))));
            var roster = Roster(finals[0].Assignment);
            Assert.Equal(8, roster.Count);
            var humans = roster.Where(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0).ToArray();
            Assert.Equal(4, humans.Length);
            Assert.Equal(new[] { 2, 2 }, humans.GroupBy(entry => entry.GetProperty("teamType").GetInt32())
                .Select(group => group.Count()).Order().ToArray());

            foreach (var human in humans)
            {
                var userId = human.GetProperty("userId").GetString()!;
                var source = expected[userId];
                Assert.Equal(source.KickerId, human.GetProperty("kickerId").GetInt32());
                Assert.Equal(source.CostumeId, human.GetProperty("kickerCostumeId").GetInt32());
                Assert.Equal(0, human.GetProperty("kickerAiParameterId").GetInt32());
                Assert.Equal(0, human.GetProperty("kickerAiDiscDeckId").GetInt32());
                Assert.Equal(source.DiscIds, Enumerable.Range(1, 4)
                    .Select(slot => human.GetProperty($"discId{slot}").GetInt32()).ToArray());
            }
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Cancel();
            try
            {
                await Task.WhenAll(streams.Select(async stream =>
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream)));
            }
            finally
            {
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }
    }

    [Fact]
    public async Task Non_scramble_and_gym_keep_the_legacy_bot_deck_and_gym_ai_ids()
    {
        var previousGym = BattleMatchmakingService.GymEnabled;
        try
        {
            BattleMatchmakingService.GymEnabled = false;
            var service = CreateService(withCatalog: true);
            service.MatchWindow = TimeSpan.FromMilliseconds(30);
            var (_, ticket) = service.RegisterEntry("1000001", "Unknown mode human", 1, 2010101, 100,
                [3010001, 3010002, 3010003, 3010004]);
            var roster = await FinalRoster(service, ticket);
            var ordinaryBots = roster.Where(row => row.GetProperty("kickerAiParameterId").GetInt32() > 0).ToArray();
            Assert.Equal(7, ordinaryBots.Length);
            Assert.Equal(5, ordinaryBots[0].GetProperty("kickerId").GetInt32());
            Assert.All(ordinaryBots, bot =>
            {
                Assert.Equal(1, bot.GetProperty("kickerAiDiscDeckId").GetInt32());
                Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 }, Enumerable.Range(1, 4)
                    .Select(slot => bot.GetProperty($"discId{slot}").GetInt32()).ToArray());
            });

            BattleMatchmakingService.GymEnabled = true;
            var gymService = CreateService(withCatalog: true);
            gymService.MatchWindow = TimeSpan.FromMilliseconds(30);
            var (_, gymTicket) = gymService.RegisterEntry("1000002", "Gym human", 1, 2010101, 1,
                [3010001, 3010002, 3010003, 3010004]);
            var gymRoster = await FinalRoster(gymService, gymTicket);
            var gymBots = gymRoster.Where(row => row.GetProperty("kickerAiParameterId").GetInt32() > 0).ToArray();
            Assert.Equal(BattleMatchmakingService.GymBotCount, gymBots.Length);
            Assert.All(gymBots, bot =>
            {
                Assert.True(bot.GetProperty("kickerAiParameterId").GetInt32() >= BattleMatchmakingService.GymAiParameterBase);
                Assert.Equal(1, bot.GetProperty("kickerAiDiscDeckId").GetInt32());
                Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 }, Enumerable.Range(1, 4)
                    .Select(slot => bot.GetProperty($"discId{slot}").GetInt32()).ToArray());
            });
        }
        finally
        {
            BattleMatchmakingService.GymEnabled = previousGym;
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Supported_non_gym_modes_use_registered_composition_decks(int battleRuleId)
    {
        var service = CreateService(withCatalog: true);
        service.MatchWindow = TimeSpan.FromMilliseconds(30);
        var (_, ticket) = service.RegisterEntry("1000001", "Mode human", 1, 2010101, battleRuleId,
            [3010001, 3010002, 3010003, 3010004]);

        var roster = await FinalRoster(service, ticket);
        var bots = roster.Where(row => row.GetProperty("kickerAiParameterId").GetInt32() > 0).ToArray();

        Assert.Equal(7, bots.Length);
        Assert.Contains(bots, bot => bot.GetProperty("kickerAiDiscDeckId").GetInt32() != 1);
        Assert.All(bots, bot => Assert.All(Enumerable.Range(1, 4), slot =>
            Assert.True(bot.GetProperty($"discId{slot}").GetInt32() > 0)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Supported_modes_keep_the_gym_mannequin_roster(int battleRuleId)
    {
        var previousGym = BattleMatchmakingService.GymEnabled;
        try
        {
            BattleMatchmakingService.GymEnabled = true;
            var service = CreateService(withCatalog: true);
            service.MatchWindow = TimeSpan.FromMilliseconds(30);
            var (_, ticket) = service.RegisterEntry("1000001", "Gym mode human", 1, 2010101, battleRuleId,
                [3010001, 3010002, 3010003, 3010004]);

            var roster = await FinalRoster(service, ticket);
            var bots = roster.Where(row => row.GetProperty("kickerAiParameterId").GetInt32() > 0).ToArray();

            Assert.Equal(BattleMatchmakingService.GymBotCount, bots.Length);
            Assert.All(bots, bot =>
            {
                Assert.True(bot.GetProperty("kickerAiParameterId").GetInt32() >= BattleMatchmakingService.GymAiParameterBase);
                Assert.Equal(1, bot.GetProperty("kickerAiDiscDeckId").GetInt32());
                Assert.Equal(new[] { 3010001, 3010002, 3010003, 3010004 }, Enumerable.Range(1, 4)
                    .Select(slot => bot.GetProperty($"discId{slot}").GetInt32()).ToArray());
            });
        }
        finally
        {
            BattleMatchmakingService.GymEnabled = previousGym;
        }
    }

    private static async Task<List<JsonElement>> FinalRoster(BattleMatchmakingService service, string ticket)
    {
        using var cancellation = new CancellationTokenSource();
        var writer = new CollectingWriter(expectedCount: 3, cancellation);
        var stream = service.StreamAssignmentsAsync(ticket, writer, cancellation.Token);
        await writer.WaitForExpectedWritesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        return Roster(writer.Responses[^1].Assignment);
    }

    [Fact]
    public async Task Eight_humans_start_at_once_with_no_bots_and_no_extra_room()
    {
        var service = CreateService();
        // Long enough that it cannot close on its own: the eighth join is what starts the match.
        service.MatchWindow = TimeSpan.FromSeconds(30);

        var cancellations = new List<CancellationTokenSource>();
        var writers = new List<CollectingWriter>();
        var streams = new List<Task>();

        for (var i = 0; i < 8; i++)
        {
            var userId = $"10000{i + 1:00}";
            var (_, ticket) = service.RegisterEntry(userId, $"Player {userId}", 1, 1, 1,
                [3010001, 3010002, 3010003, 3010004]);

            var cancellation = new CancellationTokenSource();
            cancellations.Add(cancellation);
            // Stage 1, one interim roster per later join (8 - index - 1), Stage 2, Stage 3.
            var writer = new CollectingWriter(expectedCount: 10 - i, cancellation);
            writers.Add(writer);
            streams.Add(service.StreamAssignmentsAsync(ticket, writer, cancellation.Token));
        }

        // The eighth join starts the match on its own: nobody waits out the 30 s window.
        await Task.WhenAll(writers.Select(writer => writer.WaitForExpectedWritesAsync()));
        await Task.WhenAll(streams.Select(async stream =>
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream)));
        foreach (var cancellation in cancellations) cancellation.Dispose();

        var connection = writers[0].Responses[^1].Assignment.Connection;
        Assert.NotEmpty(connection);
        foreach (var writer in writers)
        {
            Assert.Equal(connection, writer.Responses[^1].Assignment.Connection);

            var roster = Roster(writer.Responses[^1].Assignment);
            Assert.Equal(8, roster.Count);
            Assert.All(roster, entry => Assert.Equal(0, entry.GetProperty("kickerAiParameterId").GetInt32()));
            Assert.Equal(4, roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 0));
            Assert.Equal(4, roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 1));
        }
    }

    [Fact]
    public async Task Cancel_removes_the_only_human_and_drops_the_room_so_the_next_entry_opens_a_new_one()
    {
        var service = CreateService();
        service.MatchWindow = TimeSpan.FromSeconds(30);

        var (entryId, ticket) = service.RegisterEntry("1000001", "Leaver", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        using var leaverCancellation = new CancellationTokenSource();
        var leaverWriter = new CollectingWriter(expectedCount: 10, leaverCancellation);
        var leaverStream = service.StreamAssignmentsAsync(ticket, leaverWriter, leaverCancellation.Token);
        await leaverWriter.FirstWrite;

        Assert.True(service.CancelEntry(entryId));
        // The client waits for its GetAssignments call to finish after the cancel: the stream must end on its own,
        // cleanly and long before the 30 s window would have closed.
        await leaverStream.WaitAsync(TimeSpan.FromSeconds(2));

        // The dropped room is no longer pending: the next human waits alone in a fresh room instead of joining it.
        var (_, nextTicket) = service.RegisterEntry("1000002", "Next", 2, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        using var nextCancellation = new CancellationTokenSource();
        var nextWriter = new CollectingWriter(expectedCount: 10, nextCancellation);
        var nextStream = service.StreamAssignmentsAsync(nextTicket, nextWriter, nextCancellation.Token);
        await nextWriter.FirstWrite;
        Assert.Equal("1000002", Assert.Single(Roster(nextWriter.Responses[0].Assignment)).GetProperty("userId").GetString());
        Assert.Single(leaverWriter.Responses); // no interim roster reached the leaver

        nextCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextStream);
    }

    [Fact]
    public async Task Cancel_removes_one_human_and_the_rest_of_the_room_still_starts_without_them()
    {
        var service = CreateService();

        var (stayerEntry, stayerTicket) = service.RegisterEntry("1000001", "Stayer", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (leaverEntry, leaverTicket) = service.RegisterEntry("1000002", "Leaver", 2, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        using var stayerCancellation = new CancellationTokenSource();
        using var leaverCancellation = new CancellationTokenSource();
        // Stage 1, the leaver's join, Stage 2, Stage 3.
        var stayerWriter = new CollectingWriter(expectedCount: 4, stayerCancellation);
        var leaverWriter = new CollectingWriter(expectedCount: 10, leaverCancellation);

        var stayerStream = service.StreamAssignmentsAsync(stayerTicket, stayerWriter, stayerCancellation.Token);
        await stayerWriter.FirstWrite;
        var leaverStream = service.StreamAssignmentsAsync(leaverTicket, leaverWriter, leaverCancellation.Token);
        await leaverWriter.FirstWrite;

        Assert.True(service.CancelEntry(leaverEntry));
        await leaverStream.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.CancelEntry(leaverEntry)); // a repeated back press is harmless
        Assert.False(service.CancelEntry("be-unknown"));

        await stayerWriter.WaitForExpectedWritesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stayerStream);
        var roster = Roster(stayerWriter.Responses[^1].Assignment);
        Assert.Equal(8, roster.Count);
        Assert.Equal("1000001", Assert.Single(roster, entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0)
            .GetProperty("userId").GetString());

        // The cancelled stream ended without an assignment: no re-queue, no room of its own.
        Assert.All(leaverWriter.Responses, response => Assert.Empty(response.Assignment.Connection));
        Assert.False(service.CancelEntry(stayerEntry)); // the stayer is in a started battle now
    }

    [Fact]
    public async Task A_stream_that_attaches_after_the_cancel_completes_at_once_without_writes()
    {
        var service = CreateService();
        service.MatchWindow = TimeSpan.FromSeconds(30);

        var (entryId, ticket) = service.RegisterEntry("1000001", "Early leaver", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        Assert.True(service.CancelEntry(entryId));

        using var cancellation = new CancellationTokenSource();
        var writer = new CollectingWriter(expectedCount: 10, cancellation);
        await service.StreamAssignmentsAsync(ticket, writer, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(writer.Responses);
    }

    [Fact]
    public async Task Cancel_ends_a_team_ticket_stream_still_waiting_for_the_host_entry()
    {
        var service = CreateService();
        var member = service.CreateTeam("1000001", "Host", 1, 1, 1, [3010001, 3010002, 3010003, 3010004], "2563");

        using var cancellation = new CancellationTokenSource();
        var writer = new CollectingWriter(expectedCount: 10, cancellation);
        var stream = service.StreamAssignmentsAsync(member.TicketId, writer, cancellation.Token);
        await Task.Delay(100);
        Assert.False(stream.IsCompleted); // parked until /battle/teamEntry

        Assert.True(service.CancelEntry(member.BattleEntryId));
        await stream.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(writer.Responses);
    }

    [Fact]
    public async Task Cancel_after_the_window_closed_is_a_no_op_and_the_assignment_still_replays()
    {
        var service = CreateService();

        var (entryId, ticket) = service.RegisterEntry("1000001", "Late", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        using var cancellation = new CancellationTokenSource();
        // More than Stage 1-3, so the stream is still holding its Stage 3 acknowledgement when the cancel arrives.
        var writer = new CollectingWriter(expectedCount: 4, cancellation);
        var stream = service.StreamAssignmentsAsync(ticket, writer, cancellation.Token);
        var final = await writer.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(service.CancelEntry(entryId));
        Assert.False(service.CancelEntry(entryId));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);

        using var replayCancellation = new CancellationTokenSource();
        var replayWriter = new CollectingWriter(expectedCount: 1, replayCancellation);
        var replayStream = service.StreamAssignmentsAsync(ticket, replayWriter, replayCancellation.Token);
        await replayWriter.WaitForExpectedWritesAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replayStream);
        Assert.Equal(final.Assignment.Connection, replayWriter.Responses[0].Assignment.Connection);
    }

    [Fact]
    public void Default_window_is_forty_seconds_and_first_join_increment_is_five()
    {
        var service = CreateService(fastWindow: false);

        if (Environment.GetEnvironmentVariable("KF_MATCH_WINDOW_SECONDS") is null)
        {
            Assert.Equal(40.0, service.MatchWindow.TotalSeconds, 3);
        }
        if (Environment.GetEnvironmentVariable("KF_MATCH_JOIN_INCREMENT_SECONDS") is null)
        {
            Assert.Equal(5.0, service.JoinIncrementBaseSeconds, 3);
        }
    }

    [Fact]
    public void Join_increments_are_5_then_half_a_second_less_per_human_for_64_5_total()
    {
        var service = CreateService(fastWindow: false);
        service.JoinIncrementBaseSeconds = 5.0;

        Assert.Equal(0, service.JoinIncrementSeconds(1), 3);
        double[] expected = [5.0, 4.5, 4.0, 3.5, 3.0, 2.5, 2.0];
        for (var humans = 2; humans <= 8; humans++)
        {
            Assert.Equal(expected[humans - 2], service.JoinIncrementSeconds(humans), 3);
        }

        var total = 40.0 + Enumerable.Range(2, 7).Sum(service.JoinIncrementSeconds);
        Assert.Equal(64.5, total, 3);

        service.JoinIncrementBaseSeconds = 0;
        Assert.Equal(0, Enumerable.Range(1, 8).Sum(service.JoinIncrementSeconds), 3);
    }

    [Theory]
    [InlineData("KF_MATCH_WINDOW_SECONDS", "7.5")]
    [InlineData("KF_MATCH_JOIN_INCREMENT_SECONDS", "3")]
    [InlineData("KF_MATCH_WINDOW_SECONDS", "0")]
    [InlineData("KF_MATCH_JOIN_INCREMENT_SECONDS", "0")]
    public void Environment_overrides_configure_window_and_increment(string variable, string value)
    {
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            var service = CreateService(fastWindow: false);
            var parsed = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            if (variable == "KF_MATCH_WINDOW_SECONDS")
            {
                Assert.Equal(parsed, service.MatchWindow.TotalSeconds, 3);
            }
            else
            {
                Assert.Equal(parsed, service.JoinIncrementBaseSeconds, 3);
                Assert.Equal(parsed == 0 ? 0 : parsed, service.JoinIncrementSeconds(2), 3);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Each_join_moves_the_deadline_and_the_window_task_honours_it()
    {
        var service = CreateService(fastWindow: false);
        service.MatchWindow = TimeSpan.FromSeconds(1);
        service.JoinIncrementBaseSeconds = 1.0; // second human +1 s, third +0.9 s

        var (_, ticket1) = service.RegisterEntry("1000001", "Player 0001", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (_, ticket2) = service.RegisterEntry("1000002", "Player 0002", 2, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        using var cancellation1 = new CancellationTokenSource();
        using var cancellation2 = new CancellationTokenSource();
        var writer1 = new CollectingWriter(expectedCount: 4, cancellation1);
        var writer2 = new CollectingWriter(expectedCount: 3, cancellation2);

        var stream1 = service.StreamAssignmentsAsync(ticket1, writer1, cancellation1.Token);
        await writer1.FirstWrite;
        await Task.Delay(300);
        var stream2 = service.StreamAssignmentsAsync(ticket2, writer2, cancellation2.Token);

        await Task.WhenAll(writer1.WaitForExpectedWritesAsync(), writer2.WaitForExpectedWritesAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream2);

        // Base 1 s + 1 s for the second human = ~2 s after the first entry, not the original 1 s.
        var fullRosterWrite = writer1.Responses
            .Select((response, index) => (response, index))
            .First(item => Roster(item.response.Assignment).Count == 8);
        var elapsed = writer1.WriteTimes[fullRosterWrite.index] - writer1.WriteTimes[0];
        Assert.InRange(elapsed.TotalSeconds, 1.8, 2.8);
    }

    [Fact]
    public async Task Expiry_is_the_real_utc_deadline_and_moves_out_with_each_join()
    {
        var service = CreateService(fastWindow: false);
        service.MatchWindow = TimeSpan.FromSeconds(1);
        service.JoinIncrementBaseSeconds = 1.0;

        var (_, ticket1) = service.RegisterEntry("1000001", "Player 0001", 1, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        var (_, ticket2) = service.RegisterEntry("1000002", "Player 0002", 2, 1, 1, [3010001, 3010002, 3010003, 3010004]);
        using var cancellation1 = new CancellationTokenSource();
        using var cancellation2 = new CancellationTokenSource();
        var writer1 = new CollectingWriter(expectedCount: 4, cancellation1);
        var writer2 = new CollectingWriter(expectedCount: 3, cancellation2);

        var opened = DateTimeOffset.UtcNow;
        var stream1 = service.StreamAssignmentsAsync(ticket1, writer1, cancellation1.Token);
        await writer1.FirstWrite;
        var stream2 = service.StreamAssignmentsAsync(ticket2, writer2, cancellation2.Token);

        await Task.WhenAll(writer1.WaitForExpectedWritesAsync(), writer2.WaitForExpectedWritesAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream2);

        // Stage 1, the interim roster after the join, Stage 2: base 1 s + 3 s margin, then +1 s for the join.
        var expiries = writer1.Responses.Take(3).Select(response => Expiry(response.Assignment)).ToList();
        Assert.All(expiries, expiry => Assert.Equal(TimeSpan.Zero, expiry.Offset));
        Assert.InRange((expiries[0] - opened).TotalSeconds, 3.0, 5.0);
        Assert.InRange((expiries[1] - expiries[0]).TotalSeconds, 0.5, 1.5);
        Assert.Equal(expiries[1], expiries[2]);
        Assert.True(expiries[2] < opened.AddMinutes(1), "the client keeps the latest expiry, so it must never be far-future");
    }

    [Fact]
    public async Task Complete_teams_stay_together_and_2_plus_2_plus_2_fills_to_four_per_side()
    {
        var roster = await RunPartyMatchAsync([2, 2, 2]);

        Assert.Equal(8, roster.Count);
        Assert.Equal(2, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));
        Assert.Equal(4, roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 0));
        Assert.Equal(4, roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 1));
        AssertEachPartyIsOnOneSide(roster, new[] { 2, 2, 2 });
    }

    [Fact]
    public async Task Three_player_and_two_player_teams_stay_together_and_bots_fill_the_open_slots()
    {
        var roster = await RunPartyMatchAsync([3, 2]);

        Assert.Equal(8, roster.Count);
        Assert.Equal(3, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));
        Assert.InRange(roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 0), 3, 4);
        Assert.InRange(roster.Count(entry => entry.GetProperty("teamType").GetInt32() == 1), 3, 4);
        AssertEachPartyIsOnOneSide(roster, new[] { 3, 2 });
    }

    [Fact]
    public async Task Unpartitionable_three_plus_three_plus_two_starts_oldest_six_and_requeues_last_team()
    {
        var service = CreateService();
        var parties = new[] { 3, 3, 2 };
        var tickets = new List<(string partyId, string userId, string ticket)>();
        var userNumber = 0;
        foreach (var (size, partyIndex) in parties.Select((size, index) => (size, index)))
        {
            var partyId = $"party-{partyIndex}";
            for (var member = 0; member < size; member++)
            {
                var userId = $"{++userNumber:0000000}";
                var (_, ticket) = service.RegisterEntry(userId, $"Player {userId}", 1, 1, 1,
                    [3010001, 3010002, 3010003, 3010004], partyId);
                tickets.Add((partyId, userId, ticket));
            }
        }

        var cancellations = tickets.Select(_ => new CancellationTokenSource()).ToList();
        var writers = cancellations.Select(cancellation => new CollectingWriter(100, cancellation)).ToList();
        var streams = new List<Task>();
        try
        {
            for (var i = 0; i < tickets.Count; i++)
            {
                var stream = service.StreamAssignmentsAsync(tickets[i].ticket, writers[i], cancellations[i].Token);
                streams.Add(stream);
                await writers[i].FirstWrite;
            }

            var finalAssignments = await Task.WhenAll(writers.Select(writer =>
                writer.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(12))));
            var firstBattle = finalAssignments[0].Assignment.Connection;
            var nextBattle = finalAssignments[6].Assignment.Connection;
            Assert.NotEmpty(firstBattle);
            Assert.NotEmpty(nextBattle);
            Assert.NotEqual(firstBattle, nextBattle);
            Assert.All(finalAssignments.Take(6), item => Assert.Equal(firstBattle, item.Assignment.Connection));
            Assert.All(finalAssignments.Skip(6), item => Assert.Equal(nextBattle, item.Assignment.Connection));

            var firstRoster = Roster(finalAssignments[0].Assignment);
            Assert.Equal(6, firstRoster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0));
            Assert.Equal(2, firstRoster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));
            AssertEachPartyIsOnOneSide(firstRoster, new[] { 3, 3 });

            var secondRoster = Roster(finalAssignments[6].Assignment);
            Assert.Equal(2, secondRoster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0));
            Assert.Equal(6, secondRoster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));
            AssertEachPartyIsOnOneSide(secondRoster, new[] { 2 }, startingUserNumber: 6);
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Cancel();
        }

        await Task.WhenAll(streams.Select(async stream =>
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream)));
        foreach (var cancellation in cancellations) cancellation.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Team_code_and_tickets_keep_membership_and_wait_for_host_entry(int battleRuleId)
    {
        var service = CreateService();
        var host = service.CreateTeam("1000001", "Host", 1, 110, battleRuleId,
            [3010001, 3010002, 3010003, 3010004], "2563");
        var guest = service.JoinTeam(host.MatchmakingTeamId, "1000002", "Guest", 2, 120,
            [3010001, 3010002, 3010003, 3010004]);

        Assert.NotNull(guest);
        Assert.NotEqual(host.TicketId, guest!.TicketId);
        Assert.NotEqual(host.BattleEntryId, guest.BattleEntryId);
        var recruiting = Assert.Single(service.GetTeams([host.MatchmakingTeamId]));
        Assert.Equal("2563", recruiting.Code);
        Assert.Equal(battleRuleId, recruiting.BattleRuleId);
        Assert.Equal(new[] { 110, 120 }, recruiting.KickerCostumeIdList);

        using var hostCancellation = new CancellationTokenSource();
        using var guestCancellation = new CancellationTokenSource();
        var hostWriter = new CollectingWriter(100, hostCancellation);
        var guestWriter = new CollectingWriter(100, guestCancellation);
        var hostStream = service.StreamAssignmentsAsync(host.TicketId, hostWriter, hostCancellation.Token);
        var guestStream = service.StreamAssignmentsAsync(guest.TicketId, guestWriter, guestCancellation.Token);

        await Task.Delay(150);
        Assert.False(hostWriter.FinalAssignment.IsCompleted);
        Assert.False(guestWriter.FinalAssignment.IsCompleted);
        Assert.False(service.StartTeam(host.MatchmakingTeamId, "1000999", ["1000001", "1000002"]));

        Assert.True(service.StartTeam(host.MatchmakingTeamId, "1000001", ["1000001", "1000002"]));
        var final = await Task.WhenAll(
            hostWriter.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8)),
            guestWriter.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(final[0].Assignment.Connection, final[1].Assignment.Connection);
        Assert.True(service.StartTeam(host.MatchmakingTeamId, "1000001", ["1000001", "1000002"]));

        var roster = Roster(final[0].Assignment);
        var humanTeams = roster
            .Where(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0)
            .ToDictionary(entry => entry.GetProperty("userId").GetString()!,
                entry => entry.GetProperty("teamType").GetInt32(), StringComparer.Ordinal);
        Assert.Equal(humanTeams["1000001"], humanTeams["1000002"]);

        hostCancellation.Cancel();
        guestCancellation.Cancel();
        await Task.WhenAll(
            Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostStream),
            Assert.ThrowsAnyAsync<OperationCanceledException>(() => guestStream));
    }

    [Fact]
    public async Task Recruiting_snapshots_are_safe_while_members_join()
    {
        var service = CreateService();
        var host = service.CreateTeam("1000001", "Host", 1, 110, 1,
            [3010001, 3010002, 3010003, 3010004], "2563");
        var joins = Enumerable.Range(2, 3).Select(number => Task.Run(() =>
            service.JoinTeam(host.MatchmakingTeamId, $"100000{number}", $"Player {number}", number,
                110 + number, [3010001, 3010002, 3010003, 3010004]))).ToArray();
        var polls = Enumerable.Range(0, 250).Select(_ => Task.Run(() =>
        {
            var snapshot = Assert.Single(service.GetTeams([host.MatchmakingTeamId]));
            Assert.InRange(snapshot.KickerCostumeIdList.Count, 1, 4);
            Assert.Equal("2563", snapshot.Code);
            return snapshot.KickerCostumeIdList.Count;
        })).ToArray();

        await Task.WhenAll(joins.Cast<Task>().Concat(polls));
        var final = Assert.Single(service.GetTeams([host.MatchmakingTeamId]));
        Assert.Equal(4, final.KickerCostumeIdList.Count);
    }

    [Fact]
    public async Task Team_stays_together_when_remaining_slots_fill_with_solos_and_bots()
    {
        var service = CreateService();
        var host = service.CreateTeam("1000001", "Host", 1, 110, 1,
            [3010001, 3010002, 3010003, 3010004], "2563");
        var guest = service.JoinTeam(host.MatchmakingTeamId, "1000002", "Guest", 2, 120,
            [3010001, 3010002, 3010003, 3010004]);
        Assert.NotNull(guest);
        Assert.True(service.StartTeam(host.MatchmakingTeamId, "1000001", ["1000001", "1000002"]));

        var entries = new List<(string UserId, string Ticket)>
        {
            ("1000001", host.TicketId),
            ("1000002", guest!.TicketId)
        };
        for (var index = 3; index <= 5; index++)
        {
            var userId = $"100000{index}";
            var (_, ticket) = service.RegisterEntry(userId, $"Solo {index}", index, 110 + index, 1,
                [3010001, 3010002, 3010003, 3010004]);
            entries.Add((userId, ticket));
        }

        var cancellations = entries.Select(_ => new CancellationTokenSource()).ToList();
        var writers = cancellations.Select(cancellation => new CollectingWriter(100, cancellation)).ToList();
        var streams = new List<Task>();
        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                streams.Add(service.StreamAssignmentsAsync(entries[index].Ticket, writers[index], cancellations[index].Token));
                await writers[index].FirstWrite;
            }

            var finals = await Task.WhenAll(writers.Select(writer =>
                writer.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8))));
            Assert.All(finals, item => Assert.Equal(finals[0].Assignment.Connection, item.Assignment.Connection));
            var roster = Roster(finals[0].Assignment);
            Assert.Equal(5, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0));
            Assert.Equal(3, roster.Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0));
            var humansById = roster.Where(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0)
                .ToDictionary(entry => entry.GetProperty("userId").GetString()!,
                    entry => entry.GetProperty("teamType").GetInt32(), StringComparer.Ordinal);
            Assert.Equal(humansById["1000001"], humansById["1000002"]);
            Assert.Equal(entries.Count, humansById.Count);
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Cancel();
            try
            {
                await Task.WhenAll(streams.Select(async stream =>
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream)));
            }
            finally
            {
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }
    }

    private static async Task<List<JsonElement>> RunPartyMatchAsync(int[] partySizes)
    {
        var service = CreateService();
        var tickets = new List<(string PartyId, string UserId, string Ticket)>();
        var index = 0;
        foreach (var (size, partyIndex) in partySizes.Select((size, partyIndex) => (size, partyIndex)))
        {
            var partyId = $"party-{partyIndex}";
            for (var member = 0; member < size; member++)
            {
                var userId = $"{++index:0000000}";
                var (_, ticket) = service.RegisterEntry(userId, $"Player {userId}", 1, 1, 1,
                    [3010001, 3010002, 3010003, 3010004], partyId);
                tickets.Add((partyId, userId, ticket));
            }
        }

        var cancellations = tickets.Select(_ => new CancellationTokenSource()).ToList();
        var writers = cancellations.Select(cancellation => new CollectingWriter(100, cancellation)).ToList();
        var streams = new List<Task>();
        try
        {
            for (var i = 0; i < tickets.Count; i++)
            {
                streams.Add(service.StreamAssignmentsAsync(tickets[i].Ticket, writers[i], cancellations[i].Token));
                await writers[i].FirstWrite;
            }

            var assignments = await Task.WhenAll(writers.Select(writer =>
                writer.FinalAssignment.WaitAsync(TimeSpan.FromSeconds(8))));
            Assert.All(assignments, item => Assert.Equal(assignments[0].Assignment.Connection, item.Assignment.Connection));
            return Roster(assignments[0].Assignment);
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Cancel();
            try
            {
                await Task.WhenAll(streams.Select(async stream =>
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream)));
            }
            finally
            {
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }
    }

    private static void AssertEachPartyIsOnOneSide(
        List<JsonElement> roster, int[] partySizes, int startingUserNumber = 0)
    {
        var humanTeams = roster
            .Where(entry => entry.GetProperty("kickerAiParameterId").GetInt32() == 0)
            .ToDictionary(entry => entry.GetProperty("userId").GetString()!,
                entry => entry.GetProperty("teamType").GetInt32(), StringComparer.Ordinal);
        var nextUser = startingUserNumber;
        foreach (var size in partySizes)
        {
            var teamTypes = Enumerable.Range(nextUser + 1, size)
                .Select(userNumber => humanTeams[$"{userNumber:0000000}"])
                .Distinct()
                .ToList();
            Assert.Single(teamTypes);
            nextUser += size;
        }
    }

    private static BattleMatchmakingService CreateService(bool fastWindow = true, bool withCatalog = false)
    {
        var photon = new PhotonServerManager(
            Options.Create(new PhotonServerOptions { Enabled = false }),
            NullLogger<PhotonServerManager>.Instance);
        var environment = withCatalog
            ? new TestWebHostEnvironment(RepositoryPaths.FindRoot(AppContext.BaseDirectory))
            : null;
        var service = new BattleMatchmakingService(NullLogger<BattleMatchmakingService>.Instance, photon, environment: environment);

        if (fastWindow)
        {
            // Still a real deadline, only short for the streaming tests; joins do not extend it here.
            service.MatchWindow = TimeSpan.FromSeconds(1);
            service.JoinIncrementBaseSeconds = 0;
        }

        return service;
    }

    private sealed class TestWebHostEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "KickFlight.BootstrapApi.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string ReadFirstRosterUser(Assignment assignment)
    {
        return Roster(assignment)[0].GetProperty("userId").GetString()!;
    }

    private static List<JsonElement> Roster(Assignment assignment)
    {
        var battleJson = assignment.Properties.Fields["battle"].StructValue.ToString();
        using var document = JsonDocument.Parse(battleJson);
        return document.RootElement.GetProperty("battlePlayerList")
            .EnumerateArray()
            .Select(entry => entry.Clone())
            .ToList();
    }

    private static DateTimeOffset Expiry(Assignment assignment)
    {
        var battleJson = assignment.Properties.Fields["battle"].StructValue.ToString();
        using var document = JsonDocument.Parse(battleJson);
        var raw = document.RootElement.GetProperty("matchmakingExpirationDatetime").GetString()!;
        // The client uses DateTimeOffset.TryParse with default styles.
        Assert.True(DateTimeOffset.TryParse(raw, out var expiry), $"unparseable expiry '{raw}'");
        return expiry;
    }

    private static int Bots(Assignment assignment)
    {
        return Roster(assignment).Count(entry => entry.GetProperty("kickerAiParameterId").GetInt32() > 0);
    }

    private sealed class CollectingWriter : IServerStreamWriter<GetAssignmentsResponse>
    {
        // A stage write that never arrives would otherwise hang the suite instead of failing it.
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

        private readonly int _expectedCount;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _expectedWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<GetAssignmentsResponse> _finalAssignment = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CollectingWriter(int expectedCount, CancellationTokenSource cancellation)
        {
            _expectedCount = expectedCount;
            _cancellation = cancellation;
        }

        public WriteOptions? WriteOptions { get; set; }
        public List<GetAssignmentsResponse> Responses { get; } = [];
        public List<DateTimeOffset> WriteTimes { get; } = [];
        public Task FirstWrite => _firstWrite.Task;
        public Task<GetAssignmentsResponse> FinalAssignment => _finalAssignment.Task;

        public async Task WaitForExpectedWritesAsync()
        {
            try
            {
                await _expectedWrites.Task.WaitAsync(WriteTimeout);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"only {Responses.Count} of {_expectedCount} expected writes arrived within {WriteTimeout}");
            }
        }

        public Task WriteAsync(GetAssignmentsResponse message)
        {
            Responses.Add(message);
            WriteTimes.Add(DateTimeOffset.UtcNow);
            _firstWrite.TrySetResult();
            if (!string.IsNullOrEmpty(message.Assignment.Connection))
            {
                _finalAssignment.TrySetResult(message);
            }

            if (Responses.Count >= _expectedCount)
            {
                _expectedWrites.TrySetResult();
                _cancellation.Cancel();
            }
            return Task.CompletedTask;
        }
    }
}
